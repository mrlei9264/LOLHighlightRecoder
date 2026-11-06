using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LolHighlight.Recording.Services;

public sealed record GameCaptureTarget(int ProcessId, long WindowHandle, CaptureRect Rect, bool CanCapture = true,
    long MonitorHandle = 0, CaptureRect MonitorRect = default);

public interface IGameWindowLocator
{
    GameCaptureTarget? FindLeagueWindow();
}

public sealed class GameWindowLocator : IGameWindowLocator
{
    private GameCaptureTarget? _lastTarget;
    public GameCaptureTarget? FindLeagueWindow()
    {
        // Enumerate HWNDs by PID instead of opening protected game processes to read MainWindowHandle.
        var pids = new HashSet<int>();
        foreach (var name in new[] { "League of Legends", "League of Legends (TM) Client" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) pids.Add(process.Id);
            }
        GameCaptureTarget? best = null;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var previous = _lastTarget?.WindowHandle == hwnd.ToInt64() && _lastTarget.ProcessId == pid ? _lastTarget : null;
            if (!pids.Contains((int)pid) || (!IsWindowVisible(hwnd) && previous == null))
                return true;
            GetClientRect(hwnd, out var rect);
            var minimized = IsIconic(hwnd);
            var width = minimized && previous != null ? previous.Rect.Width : rect.Right & ~1;
            var height = minimized && previous != null ? previous.Rect.Height : rect.Bottom & ~1;
            if ((width <= 0 || height <= 0) && previous != null) { width = previous.Rect.Width; height = previous.Rect.Height; }
            if (width <= 0 || height <= 0) return true;
            var monitor = MonitorFromWindow(hwnd, 2);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var monitorRect = GetMonitorInfo(monitor, ref monitorInfo) ? monitorInfo.Monitor : default;
            var target = new GameCaptureTarget((int)pid, hwnd.ToInt64(), new(0, 0, width, height),
                true, monitor.ToInt64(),
                new(monitorRect.Left, monitorRect.Top, (monitorRect.Right - monitorRect.Left) & ~1, (monitorRect.Bottom - monitorRect.Top) & ~1));
            if (best == null || (target.CanCapture && !best.CanCapture) ||
                (target.CanCapture == best.CanCapture && (long)width * height > (long)best.Rect.Width * best.Rect.Height))
                best = target;
            return true;
        }, IntPtr.Zero);
        _lastTarget = best;
        return best;
    }

    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}
