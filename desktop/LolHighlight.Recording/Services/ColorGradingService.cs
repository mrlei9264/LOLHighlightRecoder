using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

public interface IColorGradingService
{
    Task<ColorVideoInfo> ProbeAsync(string path, CancellationToken token = default);
    Task<string> RenderAsync(ColorGradeRequest request, IProgress<ColorGradeProgress>? progress = null, CancellationToken token = default);
}

/// <summary>Only changes video colors: no timeline, effects, mixing, speed changes or overlays.</summary>
public sealed class ColorGradingService(AppConfig config, AppPaths paths, IMetadataFooterService footer) : IColorGradingService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Ffmpeg => FfmpegExecutable.Resolve(config.FfmpegPath);
    private string Ffprobe => Path.Combine(Path.GetDirectoryName(Ffmpeg) ?? "", "ffprobe.exe");

    public async Task<ColorVideoInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        using var doc = JsonDocument.Parse(await RunAsync(Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", Path.GetFullPath(path)], token));
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new IOException("素材没有视频轨道。");
        var duration = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(duration) || duration <= 0) throw new IOException("素材时长无效。");
        return new(duration, video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(),
            streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").Select(s => s.GetProperty("codec_name").GetString() ?? "").ToArray());
    }

    public async Task<string> RenderAsync(ColorGradeRequest request, IProgress<ColorGradeProgress>? progress = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        string? partial = null;
        string? telemetry = null;
        string? lut = null;
        var ownsPartial = false;
        try
        {
            var tone = ColorTones.Get(request.ToneId);
            var table = ColorGradeLut.CreateRgb(tone, request.Strength);
            var source = Path.GetFullPath(request.SourcePath);
            var output = Path.GetFullPath(request.OutputPath);
            if (!File.Exists(source)) throw new FileNotFoundException("找不到源视频。", source);
            if (source.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择新的输出文件，不能覆盖原片。");
            partial = output + ".part.mp4";
            if (File.Exists(output) || File.Exists(partial)) throw new IOException("输出文件已存在，请更换文件名。");
            var info = await ProbeAsync(source, token);
            var duration = info.Duration;
            var drive = new DriveInfo(Path.GetPathRoot(output)!);
            if (drive.AvailableFreeSpace < Math.Max(64 * 1024 * 1024, new FileInfo(source).Length))
                throw new IOException("输出磁盘空间不足。");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (File.Open(partial, FileMode.CreateNew, FileAccess.Write)) { }
            ownsPartial = true;
            var runtime = paths.ResolveRuntimeDirectory(config.RuntimeDir);
            Directory.CreateDirectory(runtime);
            lut = Path.Combine(runtime, $"grade_{Guid.NewGuid():N}.cube");
            await File.WriteAllTextAsync(lut, ColorGradeLut.ToCube(table), token);
            var filter = $"lut3d=file={Path.GetFileName(lut)}:interp=trilinear";
            // Leave decoder, filter and encoder capacity for real-time recording.
            var args = new List<string> { "-hide_banner", "-v", "warning", "-nostdin", "-y", "-threads:v", "2", "-i", source,
                "-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0", "-map_chapters", "0", "-filter_threads", "2", "-vf", filter,
                "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-threads:v", "2" };
            args.AddRange(["-fps_mode", "passthrough", "-pix_fmt", "yuv420p"]);
            // Keep every original audio track. Only transcode formats unsuitable for MP4.
            var copyAudio = info.AudioCodecs.All(c => c is "aac" or "mp3" or "ac3" or "alac");
            args.AddRange(copyAudio ? ["-c:a", "copy"] : new[] { "-c:a", "aac", "-b:a", "192k" });
            args.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", partial]);
            await RunAsync(Ffmpeg, args, token, seconds => progress?.Report(new(Math.Clamp(seconds / duration * 94, 0, 94), "正在生成调色视频…")), runtime);
            {
                progress?.Report(new(96, "正在保留对局数据…"));
                var metadata = await Task.Run(() => footer.ReadMetadataFooter(source), token) ?? new Dictionary<string, object>();
                Directory.CreateDirectory(paths.ResolveRuntimeDirectory(config.RuntimeDir));
                telemetry = Path.Combine(paths.ResolveRuntimeDirectory(config.RuntimeDir), $"grade_{Guid.NewGuid():N}.ndjson");
                await using (var stream = new FileStream(telemetry, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await TelemetryArchive.ExtractAsync(source, stream, token);
                if (new FileInfo(telemetry).Length > 0)
                {
                    var archive = await TelemetryArchive.AppendAsync(partial, new(telemetry, DateTime.MinValue, DateTime.MaxValue), token);
                    metadata["telemetry_archive"] = new { format = "gzip-ndjson-uuid-v1", archive.Chunks, archive.Records, archive.Bytes };
                }
                else metadata.Remove("telemetry_archive");
                metadata["color_grade"] = new { tone = tone.Id, strength = request.Strength, source_file = Path.GetFileName(source), created_at = DateTime.UtcNow.ToString("O") };
                footer.AppendMetadataFooter(partial, metadata);
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, output, overwrite: false);
            progress?.Report(new(100, "调色视频已生成"));
            return output;
        }
        finally
        {
            if (ownsPartial && partial != null) try { File.Delete(partial); } catch (IOException) { }
            if (telemetry != null) try { File.Delete(telemetry); } catch (IOException) { }
            if (lut != null) try { File.Delete(lut); } catch (IOException) { }
            _gate.Release();
        }
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> args, CancellationToken token, Action<double>? progress = null, string? workingDirectory = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromHours(6));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (workingDirectory != null) start.WorkingDirectory = workingDirectory;
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动视频处理程序。");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var error = ProcessRunner.ReadBoundedTextAsync(process.StandardError);
        async Task<string> Read()
        {
            if (progress == null) return await ProcessRunner.ReadBoundedTextAsync(process.StandardOutput);
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var value)) progress(value / 1_000_000);
            return "";
        }
        var output = Read();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var diagnostic = await error;
            if (process.ExitCode != 0) throw new IOException("调色失败：" + diagnostic[^Math.Min(1800, diagnostic.Length)..]);
            return await output;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            await Task.WhenAll(output, error);
        }
    }
    public void Dispose() => _gate.Dispose();
}
