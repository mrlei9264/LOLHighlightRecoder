using System.Diagnostics;
using LolHighlight.Recording.Infrastructure;

namespace LolHighlight.Desktop.Tests;

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegTestTools.FindExecutable() == null)
            Skip = "FFmpeg unavailable. Set LHR_TEST_FFMPEG to the executable from the portable build.";
    }
}

public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (FfmpegTestTools.FindExecutable() == null)
            Skip = "FFmpeg unavailable. Set LHR_TEST_FFMPEG to the executable from the portable build.";
    }
}

internal static class FfmpegTestTools
{
    internal static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("LHR_TEST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var bundled = Path.Combine(directory.FullName, ".runtime", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(bundled)) return bundled;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), "ffmpeg.exe")).FirstOrDefault(File.Exists);
    }

    internal static async Task RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(FindExecutable() ?? throw new InvalidOperationException("FFmpeg unavailable"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(30), default);
        Assert.True(result.ExitCode == 0, result.Error);
    }
}
