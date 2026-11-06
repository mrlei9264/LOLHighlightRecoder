using System.Diagnostics;
using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Models;

namespace LolHighlight.Recording.Services;

/// <summary>Remuxes the live encoded TS stream to a fragmented MP4 with bounded memory.</summary>
public sealed class ContinuousRecording(AppConfig config, string outputPath) : IAsyncDisposable
{
    private Process? _process;
    private Task<string>? _errors;
    private long _written;
    private long _nextQuotaCheck;
    private readonly StorageQuotaManager _quota = new();
    private string PartialPath => outputPath + ".part.mp4";

    public void Start()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath) || File.Exists(PartialPath)) throw new IOException("整局录像文件已存在");
        CheckQuota();
        var info = new ProcessStartInfo(FfmpegExecutable.Resolve(config.FfmpegPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "warning", "-y", "-fflags", "+genpts",
                     "-f", "mpegts", "-i", "pipe:0", "-map", "0", "-c", "copy",
                     "-movflags", "+frag_keyframe+empty_moov+default_base_moof", PartialPath })
            info.ArgumentList.Add(arg);
        _process = Process.Start(info) ?? throw new IOException("无法启动整局录像封装器");
        _errors = ProcessRunner.ReadBoundedTextAsync(_process.StandardError);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_process == null) throw new InvalidOperationException("整局录像尚未启动");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _process.StandardInput.BaseStream.WriteAsync(data, timeout.Token);
        _written += data.Length;
        if (_written >= _nextQuotaCheck) CheckQuota();
    }

    private void CheckQuota()
    {
        // The current .part file is never eligible for quota deletion.
        var size = File.Exists(PartialPath) ? new FileInfo(PartialPath).Length : _written;
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        if (size + 16 * 1024 * 1024 > config.DiskQuotaGb * 1024L * 1024 * 1024)
            throw new IOException("当前整局录像接近容量上限，已停止写入；请提高录像空间上限");
        _quota.Enforce(directory, config.DiskQuotaGb, config.MinimumFreeGb, outputPath, size + 16 * 1024 * 1024);
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < config.MinimumFreeGb * 1024L * 1024 * 1024 + 16 * 1024 * 1024)
            throw new IOException("剩余磁盘空间不足，已停止整局录制");
        _nextQuotaCheck = _written + 8 * 1024 * 1024;
    }

    public async Task CompleteAsync()
    {
        if (_process == null) return;
        var process = _process;
        _process = null;
        try
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var errors = _errors == null ? "" : await _errors;
            if (process.ExitCode != 0 || !File.Exists(PartialPath) || new FileInfo(PartialPath).Length == 0)
                throw new IOException($"整局录像封装失败（保留 .part 文件）：{errors}");
            File.Move(PartialPath, outputPath);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            if (_errors != null) await _errors;
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await CompleteAsync();
}
