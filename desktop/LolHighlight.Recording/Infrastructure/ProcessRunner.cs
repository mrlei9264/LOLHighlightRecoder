using System.Diagnostics;
using System.Text;

namespace LolHighlight.Recording.Infrastructure;

internal static class ProcessRunner
{
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start process");
        var output = ReadBoundedTextAsync(process.StandardOutput);
        var error = ReadBoundedTextAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(output, error);
        }
    }

    // Keep a bounded tail: a malfunctioning subprocess must not exhaust application memory.
    internal static async Task<string> ReadBoundedTextAsync(TextReader reader, int maxChars = 256 * 1024)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            result.Append(buffer, 0, count);
            if (result.Length > maxChars) result.Remove(0, result.Length - maxChars);
        }
        return result.ToString();
    }
}
