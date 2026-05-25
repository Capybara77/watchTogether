namespace WatchTogether.Services;

public sealed class BrowserInput
{
    public string Type { get; init; } = "";

    public double X { get; init; }

    public double Y { get; init; }

    public double DeltaX { get; init; }

    public double DeltaY { get; init; }

    public string? Key { get; init; }

    public string? Text { get; init; }
}
