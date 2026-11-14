namespace Meowser.Browser.Core.Navigation;

public interface IBrowserSurface
{
    event EventHandler? NavigationStateChanged;
    event EventHandler? NavigationStarted;
    event EventHandler<NavigationCompletedEventArgs>? NavigationCompleted;
    event EventHandler<BrowserPageTitleChangedEventArgs>? PageTitleChanged;
    event EventHandler<BrowserDownloadStartingEventArgs>? DownloadStarting;
    event EventHandler<BrowserDownloadStateChangedEventArgs>? DownloadStateChanged;
    event EventHandler<BrowserInitializationFailedEventArgs>? InitializationFailed;
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    Uri? CurrentAddress { get; }
    bool IsLoading { get; }
    void Navigate(Uri address);
    void GoBack();
    void GoForward();
    void Reload();
}

public sealed class NavigationCompletedEventArgs(bool isSuccess, string? errorDescription) : EventArgs
{
    public bool IsSuccess { get; } = isSuccess;
    public string? ErrorDescription { get; } = errorDescription;
}

public sealed class BrowserInitializationFailedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class BrowserPageTitleChangedEventArgs(string title) : EventArgs
{
    public string Title { get; } = title;
}

public sealed class BrowserDownloadStartingEventArgs(Uri address, string fileName, string filePath) : EventArgs
{
    public Uri Address { get; } = address;
    public string FileName { get; } = fileName;
    public string FilePath { get; set; } = filePath;
    public bool Cancel { get; set; }
}

public sealed class BrowserDownloadStateChangedEventArgs(string fileName, string filePath, string state) : EventArgs
{
    public string FileName { get; } = fileName;
    public string FilePath { get; } = filePath;
    public string State { get; } = state;
}
