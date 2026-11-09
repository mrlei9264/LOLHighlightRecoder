using System.Runtime.InteropServices;

namespace LolHighlight.Desktop.Services;

internal static class RecycleBin
{
    private const uint Silent = 0x0004;
    private const uint NoConfirmation = 0x0010;
    private const uint NoErrorUi = 0x0400;
    private const uint NoRecursion = 0x1000;
    private const uint NoConnectedElements = 0x2000;
    private const uint RecycleOnDelete = 0x00080000;
    private const uint EarlyFailure = 0x00100000;
    private static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // Called on the WinUI STA after the application has confirmed the exact file selection.
    internal static void MoveFile(string path)
    {
        path = Path.GetFullPath(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException("录像路径已变为文件夹。");

        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ShellItemId, out var item));
        IFileOperation? operation = null;
        try
        {
            operation = (IFileOperation)new FileOperation();
            operation.SetOperationFlags(Silent | NoConfirmation | NoErrorUi | NoRecursion | NoConnectedElements |
                RecycleOnDelete | EarlyFailure);
            operation.DeleteItem(item, IntPtr.Zero);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || File.Exists(path))
                throw new IOException("Windows 未能将录像移入回收站。");
        }
        finally
        {
            if (operation != null) Marshal.ReleaseComObject(operation);
            Marshal.Release(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid iid, out IntPtr item);

    [ComImport, Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    private class FileOperation { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        // The declarations follow the Windows SDK vtable order.
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IntPtr item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
