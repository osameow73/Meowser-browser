namespace Meowser.Browser.WebView2;

public sealed record BrowserMediaInfo(
    string Title,
    string Artist,
    string Source,
    string Collection,
    string? ArtworkUrl,
    double CurrentTime,
    double Duration,
    double Volume,
    bool IsPlaying);

public sealed class BrowserMediaInfoChangedEventArgs(BrowserMediaInfo? info) : EventArgs
{
    public BrowserMediaInfo? Info { get; } = info;
}
