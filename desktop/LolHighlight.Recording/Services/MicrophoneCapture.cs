using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace LolHighlight.Recording.Services;

public interface IMicrophoneCapture : IDisposable
{
    Task StartAsync(Action<byte[]> onData);
    void Stop();
    bool IsRunning { get; }
}

public sealed class MicrophoneCapture : IMicrophoneCapture
{
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private Action<byte[]>? _onData;
    private bool _running;
    private AudioPcmConverter _converter = new();

    public bool IsRunning => Volatile.Read(ref _running);

    public Task StartAsync(Action<byte[]> onData)
    {
        Stop();
        _onData = onData;
        _converter = new AudioPcmConverter();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            _capture = new WasapiCapture(_device);
            _capture.DataAvailable += CaptureOnDataAvailable;
            _capture.RecordingStopped += CaptureOnRecordingStopped;
            Volatile.Write(ref _running, true);
            _capture.StartRecording();
        }
        catch { Stop(); throw; }
        return Task.CompletedTask;
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs args)
    {
        var capture = _capture;
        if (capture == null || args.BytesRecorded == 0)
            return;

        var converted = _converter.Convert(
            args.Buffer,
            args.BytesRecorded,
            capture.WaveFormat);
        if (converted.Length > 0)
            _onData?.Invoke(converted);
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        Volatile.Write(ref _running, false);
        if (args.Exception != null)
            _onData = null;
    }

    public void Stop()
    {
        var capture = _capture;
        var device = _device;
        _device = null;
        Volatile.Write(ref _running, false);
        _capture = null;
        _onData = null;
        if (capture == null)
        {
            device?.Dispose();
            return;
        }

        capture.DataAvailable -= CaptureOnDataAvailable;
        capture.RecordingStopped -= CaptureOnRecordingStopped;
        try { capture.StopRecording(); } catch { }
        try { capture.Dispose(); }
        finally { device?.Dispose(); }
    }

    public void Dispose() => Stop();
}
