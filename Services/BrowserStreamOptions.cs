namespace WatchTogether.Services;

public sealed class BrowserStreamOptions
{
    public const string SectionName = "BrowserStream";

    public int ViewportWidth { get; init; } = 1366;

    public int ViewportHeight { get; init; } = 768;

    public int CaptureFps { get; init; } = 24;

    public int VideoBitrateKbps { get; init; } = 2500;

    public int AudioBitrateKbps { get; init; } = 128;

    public int NavigationTimeoutMs { get; init; } = 30000;

    public string XvfbPath { get; init; } = "Xvfb";

    public string PulseAudioPath { get; init; } = "pulseaudio";

    public string FfmpegPath { get; init; } = "ffmpeg";
}
