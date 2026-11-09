using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace LolHighlight.Desktop.Services;

internal sealed class LeagueGameLauncher(AppPaths paths)
{
    private const string WeGameLeagueUri = "wegame://action=StartFor&game_id=26";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\英雄联盟";
    private string SavedClientPath => Path.Combine(paths.ResolveRuntimeDirectory(".runtime"), "league-client-path.txt");

    internal static void StartWithWeGame()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(WeGameLeagueUri) { UseShellExecute = true });
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 1155)
        {
            throw new InvalidOperationException("找不到 WeGame 的启动入口。请安装或修复 WeGame 客户端。", error);
        }
    }

    internal string? FindClient()
    {
        try
        {
            if (File.Exists(SavedClientPath))
            {
                var saved = File.ReadAllText(SavedClientPath).Trim();
                if (IsClient(saved)) return saved;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallKey);
                var client = ClientIn(key?.GetValue("InstallLocation") as string)
                    ?? ClientIn(DirectoryOfUninstaller(key?.GetValue("UninstallString") as string));
                if (client != null) return client;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }

    internal static void StartClient(string path)
    {
        if (!IsClient(path))
            throw new IOException("请选择英雄联盟安装目录中的 TCLS\\Client.exe。");

        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        });
    }

    internal bool RememberClient(string path)
    {
        try
        {
            var destination = SavedClientPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsClient(string path) =>
        Path.IsPathFullyQualified(path) &&
        Path.GetFileName(path).Equals("Client.exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "TCLS", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private static string? ClientIn(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        directory = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(directory)) return null;
        var path = Path.Combine(directory, "TCLS", "Client.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? DirectoryOfUninstaller(string? command)
    {
        var end = command?.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) ?? -1;
        return end < 0 ? null : Path.GetDirectoryName(command![..(end + 4)].Trim().TrimStart('"'));
    }
}
