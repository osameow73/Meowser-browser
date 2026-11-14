namespace Meowser.Browser.Core.Sessions;

public sealed record BrowserTabSnapshot(string? Address, string Title);
public sealed record BrowserSessionSnapshot(IReadOnlyList<BrowserTabSnapshot> Tabs, int ActiveIndex);

public sealed class BrowserTabState(Guid id, string title, Uri? address)
{
    public Guid Id { get; } = id;
    public string Title { get; internal set; } = title;
    public Uri? Address { get; internal set; } = address;
    public bool IsLoading { get; internal set; }
    public string? InitializationError { get; internal set; }
}

public sealed class BrowserTabManager
{
    private readonly List<BrowserTabState> _tabs = [];
    public IReadOnlyList<BrowserTabState> Tabs => _tabs;
    public BrowserTabState ActiveTab { get; private set; }
    public int ActiveIndex => _tabs.IndexOf(ActiveTab);

    public BrowserTabManager(BrowserSessionSnapshot? restored = null)
    {
        if (restored is not null)
        {
            foreach (var item in restored.Tabs)
            {
                var address = TryParseWebAddress(item.Address);
                _tabs.Add(new BrowserTabState(Guid.NewGuid(),
                    string.IsNullOrWhiteSpace(item.Title) ? "New Tab" : item.Title,
                    address));
            }
        }

        if (_tabs.Count == 0)
            _tabs.Add(CreateDefaultTab());

        var activeIndex = restored is null ? 0 : Math.Clamp(restored.ActiveIndex, 0, _tabs.Count - 1);
        ActiveTab = _tabs[activeIndex];
    }

    public BrowserTabState AddTab()
    {
        var tab = CreateDefaultTab();
        _tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    public bool Activate(Guid id)
    {
        var tab = _tabs.FirstOrDefault(candidate => candidate.Id == id);
        if (tab is null) return false;
        ActiveTab = tab;
        return true;
    }

    public bool Move(Guid id, int targetIndex)
    {
        var currentIndex = _tabs.FindIndex(candidate => candidate.Id == id);
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _tabs.Count) return false;
        if (currentIndex == targetIndex) return true;
        var tab = _tabs[currentIndex];
        _tabs.RemoveAt(currentIndex);
        _tabs.Insert(targetIndex, tab);
        return true;
    }

    public bool Close(Guid id)
    {
        var index = _tabs.FindIndex(candidate => candidate.Id == id);
        if (index < 0) return false;

        var wasActive = _tabs[index] == ActiveTab;
        _tabs.RemoveAt(index);
        if (_tabs.Count == 0)
        {
            ActiveTab = CreateDefaultTab();
            _tabs.Add(ActiveTab);
        }
        else if (wasActive)
        {
            ActiveTab = _tabs[Math.Min(index, _tabs.Count - 1)];
        }
        return true;
    }

    public void SetAddress(Guid id, Uri? address)
    {
        if (address is not null && address.Scheme is not ("http" or "https")) return;
        Find(id).Address = address;
    }

    public void SetTitle(Guid id, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title)) Find(id).Title = title.Trim();
    }

    public void SetLoading(Guid id, bool isLoading)
    {
        var tab = Find(id);
        tab.IsLoading = isLoading;
        if (isLoading) tab.InitializationError = null;
    }

    public void SetInitializationError(Guid id, string? message) => Find(id).InitializationError = message;

    public BrowserSessionSnapshot Capture() => new(
        _tabs.Select(tab => new BrowserTabSnapshot(tab.Address?.AbsoluteUri, tab.Title)).ToArray(),
        ActiveIndex);

    private BrowserTabState Find(Guid id) =>
        _tabs.FirstOrDefault(tab => tab.Id == id) ?? throw new ArgumentException("Unknown browser tab.", nameof(id));

    private static BrowserTabState CreateDefaultTab() => new(Guid.NewGuid(), "New Tab", null);

    private static Uri? TryParseWebAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
}
