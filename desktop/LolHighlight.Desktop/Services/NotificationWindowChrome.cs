using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LolHighlight.Desktop.Services;

/// <summary>Removes native window chrome and keeps the HWND background alpha-clear.</summary>
internal sealed class NotificationWindowChrome : IDisposable
{
    private const nuint SubclassId = 0x52494654;
    private readonly SubclassProcedure _procedure;
    private nint _handle;

    public NotificationWindowChrome(nint handle)
    {
        _handle = handle;
        _procedure = WindowProcedure;

        // A transparent XAML brush alone does not clear the HWND's redirection surface.
        var style = GetWindowLongPtr(handle, -16).ToInt64();
        SetWindowLongPtr(handle, -16, (nint)((style & ~0x00CF0000L) | 0x80000000L)); // POPUP, no caption/frame
        var extended = GetWindowLongPtr(handle, -20).ToInt64();
        extended &= ~0x00020301L; // no modal, window, client, or static edge
        extended |= 0x082000A0L; // NOACTIVATE, NOREDIRECTIONBITMAP, TOOLWINDOW, TRANSPARENT
        SetWindowLongPtr(handle, -20, (nint)extended);
        ConfigureComposition(handle);
        if (!SetWindowSubclass(handle, _procedure, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置透明提示窗口");
        SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, NOMOVE, NOSIZE, NOZORDER, NOACTIVATE
        InvalidateRect(handle, 0, true);
    }

    private static void ConfigureComposition(nint handle)
    {
        // Disable native rounding/shadows and the Windows 11 one-pixel border.
        // These optional attributes are ignored on Windows 10.
        var corner = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        var border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(handle, ref margins));

        // Enable per-pixel alpha without blurring the desktop behind the notification.
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(handle, ref blur));
        }
        finally { DeleteObject(region); }
    }

    private nint WindowProcedure(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0014 && GetClientRect(handle, out var rect)) // WM_ERASEBKGND
        {
            // GDI black supplies zero alpha to the composed native background.
            // The independently composed XAML notification artwork remains opaque.
            FillRect((nint)wParam, ref rect, GetStockObject(4)); // BLACK_BRUSH, stock object: do not delete
            return 1;
        }
        if (message == 0x031E) // WM_DWMCOMPOSITIONCHANGED
        {
            try { ConfigureComposition(handle); }
            catch (Exception error)
            {
                LolHighlight.Recording.Infrastructure.RuntimeDiagnostics.Write(
                    new LolHighlight.Recording.Infrastructure.AppPaths().ResolveRuntimeDirectory(".runtime"),
                    $"提示窗口透明合成更新失败：{error}");
            }
        }
        if (message == 0x0082) // WM_NCDESTROY
        {
            RemoveWindowSubclass(handle, _procedure, SubclassId);
            _handle = 0;
        }
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        RemoveWindowSubclass(_handle, _procedure, SubclassId);
        _handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind { public uint Flags; public int Enable; public nint Region; public int TransitionOnMaximized; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint handle, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint handle, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint handle, ref BlurBehind blur);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint handle, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint handle, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint handle, uint message, nuint wParam, nint lParam);
}
