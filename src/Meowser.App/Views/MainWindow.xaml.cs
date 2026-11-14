using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Meowser.App.Services;
using Meowser.App.ViewModels;
using Meowser.Browser.Core.Navigation;
using Meowser.Browser.Core.Sessions;
using Meowser.Browser.WebView2;
using Meowser.Data.Sqlite;

namespace Meowser.App.Views;

public partial class MainWindow : Window
{
    private readonly BrowserTabManager _tabManager;
    private readonly BrowserDatabase _database;
    private readonly bool _isPrivate;
    private readonly string? _privateUserDataFolder;
    private readonly ObservableCollection<BrowserTabViewModel> _tabs = [];
    private readonly Dictionary<string, long> _downloadIdsByPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _synchronizingSelection;
    private string? _currentSidebarKey;
    private TextBox? _themeName;
    private Point _tabDragStartPoint;
    private BrowserTabViewModel? _draggedTab;
    private Guid? _musicTabId;
    private BrowserMediaInfo? _musicInfo;
    private string? _musicArtworkUrl;
    private bool _updatingMusicUi;
    private bool _isSeekingMusic;
    private bool _musicWidgetDismissed;
    private bool _isFullscreen;
    private bool _sidebarWasVisibleBeforeFullscreen;
    private WindowState _windowStateBeforeFullscreen;
    private bool _privateCleanupStarted;
    private bool _privateCleanupCompleted;

    public MainWindow() : this(false) { }

    public MainWindow(bool isPrivate)
    {
        AppDiagnostics.Write($"MainWindow construction started. Private={isPrivate}.");
        _isPrivate = isPrivate;
        AppDiagnostics.Write("Opening browser database.");
        _database = new BrowserDatabase();
        AppDiagnostics.Write("Browser database opened.");
        _privateUserDataFolder = isPrivate
            ? Path.Combine(Path.GetTempPath(), $"Meowser.Private.{Guid.NewGuid():N}")
            : null;
        _tabManager = new BrowserTabManager();

        AppDiagnostics.Write("Loading WPF window markup.");
        InitializeComponent();
        AppDiagnostics.Write("WPF window markup loaded.");
        if (_isPrivate)
        {
            PrivateIndicator.Visibility = Visibility.Visible;
            Title = "Meowser — Private browsing";
            StatusText.Text = "Private browsing. This window does not save browsing history.";
        }

        BrowserTabs.ItemsSource = _tabs;
        foreach (var state in _tabManager.Tabs)
            _tabs.Add(CreateTabViewModel(state));
        AppDiagnostics.Write($"Created {_tabs.Count} initial browser tab(s).");

        _synchronizingSelection = true;
        BrowserTabs.SelectedIndex = _tabManager.ActiveIndex;
        _synchronizingSelection = false;
        ActiveTabContent.Content = ActiveTab?.Content;
        ApplyTheme(_database.GetTheme() ?? DefaultTheme);
        UpdateNavigationState();
        AppDiagnostics.Write("MainWindow construction completed.");
        Closing += Window_Closing;
        Closed += Window_Closed;
    }

    private static ThemeRecord DefaultTheme => BuiltInThemes[0];

    private void Window_Loaded(object sender, RoutedEventArgs e) => AppDiagnostics.Write("MainWindow loaded and visible.");
    private void Window_ContentRendered(object sender, EventArgs e) => AppDiagnostics.Write("MainWindow content rendered.");

    private void WindowDragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
        {
            chrome.CaptionHeight = _isFullscreen ? 0 : 42;
            chrome.ResizeBorderThickness = _isFullscreen || maximized ? new Thickness(0) : new Thickness(6);
        }
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Restore down" : "Maximize";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (_isFullscreen)
        {
            _windowStateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _sidebarWasVisibleBeforeFullscreen = SidebarPanel.Visibility == Visibility.Visible;
            WindowState = WindowState.Maximized;
            if (chrome is not null)
            {
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
            }
            BrowserChrome.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            SidebarRail.Visibility = Visibility.Collapsed;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            MainContentRegion.Margin = new Thickness(0);
        }
        else
        {
            if (chrome is not null)
            {
                chrome.CaptionHeight = 42;
                chrome.ResizeBorderThickness = new Thickness(6);
            }
            BrowserChrome.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            SidebarRail.Visibility = Visibility.Visible;
            SidebarPanel.Visibility = _sidebarWasVisibleBeforeFullscreen ? Visibility.Visible : Visibility.Collapsed;
            SidebarColumn.Width = _sidebarWasVisibleBeforeFullscreen ? new GridLength(330) : new GridLength(0);
            MainContentRegion.Margin = new Thickness(12, 8, 12, 12);
            WindowState = _windowStateBeforeFullscreen;
            if (chrome is not null)
                chrome.ResizeBorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(6);
        }
    }

    private BrowserTabViewModel CreateTabViewModel(BrowserTabState state)
    {
        if (state.Address is null)
        {
            IReadOnlyList<FrequentlyVisitedRecord> sites = _isPrivate ? [] : _database.GetFrequentlyVisited(8);
            if (!_isPrivate && sites.Count < 8)
            {
                var known = sites.Select(site => site.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var extra = _database.GetBookmarks().Where(bookmark => !known.Contains(bookmark.Url))
                    .Take(8 - sites.Count)
                    .Select(bookmark => new FrequentlyVisitedRecord(bookmark.Title, bookmark.Url, 0));
                sites = sites.Concat(extra).ToArray();
            }
            var startPage = new NewTabPage(sites);
            var startViewModel = new BrowserTabViewModel(state, startPage);
            startPage.NavigateRequested += input => NavigateInTab(startViewModel, AddressResolver.Resolve(input));
            return startViewModel;
        }

        var surface = CreateBrowserSurface();
        var viewModel = new BrowserTabViewModel(state, surface);
        AttachBrowserSurface(state, surface);
        surface.Navigate(state.Address);
        return viewModel;
    }

    private WebView2BrowserSurface CreateBrowserSurface()
    {
        var passwordSavingEnabled = !_isPrivate && _database.GetSetting("savePasswords") != "false";
        var userDataFolder = _privateUserDataFolder ?? GetDefaultBrowserUserDataFolder();
        return new WebView2BrowserSurface(userDataFolder, passwordSavingEnabled);
    }

    private string GetDefaultBrowserUserDataFolder()
    {
        const string profileSetting = "webview.userDataFolder";
        var savedFolder = _database.GetSetting(profileSetting);
        if (!string.IsNullOrWhiteSpace(savedFolder) && Directory.Exists(savedFolder))
            return savedFolder;

        var appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meowser");
        var sharedFolder = Path.Combine(appDataFolder, "WebView2");
        var previousDefaultFolder = (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Meowser.exe")) + ".WebView2";
        var binFolder = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.FullName;
        var targetFramework = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var debugProfileFolder = binFolder is null
            ? null
            : Path.Combine(binFolder, "Debug", targetFramework, "Meowser.exe.WebView2");

        // Keep the profile that this user already has when upgrading. Once selected,
        // store its path so Debug, Release, and later launches share the same cookies.
        var userDataFolder = new[] { debugProfileFolder, previousDefaultFolder }
            .FirstOrDefault(folder => folder is not null && Directory.Exists(folder)) ?? sharedFolder;
        Directory.CreateDirectory(userDataFolder);
        _database.SetSetting(profileSetting, userDataFolder);
        return userDataFolder;
    }

    private void AttachBrowserSurface(BrowserTabState state, WebView2BrowserSurface surface)
    {
        surface.NavigationStarted += (_, _) =>
        {
            _tabManager.SetLoading(state.Id, true);
            if (IsTabActive(state.Id))
            {
                LoadingIndicator.Visibility = Visibility.Visible;
                StatusText.Text = _isPrivate ? "Private browsing · Loading…" : "Loading…";
                BrowserUnavailablePanel.Visibility = Visibility.Collapsed;
            }
        };
        surface.NavigationCompleted += (_, args) =>
        {
            _tabManager.SetLoading(state.Id, false);
            var address = surface.CurrentAddress;
            _tabManager.SetAddress(state.Id, address);
            if (args.IsSuccess && !_isPrivate && address is not null)
                _database.AddHistory(state.Title, address.AbsoluteUri);
            if (IsTabActive(state.Id))
            {
                LoadingIndicator.Visibility = Visibility.Collapsed;
                StatusText.Text = _isPrivate ? "Private browsing · Ready" : args.IsSuccess ? "Ready" : args.ErrorDescription ?? "Page could not be loaded.";
                UpdateNavigationState();
                if (_currentSidebarKey == "History") ShowHistory();
            }
        };
        surface.NavigationStateChanged += (_, _) =>
        {
            _tabManager.SetAddress(state.Id, surface.CurrentAddress);
            if (IsTabActive(state.Id)) UpdateNavigationState();
        };
        surface.PageTitleChanged += (_, args) => _tabManager.SetTitle(state.Id, args.Title);
        surface.MediaInfoChanged += (_, args) => Dispatcher.BeginInvoke(new Action(() =>
            UpdateMusicWidget(state.Id, args.Info)));
        surface.DownloadStarting += (_, args) => OnDownloadStarting(args);
        surface.DownloadStateChanged += (_, args) => OnDownloadStateChanged(args);
        surface.InitializationFailed += (_, args) =>
        {
            AppDiagnostics.Write($"WebView2 initialization failed for tab {state.Id}: {args.Message}");
            _tabManager.SetInitializationError(state.Id, args.Message);
            if (!IsTabActive(state.Id)) return;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            StatusText.Text = args.Message;
            BrowserUnavailablePanel.Visibility = Visibility.Visible;
        };

    }

    private void UpdateMusicWidget(Guid tabId, BrowserMediaInfo? info)
    {
        if (info is null)
        {
            if (_musicTabId != tabId) return;
            var oldMusicTab = _tabs.FirstOrDefault(tab => tab.State.Id == tabId);
            oldMusicTab?.ShowInTabStrip();
            if (oldMusicTab is not null && ReferenceEquals(BackgroundMusicHost.Content, oldMusicTab.Content))
            {
                BackgroundMusicHost.Content = null;
                BackgroundMusicHost.Visibility = Visibility.Collapsed;
            }
            _musicTabId = null;
            _musicInfo = null;
            _musicWidgetDismissed = false;
            MusicWidget.Visibility = Visibility.Collapsed;
            NowPlayingButton.Visibility = Visibility.Collapsed;
            SidebarRail.Width = 174;
            MusicArtwork.Source = null;
            _musicArtworkUrl = null;
            return;
        }

        var trackChanged = _musicTabId != tabId || _musicInfo is null ||
            !string.Equals(_musicInfo.Title, info.Title, StringComparison.Ordinal) ||
            !string.Equals(_musicInfo.Artist, info.Artist, StringComparison.Ordinal);
        if (trackChanged) _musicWidgetDismissed = false;
        _musicTabId = tabId;
        _musicInfo = info;
        var musicTab = _tabs.FirstOrDefault(tab => tab.State.Id == tabId);
        if (musicTab is not null && ActiveTab?.State.Id != tabId)
        {
            if (ReferenceEquals(ActiveTabContent.Content, musicTab.Content)) ActiveTabContent.Content = null;
            BackgroundMusicHost.Content = musicTab.Content;
            BackgroundMusicHost.Visibility = Visibility.Hidden;
        }
        NowPlayingButton.Visibility = Visibility.Visible;
        MusicWidget.Visibility = _musicWidgetDismissed ? Visibility.Collapsed : Visibility.Visible;
        SidebarRail.Width = _musicWidgetDismissed ? 174 : 300;
        MusicSourceText.Text = FriendlyMusicSource(info.Source);
        MusicCollectionText.Text = info.Collection;
        MusicCollectionText.Visibility = string.IsNullOrWhiteSpace(info.Collection) ? Visibility.Collapsed : Visibility.Visible;
        MusicTrackText.Text = string.IsNullOrWhiteSpace(info.Title) ? "Unknown track" : info.Title;
        MusicArtistText.Text = string.IsNullOrWhiteSpace(info.Artist) ? "Unknown artist" : info.Artist;
        MusicPlayPauseButton.Content = info.IsPlaying ? "Ⅱ" : "▶";

        if (!string.Equals(_musicArtworkUrl, info.ArtworkUrl, StringComparison.Ordinal))
        {
            _musicArtworkUrl = info.ArtworkUrl;
            if (Uri.TryCreate(info.ArtworkUrl, UriKind.Absolute, out var artworkUri))
            {
                try { MusicArtwork.Source = new BitmapImage(artworkUri); }
                catch (Exception) { MusicArtwork.Source = null; }
            }
            else MusicArtwork.Source = null;
            MusicArtworkFallback.Visibility = MusicArtwork.Source is null ? Visibility.Visible : Visibility.Collapsed;
        }

        _updatingMusicUi = true;
        var duration = Math.Max(0, info.Duration);
        MusicPositionSlider.Maximum = duration > 0 ? duration : 1;
        MusicPositionSlider.Value = Math.Clamp(info.CurrentTime, 0, duration > 0 ? duration : 1);
        MusicPositionSlider.IsEnabled = duration > 0;
        MusicCurrentTimeText.Text = FormatMediaTime(info.CurrentTime);
        MusicVolumeSlider.Value = Math.Clamp(info.Volume, 0, 1);
        _updatingMusicUi = false;
    }

    private static string FriendlyMusicSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) return "Local Files";
        var host = source.Trim().ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.EndsWith("youtube.com", StringComparison.Ordinal) || host == "youtu.be") return "YouTube";
        if (host.EndsWith("spotify.com", StringComparison.Ordinal)) return "Spotify";
        if (host.EndsWith("samply.app", StringComparison.Ordinal)) return "Samply";
        if (host.EndsWith("unrlsd.app", StringComparison.Ordinal)) return "Unrlsd";
        return string.IsNullOrWhiteSpace(source) ? "Website" : source;
    }

    private static string FormatMediaTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private WebView2BrowserSurface? GetMusicSurface() => _musicTabId is { } id
        ? _tabs.FirstOrDefault(tab => tab.State.Id == id)?.BrowserSurface
        : null;

    private void DismissMusicWidget_Click(object sender, RoutedEventArgs e)
    {
        _musicWidgetDismissed = true;
        MusicWidget.Visibility = Visibility.Collapsed;
        SidebarRail.Width = 174;
    }

    private void ShowMusicWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_musicInfo is null) return;
        _musicWidgetDismissed = false;
        MusicWidget.Visibility = Visibility.Visible;
        SidebarRail.Width = 300;
    }

    private async void MusicPlayPause_Click(object sender, RoutedEventArgs e)
    {
        await RunMusicCommandAsync(surface => surface.TogglePlaybackAsync());
    }

    private async void MusicPrevious_Click(object sender, RoutedEventArgs e)
    {
        await RunMusicCommandAsync(surface => surface.SkipBackAsync());
    }

    private async void MusicNext_Click(object sender, RoutedEventArgs e)
    {
        await RunMusicCommandAsync(surface => surface.SkipForwardAsync());
    }

    private async Task RunMusicCommandAsync(Func<WebView2BrowserSurface, Task<bool>> command)
    {
        if (GetMusicSurface() is not { } surface)
        {
            StatusText.Text = "The music tab is no longer available.";
            return;
        }

        try
        {
            var succeeded = await command(surface);
            StatusText.Text = succeeded
                ? "Music control sent to the playing tab."
                : "This page does not expose a controllable audio or video player.";
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write($"Music control failed: {exception}");
            StatusText.Text = "This site did not respond to the music control.";
        }
    }

    private async void MusicPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingMusicUi || !_isSeekingMusic || GetMusicSurface() is not { } surface) return;
        MusicCurrentTimeText.Text = FormatMediaTime(e.NewValue);
        await surface.SeekAsync(e.NewValue, _musicInfo?.Duration ?? 0);
    }

    private void MusicPositionSlider_MouseDown(object sender, MouseButtonEventArgs e) => _isSeekingMusic = true;
    private void MusicPositionSlider_MouseUp(object sender, MouseButtonEventArgs e) => _isSeekingMusic = false;

    private async void MusicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingMusicUi || GetMusicSurface() is not { } surface) return;
        await surface.SetVolumeAsync(e.NewValue);
    }

    private void GoToMusicWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (_musicTabId is not { } id) return;
        var index = _tabs.ToList().FindIndex(tab => tab.State.Id == id);
        if (index < 0) return;
        _tabs[index].ShowInTabStrip();
        HideSidebar();
        _tabManager.Activate(id);
        BrowserTabs.SelectedIndex = index;
        DisplayTabContent(_tabs[index]);
        UpdateNavigationState();
        AddressBox.Focus();
    }

    private void NavigateInTab(BrowserTabViewModel tab, Uri address)
    {
        if (tab.BrowserSurface is null)
        {
            var surface = CreateBrowserSurface();
            tab.SetBrowserSurface(surface);
            AttachBrowserSurface(tab.State, surface);
        }
        _tabManager.SetAddress(tab.State.Id, address);
        if (IsTabActive(tab.State.Id))
        {
            ActiveTabContent.Content = tab.Content;
            BrowserUnavailablePanel.Visibility = Visibility.Collapsed;
            AddressBox.Text = address.AbsoluteUri;
        }
        tab.BrowserSurface!.Navigate(address);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var address = AddressResolver.Resolve(AddressBox.Text);
        AddressBox.Text = address.AbsoluteUri;
        if (ActiveTab is { } tab) NavigateInTab(tab, address);
        e.Handled = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ActiveSurface?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => ActiveSurface?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => ActiveSurface?.Reload();
    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab();

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BrowserTabViewModel tab }) return;
        CloseBrowserTab(tab);
    }

    private void CloseBrowserTab(BrowserTabViewModel tab)
    {
        // Keep the WebView alive when its tab is closed so background playback remains controllable.
        if (_musicTabId == tab.State.Id && _musicInfo is not null && tab.BrowserSurface is not null)
        {
            var wasActive = ActiveTab?.State.Id == tab.State.Id;
            tab.HideFromTabStrip();
            if (wasActive)
            {
                var next = _tabs.FirstOrDefault(candidate => candidate.State.Id != tab.State.Id &&
                    candidate.TabVisibility == Visibility.Visible);
                if (next is null)
                {
                    var state = _tabManager.AddTab();
                    next = CreateTabViewModel(state);
                    _tabs.Add(next);
                }
                _tabManager.Activate(next.State.Id);
                BrowserTabs.SelectedItem = next;
                DisplayTabContent(next);
                UpdateNavigationState();
            }
            StatusText.Text = "Music keeps playing in a hidden tab. Use Go To Website to return.";
            return;
        }

        if (_tabManager.Close(tab.State.Id)) SynchronizeTabs();
    }

    private void BrowserTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection || e.OriginalSource != BrowserTabs ||
            BrowserTabs.SelectedItem is not BrowserTabViewModel tab) return;
        _tabManager.Activate(tab.State.Id);
        DisplayTabContent(tab);
        UpdateNavigationState();
        LoadingIndicator.Visibility = tab.State.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = tab.State.InitializationError ?? (tab.State.IsLoading ? "Loading…" : _isPrivate ? "Private browsing · Ready" : "Ready");
        BrowserUnavailablePanel.Visibility = tab.State.InitializationError is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowserTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            _draggedTab = null;
            return;
        }
        var item = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        _draggedTab = item?.DataContext as BrowserTabViewModel;
        _tabDragStartPoint = e.GetPosition(BrowserTabs);
    }

    private void BrowserTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTab is null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(BrowserTabs);
        if (Math.Abs(position.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var dragged = _draggedTab;
        _draggedTab = null;
        DragDrop.DoDragDrop(BrowserTabs, dragged, DragDropEffects.Move);
    }

    private void BrowserTabs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BrowserTabViewModel)) is not BrowserTabViewModel dragged) return;
        var targetItem = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is not BrowserTabViewModel target) return;
        var from = _tabs.IndexOf(dragged);
        var to = _tabs.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        _tabs.Move(from, to);
        var managerTarget = _tabManager.Tabs.ToList().FindIndex(state => state.Id == target.State.Id);
        _tabManager.Move(dragged.State.Id, managerTarget);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && e.Key == Key.F11)
        {
            ToggleFullscreen(); e.Handled = true;
        }
        else if (_isFullscreen && e.Key == Key.Escape)
        {
            ToggleFullscreen(); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.T)
        {
            AddTab(); e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            OpenPrivateWindow_Click(this, new RoutedEventArgs()); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            BookmarkCurrent_Click(this, new RoutedEventArgs()); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            if (ActiveTab is { } tab) CloseBrowserTab(tab);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Left)
        {
            ActiveSurface?.GoBack(); e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Alt && e.Key == Key.Right)
        {
            ActiveSurface?.GoForward(); e.Handled = true;
        }
        else if ((modifiers == ModifierKeys.None && e.Key == Key.F5) || (modifiers == ModifierKeys.Control && e.Key == Key.R))
        {
            ActiveSurface?.Reload(); e.Handled = true;
        }
    }

    private void AddTab()
    {
        var state = _tabManager.AddTab();
        _tabs.Add(CreateTabViewModel(state));
        _synchronizingSelection = true;
        BrowserTabs.SelectedIndex = _tabManager.ActiveIndex;
        _synchronizingSelection = false;
        DisplayTabContent(ActiveTab);
        UpdateNavigationState();
    }

    private void SynchronizeTabs()
    {
        var validIds = _tabManager.Tabs.Select(tab => tab.Id).ToHashSet();
        if (_musicTabId is { } musicId && !validIds.Contains(musicId)) UpdateMusicWidget(musicId, null);
        foreach (var removed in _tabs.Where(tab => !validIds.Contains(tab.State.Id)).ToArray()) _tabs.Remove(removed);
        foreach (var state in _tabManager.Tabs.Where(state => _tabs.All(tab => tab.State.Id != state.Id)))
            _tabs.Add(CreateTabViewModel(state));
        _synchronizingSelection = true;
        BrowserTabs.SelectedIndex = _tabManager.ActiveIndex;
        _synchronizingSelection = false;
        DisplayTabContent(ActiveTab);
        UpdateNavigationState();
    }

    private void DisplayTabContent(BrowserTabViewModel? tab)
    {
        if (_musicInfo is not null && _musicTabId is { } musicId)
        {
            var musicTab = _tabs.FirstOrDefault(candidate => candidate.State.Id == musicId);
            if (musicTab is not null && tab?.State.Id != musicId)
            {
                if (ReferenceEquals(ActiveTabContent.Content, musicTab.Content)) ActiveTabContent.Content = null;
                BackgroundMusicHost.Content = musicTab.Content;
                BackgroundMusicHost.Visibility = Visibility.Hidden;
            }
            else if (musicTab is not null && ReferenceEquals(BackgroundMusicHost.Content, musicTab.Content))
            {
                BackgroundMusicHost.Content = null;
                BackgroundMusicHost.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            BackgroundMusicHost.Content = null;
            BackgroundMusicHost.Visibility = Visibility.Collapsed;
        }

        ActiveTabContent.Content = tab?.Content;
    }

    private void UpdateNavigationState()
    {
        var surface = ActiveSurface;
        BackButton.IsEnabled = surface?.CanGoBack ?? false;
        ForwardButton.IsEnabled = surface?.CanGoForward ?? false;
        AddressBox.Text = surface?.CurrentAddress?.AbsoluteUri ?? ActiveTab?.State.Address?.AbsoluteUri ?? string.Empty;
        BookmarkButton.Content = (surface?.CurrentAddress ?? ActiveTab?.State.Address) is { } address &&
            _database.GetBookmarks().Any(bookmark => bookmark.Url == address.AbsoluteUri) ? "★" : "☆";
    }

    private void OpenBookmarks_Click(object sender, RoutedEventArgs e) => ShowBookmarks();
    private void OpenHistory_Click(object sender, RoutedEventArgs e) => ShowHistory();
    private void OpenDownloads_Click(object sender, RoutedEventArgs e) => ShowDownloads();
    private void OpenThemes_Click(object sender, RoutedEventArgs e) => ShowThemes();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void CloseSidebar_Click(object sender, RoutedEventArgs e) => HideSidebar();

    private void OpenPrivateWindow_Click(object sender, RoutedEventArgs e)
    {
        var window = new MainWindow(isPrivate: true) { Owner = this };
        window.Show();
    }

    private void BookmarkCurrent_Click(object sender, RoutedEventArgs e)
    {
        if ((ActiveSurface?.CurrentAddress ?? ActiveTab?.State.Address) is not { } address) return;
        var existing = _database.GetBookmarks().FirstOrDefault(bookmark => bookmark.Url == address.AbsoluteUri);
        if (existing is null)
            _database.AddBookmark(ActiveTab?.State.Title ?? address.Host, address.AbsoluteUri);
        else
            _database.RemoveBookmark(existing.Id);
        UpdateNavigationState();
        if (_currentSidebarKey == "Bookmarks") ShowBookmarks();
    }

    private void ShowBookmarks()
    {
        OpenSidebar("Bookmarks", "Bookmarks");
        AddPanelButton("＋  Bookmark current page", BookmarkCurrent_Click);
        var records = _database.GetBookmarks();
        if (records.Count == 0) { AddEmptyState("Your saved pages will appear here."); return; }
        foreach (var bookmark in records)
            AddRecordRow(bookmark.Title, bookmark.Url, "Open", () => NavigateTo(bookmark.Url), "Remove", () =>
            {
                _database.RemoveBookmark(bookmark.Id);
                ShowBookmarks();
            });
    }

    private void ShowHistory()
    {
        OpenSidebar("History", "History");
        if (_isPrivate) { AddEmptyState("History is not saved in a private window."); return; }
        AddPanelButton("Clear browsing history", (_, _) =>
        {
            _database.ClearHistory();
            ShowHistory();
        });
        var records = _database.GetHistory();
        if (records.Count == 0) { AddEmptyState("Pages you visit will appear here."); return; }
        foreach (var item in records)
            AddRecordRow(item.Title, $"{item.VisitedAt.ToLocalTime():g} · {item.Url}", "Open", () => NavigateTo(item.Url));
    }

    private void ShowDownloads()
    {
        OpenSidebar("Downloads", "Downloads");
        if (_isPrivate) { AddEmptyState("Downloads from private windows are not kept in this list."); return; }
        var records = _database.GetDownloads();
        if (records.Count == 0) { AddEmptyState("Files you download will appear here."); return; }
        foreach (var item in records)
            AddRecordRow(item.FileName, $"{item.State} · {item.Path}", "Open file", () => OpenDownloadedFile(item.Path), "Show folder", () =>
            {
                if (File.Exists(item.Path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
            });
    }

    private void ShowSettings()
    {
        OpenSidebar("Settings", "Settings");
        if (_isPrivate)
        {
            AddSidebarText("Private windows use a temporary WebView2 profile. Settings and site data from this window are cleared when it closes.");
            return;
        }
        AddPanelText("Passwords and logins");
        var passwordSaving = new CheckBox
        {
            Content = "Offer to save website passwords",
            IsChecked = _database.GetSetting("savePasswords") != "false",
            Margin = new Thickness(4, 6, 4, 6)
        };
        passwordSaving.Checked += (_, _) => SetPasswordSavingEnabled(true);
        passwordSaving.Unchecked += (_, _) => SetPasswordSavingEnabled(false);
        SidebarContent.Children.Add(passwordSaving);
        AddSidebarText("WebView2 stores accepted logins in this browser profile and can fill them on later visits.");
        AddPanelText("Privacy");
        AddSidebarText(_isPrivate
            ? "This window uses a temporary WebView2 profile and does not write visits to Meowser history."
            : "Private windows keep cookies and site data in a separate temporary profile.");
        AddPanelText("Storage");
        AddSidebarText(_database.DatabasePath);
        AddPanelButton("Clear history", (_, _) => { _database.ClearHistory(); StatusText.Text = "Browsing history cleared."; });

        AddPanelText("Software updates");
        AddSidebarText("Updates are distributed through public GitHub Releases. Enter the repository once, then check here whenever you want.");
        var releasesRepository = AddInput("GitHub repository URL", _database.GetSetting("updates.githubRepo") ?? BrowserUpdateService.DefaultRepositoryUrl);
        AddPanelButton("Save update source", (_, _) =>
        {
            if (!BrowserUpdateService.TryNormalizeRepository(releasesRepository.Text, out var normalized))
            {
                StatusText.Text = "Enter a public GitHub repository URL, such as https://github.com/owner/repository.";
                return;
            }
            _database.SetSetting("updates.githubRepo", normalized);
            StatusText.Text = "Update source saved.";
        });
        AddPanelButton("Check for updates", async (_, _) => await CheckForUpdatesAsync());
    }

    private void SetPasswordSavingEnabled(bool enabled)
    {
        _database.SetSetting("savePasswords", enabled ? "true" : "false");
        foreach (var tab in _tabs)
            tab.BrowserSurface?.SetPasswordManagerEnabled(enabled);
        StatusText.Text = enabled ? "Password saving and autofill enabled." : "Password saving and autofill disabled.";
    }

    private async Task CheckForUpdatesAsync()
    {
        var repository = _database.GetSetting("updates.githubRepo") ?? BrowserUpdateService.DefaultRepositoryUrl;
        if (!BrowserUpdateService.TryNormalizeRepository(repository, out var normalized))
        {
            StatusText.Text = "Set and save a public GitHub repository URL in Settings first.";
            return;
        }

        try
        {
            var updates = new BrowserUpdateService(normalized);
            if (!updates.IsInstalled)
            {
                StatusText.Text = "Update checks are available in an installed copy of Meowser.";
                return;
            }

            StatusText.Text = "Checking for updates…";
            var update = await updates.CheckAsync();
            if (update is null)
            {
                StatusText.Text = "Meowser is up to date.";
                return;
            }

            var answer = MessageBox.Show("A Meowser update is available. Download and install it now? Meowser will restart when it is ready.",
                "Meowser update", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text = "Update postponed.";
                return;
            }

            StatusText.Text = "Downloading Meowser update…";
            await updates.DownloadAsync(update, progress => Dispatcher.Invoke(() => StatusText.Text = $"Downloading Meowser update… {progress}%"));
            updates.ApplyAndRestart(update);
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write($"Update check failed: {exception}");
            StatusText.Text = "Could not check for updates. Check the saved GitHub repository and your connection.";
        }
    }

    private void ShowThemes()
    {
        OpenSidebar("Themes", "Themes");
        AddSidebarText("Choose one of ten curated color palettes. Give a palette your own name to save it as your personal theme.");
        var activeTheme = _database.GetTheme() ?? DefaultTheme;
        _themeName = AddInput("Theme name", activeTheme.Name);
        foreach (var theme in BuiltInThemes) AddThemePalette(theme);
    }

    private void AddThemePalette(ThemeRecord theme)
    {
        var card = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(10, 8, 10, 8), ToolTip = $"Apply the {theme.Name} palette" };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = theme.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, 0, 0, 6) });
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var color in PaletteColors(theme))
            swatches.Children.Add(new Border
            {
                Width = 20,
                Height = 17,
                Margin = new Thickness(1),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(0.5)
            });
        content.Children.Add(swatches);
        card.Content = content;
        card.Click += (_, _) =>
        {
            var customName = _themeName?.Text.Trim();
            var selected = string.IsNullOrWhiteSpace(customName) ? theme : theme with { Name = customName };
            SaveAndApplyTheme(selected);
        };
        SidebarContent.Children.Add(card);
    }

    private static IEnumerable<string> PaletteColors(ThemeRecord theme)
    {
        yield return theme.Chrome;
        yield return theme.Background;
        yield return theme.Surface;
        yield return theme.Sidebar;
        yield return theme.Accent;
        yield return theme.Text;
        yield return theme.Muted;
        yield return theme.Border;
        yield return theme.Input;
        yield return theme.Hover;
    }

    private void AddPanelButton(string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 5), Padding = new Thickness(12, 9, 12, 9) };
        button.Click += handler;
        SidebarContent.Children.Add(button);
    }

    private void AddPanelText(string text)
    {
        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 12, 4, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        SidebarContent.Children.Add(label);
    }

    private void AddSidebarText(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 10) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        SidebarContent.Children.Add(label);
    }

    private TextBox AddInput(string label, string value)
    {
        AddSidebarText(label);
        var input = new TextBox { Text = value, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 1, 0, 5), BorderThickness = new Thickness(1) };
        input.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        input.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        SidebarContent.Children.Add(input);
        return input;
    }

    private void AddEmptyState(string message) => AddSidebarText(message);

    private void AddRecordRow(string title, string subtitle, string primaryText, Action primaryAction,
        string? secondaryText = null, Action? secondaryAction = null)
    {
        var card = new Border { CornerRadius = new CornerRadius(13), Padding = new Thickness(11), Margin = new Thickness(0, 4, 0, 5) };
        card.SetResourceReference(Border.BackgroundProperty, "PageBrush");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? "Untitled" : title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var details = new TextBlock { Text = subtitle, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
        details.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var primary = new Button { Content = primaryText, Padding = new Thickness(8, 4, 8, 4) };
        primary.Click += (_, _) => primaryAction();
        actions.Children.Add(primary);
        if (secondaryText is not null && secondaryAction is not null)
        {
            var secondary = new Button { Content = secondaryText, Padding = new Thickness(8, 4, 8, 4) };
            secondary.Click += (_, _) => secondaryAction();
            actions.Children.Add(secondary);
        }
        content.Children.Add(actions);
        card.Child = content;
        SidebarContent.Children.Add(card);
    }

    private void OpenSidebar(string key, string title)
    {
        _currentSidebarKey = key;
        SidebarTitle.Text = title;
        SidebarContent.Children.Clear();
        SidebarColumn.Width = new GridLength(330);
        SidebarPanel.Visibility = Visibility.Visible;
    }

    private void HideSidebar()
    {
        _currentSidebarKey = null;
        SidebarContent.Children.Clear();
        SidebarPanel.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);
    }

    private void NavigateTo(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            if (ActiveTab is { } tab) NavigateInTab(tab, uri);
    }

    private void OnDownloadStarting(BrowserDownloadStartingEventArgs args)
    {
        var dialog = new SaveFileDialog { FileName = args.FileName, Title = "Save download", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) { args.Cancel = true; return; }
        args.FilePath = dialog.FileName;
        if (!_isPrivate)
        {
            var id = _database.AddDownload(Path.GetFileName(args.FilePath), args.FilePath, args.Address.AbsoluteUri);
            _downloadIdsByPath[Path.GetFullPath(args.FilePath)] = id;
        }
    }

    private void OnDownloadStateChanged(BrowserDownloadStateChangedEventArgs args)
    {
        if (!_isPrivate && _downloadIdsByPath.TryGetValue(Path.GetFullPath(args.FilePath), out var id))
            _database.UpdateDownload(id, args.State);
        if (IsTabActive(ActiveTab?.State.Id ?? Guid.Empty))
            StatusText.Text = $"Download {args.State.ToLowerInvariant()}: {args.FileName}";
        if (_currentSidebarKey == "Downloads") ShowDownloads();
    }

    private static void OpenDownloadedFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void SaveAndApplyTheme(ThemeRecord theme)
    {
        _database.SaveTheme(theme);
        ApplyTheme(theme);
        StatusText.Text = $"Theme '{theme.Name}' applied.";
    }

    private void ApplyTheme(ThemeRecord theme)
    {
        SetBrushColor("ChromeBrush", theme.Chrome);
        SetBrushColor("PageBrush", theme.Background);
        SetBrushColor("SurfaceBrush", theme.Surface);
        SetBrushColor("SidebarBrush", theme.Sidebar);
        SetBrushColor("InputBrush", theme.Input);
        SetBrushColor("AccentBrush", theme.Accent);
        SetBrushColor("TextBrush", theme.Text);
        SetBrushColor("MutedBrush", theme.Muted);
        SetBrushColor("BorderBrush", theme.Border);
        SetBrushColor("HoverBrush", theme.Hover);
    }

    private void SetBrushColor(string key, string value)
    {
        Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
    }

    private static readonly ThemeRecord[] BuiltInThemes =
    [
        new("Cat Rose", "#FFF7F0F4", "#FFFCF9FB", "#FFFFFFFF", "#FFF5EDF2", "#FFB94E78", "#FF30232A", "#FF74646C", "#FFE8DCE2", "#FFFFFFFF", "#14B94E78"),
        new("Puppy Sky", "#FFF0F6FC", "#FFF8FBFE", "#FFFFFFFF", "#FFEDF4FA", "#FF3478AD", "#FF1D2D3A", "#FF687987", "#FFDCE6EF", "#FFFFFFFF", "#143478AD"),
        new("Midnight", "#FF171A23", "#FF0F1117", "#FF191D27", "#FF171A23", "#FFA78BFA", "#FFF8FAFF", "#FFB8C0D2", "#FF343A49", "#FF222736", "#26A78BFA"),
        new("Forest", "#FFEFF5F0", "#FFF8FBF8", "#FFFFFFFF", "#FFEDF4EE", "#FF3D805B", "#FF20342A", "#FF67796E", "#FFD9E6DC", "#FFFCFFFC", "#143D805B"),
        new("Ocean", "#FFEAF5F6", "#FFF6FBFC", "#FFFFFFFF", "#FFE8F2F4", "#FF187E86", "#FF193437", "#FF64787A", "#FFD5E5E6", "#FFFAFFFF", "#14187E86"),
        new("Lavender", "#FFF2EFFA", "#FFFAF9FE", "#FFFFFFFF", "#FFF0EDFA", "#FF7157B5", "#FF2B263B", "#FF726D83", "#FFE2DDF0", "#FFFFFEFF", "#147157B5"),
        new("Sunset", "#FFFFF3EC", "#FFFFFAF6", "#FFFFFFFF", "#FFFFF0E7", "#FFCC623C", "#FF392820", "#FF7C685E", "#FFEEDDD3", "#FFFFFEFC", "#14CC623C"),
        new("Mint", "#FFEDF7F3", "#FFF7FCF9", "#FFFFFFFF", "#FFEAF4EF", "#FF26866B", "#FF1E332D", "#FF657970", "#FFD7E8E0", "#FFFCFFFD", "#1426866B"),
        new("Berry", "#FFF8EFF4", "#FFFCF8FA", "#FFFFFFFF", "#FFF4EAF0", "#FFA63872", "#FF34232D", "#FF75636F", "#FFE8D8E2", "#FFFFFCFE", "#14A63872"),
        new("Slate Night", "#FF20242C", "#FF14171D", "#FF1B1F27", "#FF20242C", "#FF68B7C7", "#FFF4F7FA", "#FFB2BBC6", "#FF3A424D", "#FF252B35", "#2668B7C7")
    ];

    private bool IsTabActive(Guid id) => ActiveTab?.State.Id == id;
    private BrowserTabViewModel? ActiveTab => BrowserTabs.SelectedItem as BrowserTabViewModel;
    private WebView2BrowserSurface? ActiveSurface => ActiveTab?.BrowserSurface;

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isPrivate || _privateCleanupCompleted) return;
        e.Cancel = true;
        if (_privateCleanupStarted) return;
        _privateCleanupStarted = true;
        IsEnabled = false;
        StatusText.Text = "Clearing private browsing data…";
        foreach (var tab in _tabs) tab.BrowserSurface?.Stop();
        foreach (var tab in _tabs)
        {
            if (tab.BrowserSurface is not { } surface) continue;
            try { await surface.ClearPrivateProfileAsync(); }
            catch (Exception) { }
        }
        _privateCleanupCompleted = true;
        // Closing can run synchronously when WebView2 has no pending cleanup work.
        // Queue the second close so WPF has returned from the current Closing event.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(Close));
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _database.Dispose();
    }
}
