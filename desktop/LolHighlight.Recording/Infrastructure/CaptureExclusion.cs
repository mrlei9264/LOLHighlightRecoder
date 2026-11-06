using System.Runtime.InteropServices;

namespace LolHighlight.Recording.Infrastructure;

public static class CaptureExclusion
{
    public static bool TryEnable(nint window) => SetWindowDisplayAffinity(window, 0x11);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
