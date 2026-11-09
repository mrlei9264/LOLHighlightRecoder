using System.ComponentModel;
using System.Diagnostics;

if (args.Length is < 4 or > 5 || !long.TryParse(args[0], out long totalBytes) || totalBytes <= 0)
{
    Console.Error.WriteLine("Expected archive size, output path, GitHub API URL, release URL and optional source.");
    return 2;
}

string destination = args[1];
bool interactive = !Console.IsOutputRedirected;
string source = args.Length == 5 && args[4].Length > 0
    ? args[4]
    : SelectSource(interactive && !Console.IsInputRedirected);
bool useMirror = source.Equals("cn", StringComparison.OrdinalIgnoreCase);
string name = useMirror ? "GH-Proxy (third-party)" : "GitHub (official)";
string url = useMirror ? $"https://gh-proxy.com/{args[3]}" : args[2];

var startInfo = new ProcessStartInfo("curl.exe")
{
    UseShellExecute = false,
    RedirectStandardError = true
};
// Keep local curl config headers and credentials off the third-party route.
if (useMirror)
    startInfo.ArgumentList.Add("--disable");
foreach (string argument in new[]
{
    "--fail", "--location", "--silent", "--show-error", "--connect-timeout", "15",
    "--speed-limit", "1", "--speed-time", "30", "--retry", "2",
    "--header", "Accept: application/octet-stream", "--output", destination, url
})
{
    startInfo.ArgumentList.Add(argument);
}

Console.WriteLine($"Downloading FFmpeg via {name}...");
using var curl = new Process { StartInfo = startInfo };
try
{
    curl.Start();
}
catch (Win32Exception error)
{
    Console.Error.WriteLine($"Could not start curl.exe. {error.Message}");
    return 1;
}

Task<string> errors = curl.StandardError.ReadToEndAsync();
Task transfer = curl.WaitForExitAsync();
var timer = Stopwatch.StartNew();
int barWidth = interactive ? Math.Clamp(Console.WindowWidth - 49, 0, 28) : 28;
long previousBytes = 0;
TimeSpan previousSample = TimeSpan.Zero;
TimeSpan nextLog = TimeSpan.FromSeconds(5);
while (await Task.WhenAny(transfer, Task.Delay(500)) != transfer)
{
    TimeSpan now = timer.Elapsed;
    long bytes = DownloadedBytes(destination);
    double speed = Math.Max(0, bytes - previousBytes) / (now - previousSample).TotalSeconds;
    if (interactive || now >= nextLog)
    {
        WriteProgress(bytes, totalBytes, speed, barWidth, interactive, finished: false);
        nextLog = now + TimeSpan.FromSeconds(5);
    }
    previousBytes = bytes;
    previousSample = now;
}

if (curl.ExitCode == 0)
{
    long bytes = DownloadedBytes(destination);
    WriteProgress(bytes, totalBytes, bytes / timer.Elapsed.TotalSeconds, barWidth, interactive, finished: true);
}
else if (interactive)
{
    Console.WriteLine();
}

Console.Error.Write(await errors);
return curl.ExitCode;

static string SelectSource(bool interactive)
{
    if (!interactive)
        return "global";

    Console.WriteLine("FFmpeg download source");
    Console.WriteLine("  [1] Global          GitHub (official)");
    Console.WriteLine("  [2] Mainland China  GH-Proxy (third-party)");
    while (true)
    {
        Console.Write("Select [1]: ");
        switch (Console.ReadLine()?.Trim())
        {
            case null or "" or "1": return "global";
            case "2": return "cn";
        }
    }
}

static long DownloadedBytes(string path)
{
    var file = new FileInfo(path);
    return file.Exists ? file.Length : 0;
}

static void WriteProgress(long bytes, long total, double speed, int barWidth, bool interactive, bool finished)
{
    const double MiB = 1024 * 1024;
    double fraction = Math.Min(1, (double)bytes / total);
    int filled = (int)(barWidth * fraction);
    string bar = barWidth > 0 ? $"[{new string('=', filled)}{new string('-', barWidth - filled)}]  " : "";
    string rate = speed >= MiB ? $"{speed / MiB:0.0} MiB/s" : $"{speed / 1024:0} KiB/s";
    string line = $"  {bar}{(int)(fraction * 100),3}%  {bytes / MiB,5:0.0}/{total / MiB:0.0} MiB  {rate,11}";
    if (interactive)
    {
        Console.Write('\r');
        Console.ForegroundColor = ConsoleColor.Cyan;
    }

    Console.Write(line);
    if (interactive)
        Console.ResetColor();
    if (finished)
        Console.WriteLine(" avg");
    else if (!interactive)
        Console.WriteLine();
}
