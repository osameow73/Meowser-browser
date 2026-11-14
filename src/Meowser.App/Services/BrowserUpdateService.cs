using Velopack;
using Velopack.Sources;

namespace Meowser.App.Services;

public sealed class BrowserUpdateService
{
    public const string DefaultRepositoryUrl = "https://github.com/osameow73/Meowser-browser";

    private readonly UpdateManager _manager;

    public BrowserUpdateService(string repositoryUrl)
    {
        _manager = new UpdateManager(new GithubSource(repositoryUrl, null, false));
    }

    public bool IsInstalled => _manager.IsInstalled;

    public Task<UpdateInfo?> CheckAsync() => _manager.CheckForUpdatesAsync();

    public Task DownloadAsync(UpdateInfo update, Action<int>? progress = null) =>
        _manager.DownloadUpdatesAsync(update, progress);

    public void ApplyAndRestart(UpdateInfo update) => _manager.ApplyUpdatesAndRestart(update);

    public static bool TryNormalizeRepository(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts.Any(part => part is "." or "..")) return false;
        normalized = $"https://github.com/{parts[0]}/{parts[1]}";
        return true;
    }
}
