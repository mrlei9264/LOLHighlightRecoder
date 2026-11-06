using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;

namespace LolHighlight.Recording.Services;

internal static class ProcessAudioActivation
{
    // AUDIOCLIENT_ACTIVATION_PARAMS, PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE.
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams { public int Type; public uint ProcessId; public int Mode; }

    // VT_BLOB PROPVARIANT on x64 (the application targets x64).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct BlobVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    internal static async Task<AudioClient> ActivateAsync(int processId)
    {
        var parameters = new ActivationParams { Type = 1, ProcessId = checked((uint)processId), Mode = 0 };
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var handler = new CompletionHandler();
        try
        {
            Marshal.StructureToPtr(parameters, data, false);
            Marshal.StructureToPtr(new BlobVariant { Type = 65, Size = (uint)Marshal.SizeOf<ActivationParams>(), Data = data }, variant, false);
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variant, handler, out operation));
            return new AudioClient((IAudioClient)await handler.Result.Task);
        }
        finally
        {
            GC.KeepAlive(handler);
            if (operation != null) Marshal.ReleaseComObject(operation);
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(data);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, IntPtr parameters,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public TaskCompletionSource<object> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out var hr, out var audioInterface);
                Marshal.ThrowExceptionForHR(hr);
                Result.TrySetResult(audioInterface);
            }
            catch (Exception error) { Result.TrySetException(error); }
        }
    }
}
