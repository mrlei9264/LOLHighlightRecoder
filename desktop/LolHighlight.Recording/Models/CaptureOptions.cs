namespace LolHighlight.Recording.Models;

public sealed record CaptureOptions(
    long WindowHandle,
    CaptureRect CaptureRect,
    string Encoder,
    string Preset,
    int Fps,
    string Bitrate,
    bool WithGameAudio,
    bool WithMicrophone,
    bool SeparateAudioTracks,
    string AudioBitrate,
    int AudioChannels,
    string CaptureMode = "window",
    long MonitorHandle = 0,
    string? FullGameOutputPath = null);

public readonly record struct CaptureRect(int X, int Y, int Width, int Height);
