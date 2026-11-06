using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using LolHighlight.Recording.Models;
using LolHighlight.Recording.Infrastructure;

namespace LolHighlight.Recording.Services;

public interface IFfmpegPipeline : IDisposable
{
    Task StartAsync(CaptureOptions options, CancellationToken cancellationToken = default);
    Task StopAsync();
    bool IsRunning { get; }
    string? LastError { get; }
    string? ActiveEncoder { get; }
    void WriteGameAudio(ReadOnlySpan<byte> data);
    void WriteMicrophone(ReadOnlySpan<byte> data);
}

public sealed class FfmpegPipeline : IFfmpegPipeline
{
    private ContinuousRecording? _continuous;
    private static readonly Dictionary<string, string[]> EncoderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> UsableEncoderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object EncoderCacheLock = new();

    private readonly IMemoryRingBuffer _ringBuffer;
    private readonly AppConfig _config;
    private readonly IRecordingClock _clock;
    private readonly RecordingPreviewTap? _preview;
    private readonly object _lock = new();
    private AudioPipeWriter? _gameAudioWriter;
    private AudioPipeWriter? _microphoneWriter;

    private Process? _process;
    private NamedPipeServerStream? _gameAudioPipe;
    private NamedPipeServerStream? _microphonePipe;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _pipeConnectionTask;
    private string? _lastError;
    private string? _activeEncoder;
    private string? _fatalError;
    private readonly StringBuilder _diagnostics = new();

    public bool IsRunning
    {
        get
        {
            lock (_lock)
                return _fatalError == null && _process != null && !_process.HasExited;
        }
    }

    public string? LastError
    {
        get { lock (_lock) return _fatalError ?? (_diagnostics.Length > 0 ? _diagnostics.ToString().Trim() : _lastError); }
    }

    public string? ActiveEncoder
    {
        get { lock (_lock) return _activeEncoder; }
    }

    public FfmpegPipeline(IMemoryRingBuffer ringBuffer, AppConfig config, IRecordingClock clock, RecordingPreviewTap? preview = null)
    {
        _ringBuffer = ringBuffer;
        _config = config;
        _clock = clock;
        _preview = preview;
    }

    // Read the current setting on every start.
    private string FfmpegPath => FfmpegExecutable.Resolve(_config.FfmpegPath);

    public async Task StartAsync(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        await StopAsync();

        ValidateCaptureTarget(options);
        await EnsureGraphicsCaptureAvailableAsync(cancellationToken);

        var encoder = await ResolveEncoderAsync(options.Encoder, cancellationToken);
        var gamePipeName = options.WithGameAudio ? CreatePipeName("game") : null;
        var microphonePipeName = options.WithMicrophone ? CreatePipeName("mic") : null;

        try
        {
            if (options.FullGameOutputPath != null)
            {
                _continuous = new ContinuousRecording(_config, options.FullGameOutputPath);
                _continuous.Start();
            }
            _gameAudioPipe = gamePipeName == null ? null : CreatePipe(gamePipeName);
            _microphonePipe = microphonePipeName == null ? null : CreatePipe(microphonePipeName);
            // Listener cancellation initiates graceful shutdown; it must not discard unread video packets.
            _cts = new CancellationTokenSource();
            _lastError = null;
            _fatalError = null;
            lock (_lock) _diagnostics.Clear();
            lock (_lock)
                _activeEncoder = encoder;

            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in BuildArguments(options, encoder, gamePipeName, microphonePipeName))
                startInfo.ArgumentList.Add(argument);

            cancellationToken.ThrowIfCancellationRequested();
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start FFmpeg process");

            var firstSegment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stdoutTask = ReadOutputStreamAsync(_process.StandardOutput.BaseStream, options.Fps, firstSegment, _cts.Token);
            _stderrTask = ReadErrorStreamAsync(_process.StandardError, _cts.Token);

            _gameAudioWriter = _gameAudioPipe == null ? null : new AudioPipeWriter(_gameAudioPipe, SetFatalError, _cts.Token);
            _microphoneWriter = _microphonePipe == null ? null : new AudioPipeWriter(_microphonePipe, SetFatalError, _cts.Token);
            _pipeConnectionTask = Task.WhenAll(new[] { _gameAudioWriter?.Completion, _microphoneWriter?.Completion }
                .OfType<Task>());

            // A running process is not proof of a working capture input. Wait for encoded video.
            var started = await Task.WhenAny(firstSegment.Task, _stdoutTask)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (started != firstSegment.Task || _process.HasExited)
            {
                await _stderrTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                throw new InvalidOperationException(LastError ?? "FFmpeg 未能输出有效的视频数据");
            }
        }
        catch
        {
            await StopAsync();
            throw;
        }
        finally
        {
            if (_process == null)
            {
                lock (_lock)
                    _activeEncoder = null;
            }
        }
    }

    internal IEnumerable<string> BuildArguments(
        CaptureOptions options,
        string encoder,
        string? gamePipeName,
        string? microphonePipeName)
    {
        ValidateCaptureTarget(options);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "lavfi", "-i",
            $"gfxcapture=hwnd={options.WindowHandle}:max_framerate={options.Fps}:dup_frames=1:capture_cursor=1:output_fmt=bgra:width={options.CaptureRect.Width}:height={options.CaptureRect.Height}"
        };

        if (gamePipeName != null)
        {
            args.AddRange(["-probesize", "32", "-analyzeduration", "0", "-f", "f32le", "-sample_rate", "48000", "-ch_layout", "stereo",
                "-i", PipePath(gamePipeName)]);
        }

        if (microphonePipeName != null)
        {
            args.AddRange(["-probesize", "32", "-analyzeduration", "0", "-f", "f32le", "-sample_rate", "48000", "-ch_layout", "stereo",
                "-i", PipePath(microphonePipeName)]);
        }

        args.AddRange(["-map", "0:v:0"]);
        var audioInputIndex = 1;
        var audioMaps = new List<string>();
        if (gamePipeName != null)
            audioMaps.Add($"{audioInputIndex++}:a:0");
        if (microphonePipeName != null)
            audioMaps.Add($"{audioInputIndex}:a:0");

        if (audioMaps.Count > 0 && !options.SeparateAudioTracks)
        {
            args.AddRange(["-filter_complex", $"[{string.Join("][", audioMaps)}]amix=inputs={audioMaps.Count}:duration=longest[outa]",
                "-map", "[outa]"]);
        }
        else
        {
            foreach (var map in audioMaps)
                args.AddRange(["-map", map]);
        }

        args.AddRange(["-vf", "hwdownload,format=bgra,format=yuv420p", "-c:v", encoder]);
        AddEncoderPreset(args, encoder, options.Preset);
        args.AddRange(["-b:v", options.Bitrate, "-maxrate", _config.MaxBitrate,
            "-bufsize", _config.BufferSize, "-g", (options.Fps * 2).ToString(), "-bf", "0",
            "-force_key_frames", "expr:gte(t,n_forced*2)"]);

        if (audioMaps.Count > 0)
            args.AddRange(["-c:a", "aac", "-b:a", options.AudioBitrate, "-ac", options.AudioChannels.ToString()]);

        args.AddRange(["-mpegts_start_pid", "256", "-mpegts_pmt_start_pid", "4096", "-mpegts_flags", "+resend_headers", "-flush_packets", "1", "-f", "mpegts", "pipe:1"]);
        return args;
    }

    private static void ValidateCaptureTarget(CaptureOptions options)
    {
        if (options.CaptureMode != "window" || options.WindowHandle == 0 ||
            options.CaptureRect.Width <= 0 || options.CaptureRect.Height <= 0)
            throw new InvalidOperationException("采集目标无效，请检查 LOL 游戏窗口。");
    }

    private async Task EnsureGraphicsCaptureAvailableAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(FfmpegPath);
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add("filter=gfxcapture");
        var result = await ProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(10), cancellationToken);
        if (result.ExitCode != 0 || !System.Text.RegularExpressions.Regex.IsMatch(result.Output + result.Error, @"\bdup_frames\b"))
            throw new InvalidOperationException("当前 FFmpeg 不支持后台游戏窗口持续采集，请使用随程序提供的版本。");
    }

    private static void AddEncoderPreset(List<string> args, string encoder, string requestedPreset)
    {
        if (encoder.Equals("h264_amf", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-quality", requestedPreset.Equals("p1", StringComparison.OrdinalIgnoreCase) ? "speed" : requestedPreset]);
        }
        else if (encoder.Equals("h264_qsv", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-preset", requestedPreset.Equals("p1", StringComparison.OrdinalIgnoreCase) ? "veryfast" : requestedPreset]);
        }
        else
        {
            args.AddRange(["-preset", requestedPreset.Equals("p1", StringComparison.OrdinalIgnoreCase) &&
                encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase) ? "ultrafast" : requestedPreset]);
        }
    }

    private async Task ReadOutputStreamAsync(Stream stream, int fps, TaskCompletionSource firstSegment,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var segmenter = new TransportStreamSegmenter(
            segment =>
            {
                _ringBuffer.PushSegment(segment.Index, segment.TimestampStart, segment.TimestampEnd, segment.Data);
                firstSegment.TrySetResult();
            },
            _config.SegmentSeconds, fps, Math.Min(64L * 1024 * 1024, _config.MemoryBufferMb * 1024L * 1024 / 2));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                if (_continuous != null)
                    await _continuous.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                segmenter.Add(buffer.AsSpan(0, count), _clock.NowSeconds);
                _preview?.Publish(buffer.AsMemory(0, count));
            }
            segmenter.Complete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            SetError($"FFmpeg output read failed: {error.Message}");
            // A dead output consumer would otherwise leave FFmpeg blocked on a full stdout pipe.
            lock (_lock)
                if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task ReadErrorStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                    break;
                SetError(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError($"FFmpeg error stream failed: {exception.Message}");
        }
    }

    public void WriteGameAudio(ReadOnlySpan<byte> data) => _gameAudioWriter?.Write(data);

    public void WriteMicrophone(ReadOnlySpan<byte> data) => _microphoneWriter?.Write(data);

    internal static async Task WritePipeAsync(NamedPipeServerStream pipe, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        await pipe.WriteAsync(data, timeout.Token);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Process? process;
        Task? stdoutTask;
        Task? stderrTask;
        Task? pipeConnectionTask;
        AudioPipeWriter? gameWriter;
        AudioPipeWriter? microphoneWriter;

        lock (_lock)
        {
            cts = _cts;
            process = _process;
            stdoutTask = _stdoutTask;
            stderrTask = _stderrTask;
            pipeConnectionTask = _pipeConnectionTask;
            _cts = null;
            _process = null;
            _stdoutTask = null;
            _stderrTask = null;
            _pipeConnectionTask = null;
            _activeEncoder = null;
            gameWriter = _gameAudioWriter;
            microphoneWriter = _microphoneWriter;
            _gameAudioWriter = null;
            _microphoneWriter = null;
        }

        gameWriter?.Complete();
        microphoneWriter?.Complete();

        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("q");
                        await process.StandardInput.FlushAsync();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    catch
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }
            catch { }
        }

        cts?.Cancel();
        DisposePipes();
        if (stdoutTask != null)
            await IgnoreCancellation(stdoutTask);
        if (stderrTask != null)
            await IgnoreCancellation(stderrTask);
        if (pipeConnectionTask != null)
            await IgnoreCancellation(pipeConnectionTask);
        process?.Dispose();
        cts?.Dispose();
        var continuous = _continuous;
        _continuous = null;
        if (continuous != null) await continuous.CompleteAsync();
    }

    private void DisposePipes()
    {
        lock (_lock)
        {
            _gameAudioPipe?.Dispose();
            _microphonePipe?.Dispose();
            _gameAudioPipe = null;
            _microphonePipe = null;
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private void SetError(string message)
    {
        lock (_lock)
        {
            _lastError = message;
            _diagnostics.AppendLine(message);
            if (_diagnostics.Length > 16 * 1024)
                _diagnostics.Remove(0, _diagnostics.Length - 16 * 1024);
        }
    }

    private void SetFatalError(string message)
    {
        lock (_lock)
        {
            _fatalError = message;
            _lastError = message;
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private async Task<string> ResolveEncoderAsync(string requested, CancellationToken cancellationToken)
    {
        var normalized = requested.Trim().ToLowerInvariant();
        if (normalized == "auto")
        {
            var available = await GetAvailableEncodersAsync(cancellationToken);
            lock (EncoderCacheLock)
            {
                if (UsableEncoderCache.TryGetValue(FfmpegPath, out var cached))
                    return cached;
            }

            foreach (var candidate in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" })
            {
                if (available.Contains(candidate, StringComparer.OrdinalIgnoreCase) && await CanUseEncoderAsync(candidate, cancellationToken))
                {
                    lock (EncoderCacheLock)
                        UsableEncoderCache[FfmpegPath] = candidate;
                    return candidate;
                }
            }

            throw new InvalidOperationException("FFmpeg has no usable H.264 encoder");
        }

        var encoder = normalized switch
        {
            "nvenc" or "h264_nvenc" => "h264_nvenc",
            "amf" or "h264_amf" => "h264_amf",
            "qsv" or "h264_qsv" => "h264_qsv",
            "x264" or "libx264" => "libx264",
            _ => throw new ArgumentException($"Unsupported encoder: {requested}")
        };

        if (!(await GetAvailableEncodersAsync(cancellationToken)).Contains(encoder, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"FFmpeg encoder '{encoder}' is not available");
        return encoder;
    }

    private async Task<bool> CanUseEncoderAsync(string encoder, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            }
        };

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "color=c=black:s=1280x720:r=1", "-frames:v", "1", "-an",
            "-c:v", encoder
        };
        AddEncoderPreset(arguments, encoder, "p1");
        arguments.AddRange(["-f", "null", "-"]);

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            var result = await ProcessRunner.RunAsync(process.StartInfo, TimeSpan.FromSeconds(5), cancellationToken);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private async Task<string[]> GetAvailableEncodersAsync(CancellationToken cancellationToken)
    {
        lock (EncoderCacheLock)
            if (EncoderCache.TryGetValue(FfmpegPath, out var cached)) return cached;
        var startInfo = new ProcessStartInfo(FfmpegPath);
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");
        var result = await ProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(5), cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Cannot query FFmpeg encoders: {result.Error}");
        var encoders = new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }
            .Where(encoder => result.Output.Contains(encoder, StringComparison.OrdinalIgnoreCase)).ToArray();
        lock (EncoderCacheLock) EncoderCache[FfmpegPath] = encoders;
        return encoders;
    }

    internal static NamedPipeServerStream CreatePipe(string name)
    {
        var pipe = new NamedPipeServerStream(
            name,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            1024 * 1024,
            1024 * 1024);
        // Named pipes do not support Stream.WriteTimeout; writes use cancellation.
        return pipe;
    }

    private static string CreatePipeName(string suffix) => $"rift_replay_{suffix}_{Guid.NewGuid():N}";
    private static string PipePath(string name) => $"\\\\.\\pipe\\{name}";

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; } catch { }
    }
}
