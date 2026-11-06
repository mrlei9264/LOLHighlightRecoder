using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LolHighlight.Desktop.Tests;

/// <summary>An owned, non-activating window used only by real WGC integration tests.</summary>
internal sealed class NativeCaptureWindow : IDisposable
{
    private readonly TaskCompletionSource<long> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private uint _threadId;
    private readonly uint _style;
    private readonly int _x, _y, _width, _height;

    internal Task<long> Handle => _ready.Task;

    internal NativeCaptureWindow(uint style = 0x90000001, int x = 24, int y = 24, int width = 320, int height = 180)
    {
        _style = style;
        _x = x; _y = y; _width = width; _height = height;
        _thread = new Thread(Run) { IsBackground = true, Name = "WGC test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        SetThreadDpiAwarenessContext(new nint(-4));
        _threadId = GetCurrentThreadId();
        var window = CreateWindowEx(0x08000080, "STATIC", "Rift Replay capture integration test",
            _style, _x, _y, _width, _height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            _ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }
        try
        {
            UpdateWindow(window);
            _ready.TrySetResult(window.ToInt64());
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally { DestroyWindow(window); }
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test window did not close");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Hwnd;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}
