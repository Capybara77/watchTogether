namespace WatchTogether.Services;

public sealed class BrowserStreamOptions
{
    public const string SectionName = "BrowserStream";

    public int ViewportWidth { get; init; } = 1366;

    public int ViewportHeight { get; init; } = 768;

    public int CaptureFps { get; init; } = 8;

    public int JpegQuality { get; init; } = 65;

    public int NavigationTimeoutMs { get; init; } = 30000;
}
