using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Meowser.Browser.Core.Navigation;

namespace Meowser.Browser.WebView2;

public partial class WebView2BrowserSurface : UserControl, IBrowserSurface
{
    public event EventHandler? NavigationStateChanged;
    public event EventHandler? NavigationStarted;
    public event EventHandler<NavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<BrowserPageTitleChangedEventArgs>? PageTitleChanged;
    public event EventHandler<BrowserDownloadStartingEventArgs>? DownloadStarting;
    public event EventHandler<BrowserDownloadStateChangedEventArgs>? DownloadStateChanged;
    public event EventHandler<BrowserInitializationFailedEventArgs>? InitializationFailed;
    public event EventHandler<BrowserMediaInfoChangedEventArgs>? MediaInfoChanged;

    private Task? _initializationTask;
    private Uri? _pendingAddress;
    private bool _isLoading;
    private readonly string? _userDataFolder;
    private readonly List<CoreWebView2Frame> _mediaFrames = [];
    private string? _defaultUserAgent;
    private DispatcherTimer? _mediaPollTimer;
    private BrowserMediaInfo? _lastMediaInfo;
    private bool _mediaPollInProgress;
    private bool _passwordManagerEnabled;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<CoreWebView2Environment>> Environments = new();

    public WebView2BrowserSurface(string? userDataFolder = null, bool passwordManagerEnabled = true)
    {
        _userDataFolder = userDataFolder;
        _passwordManagerEnabled = passwordManagerEnabled;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public bool CanGoBack => Browser.CoreWebView2?.CanGoBack ?? false;
    public bool CanGoForward => Browser.CoreWebView2?.CanGoForward ?? false;
    public Uri? CurrentAddress => Uri.TryCreate(Browser.Source?.ToString(), UriKind.Absolute, out var address) ? address : null;
    public bool IsLoading => _isLoading;

    public void Navigate(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _pendingAddress = address;
        if (Browser.CoreWebView2 is not null)
            NavigatePendingAddress();
        else if (IsLoaded)
            _ = EnsureInitializedAsync();
    }

    public void GoBack() { if (CanGoBack) Browser.GoBack(); }
    public void GoForward() { if (CanGoForward) Browser.GoForward(); }
    public void Reload() { if (Browser.CoreWebView2 is not null) Browser.Reload(); }
    public void Stop() => Browser.CoreWebView2?.Stop();

    public void SetPasswordManagerEnabled(bool enabled)
    {
        _passwordManagerEnabled = enabled;
        if (Browser.CoreWebView2 is not { } core) return;
        core.Settings.IsPasswordAutosaveEnabled = enabled;
    }

    public Task<bool> TogglePlaybackAsync() => ExecuteMediaCommandAsync("toggle");
    public Task<bool> SkipBackAsync() => ExecuteMediaCommandAsync("previous");
    public Task<bool> SkipForwardAsync() => ExecuteMediaCommandAsync("next");

    public Task<bool> SeekAsync(double seconds, double duration = 0) => ExecuteMediaCommandAsync(
        "seek:" + Math.Max(0, seconds).ToString("R", CultureInfo.InvariantCulture) + ":" +
        Math.Max(0, duration).ToString("R", CultureInfo.InvariantCulture));

    public async Task<bool> SetVolumeAsync(double volume)
    {
        var core = Browser.CoreWebView2;
        if (core is null) return false;
        var normalized = Math.Clamp(volume, 0, 1);
        core.IsMuted = normalized <= 0;
        var pageHandled = await ExecuteMediaCommandAsync(
            "volume:" + normalized.ToString("R", CultureInfo.InvariantCulture));
        // WebView2 exposes a browser-level mute switch; finer volume is provided by the
        // page's media elements when it has them.
        return pageHandled || normalized <= 0;
    }

    public async Task ClearPrivateProfileAsync()
    {
        if (Browser.CoreWebView2 is not null)
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await EnsureInitializedAsync();
    }

    private Task EnsureInitializedAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            CoreWebView2Environment? environment = null;
            if (_userDataFolder is not null)
                environment = await Environments.GetOrAdd(_userDataFolder,
                    CreatePrivateEnvironmentAsync);
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            _defaultUserAgent = core.Settings.UserAgent;
            core.Settings.IsPasswordAutosaveEnabled = _passwordManagerEnabled;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaSessionBridgeScript);
            core.FrameCreated += (_, args) => TrackMediaFrame(args.Frame);
            _mediaPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _mediaPollTimer.Tick += async (_, _) => await RefreshMediaInfoAsync();
            core.IsDocumentPlayingAudioChanged += (_, _) => OnDocumentAudioChanged(core);
            core.NavigationStarting += (_, args) =>
            {
                if (!args.IsRedirected) ClearMediaInfo();
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) &&
                    SetCompatibilityUserAgent(target, core))
                {
                    args.Cancel = true;
                    core.Navigate(args.Uri);
                    return;
                }
                _isLoading = true;
                NavigationStarted?.Invoke(this, EventArgs.Empty);
                NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            };
            core.NavigationCompleted += (_, args) =>
            {
                _isLoading = false;
                var description = args.IsSuccess ? null : $"The page could not be loaded ({args.WebErrorStatus}).";
                NavigationCompleted?.Invoke(this, new NavigationCompletedEventArgs(args.IsSuccess, description));
                NavigationStateChanged?.Invoke(this, EventArgs.Empty);
                if (args.IsSuccess && core.IsDocumentPlayingAudio) _ = RefreshMediaInfoAsync();
            };
            core.DocumentTitleChanged += (_, _) =>
                PageTitleChanged?.Invoke(this, new BrowserPageTitleChangedEventArgs(core.DocumentTitle));
            core.DownloadStarting += (_, args) =>
            {
                var operation = args.DownloadOperation;
                var fileName = Path.GetFileName(operation.ResultFilePath);
                var request = new BrowserDownloadStartingEventArgs(new Uri(operation.Uri), fileName, operation.ResultFilePath);
                DownloadStarting?.Invoke(this, request);
                if (request.Cancel)
                {
                    args.Cancel = true;
                    return;
                }
                if (!string.IsNullOrWhiteSpace(request.FilePath))
                    args.ResultFilePath = request.FilePath;
                args.Handled = true;

                void NotifyDownloadState() => DownloadStateChanged?.Invoke(this,
                    new BrowserDownloadStateChangedEventArgs(fileName, args.ResultFilePath, operation.State.ToString()));
                operation.StateChanged += (_, _) => NotifyDownloadState();
                NotifyDownloadState();
            };
            core.HistoryChanged += (_, _) => NavigationStateChanged?.Invoke(this, EventArgs.Empty);

            if (_pendingAddress is null)
                _pendingAddress = new Uri("https://www.google.com");
            NavigatePendingAddress();
        }
        catch (Exception)
        {
            _isLoading = false;
            InitializationFailed?.Invoke(this, new BrowserInitializationFailedEventArgs(
                "Meowser could not start its web engine. Install or repair the Microsoft Edge WebView2 Runtime, then restart Meowser."));
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NavigatePendingAddress()
    {
        if (_pendingAddress is not { } address || Browser.CoreWebView2 is null) return;
        _pendingAddress = null;
        SetCompatibilityUserAgent(address, Browser.CoreWebView2);
        Browser.CoreWebView2.Navigate(address.AbsoluteUri);
    }

    private void OnDocumentAudioChanged(CoreWebView2 core)
    {
        if (core.IsDocumentPlayingAudio)
        {
            _mediaPollTimer?.Start();
            _ = RefreshMediaInfoAsync();
        }
        else
        {
            _mediaPollTimer?.Stop();
            _ = RefreshMediaInfoAsync();
        }
    }

    private void TrackMediaFrame(CoreWebView2Frame frame)
    {
        if (_mediaFrames.Any(candidate => candidate.FrameId == frame.FrameId)) return;
        _mediaFrames.Add(frame);
        frame.FrameCreated += (_, args) => TrackMediaFrame(args.Frame);
        frame.Destroyed += (_, _) => _mediaFrames.RemoveAll(candidate => candidate.FrameId == frame.FrameId);
    }

    private async Task RefreshMediaInfoAsync()
    {
        var core = Browser.CoreWebView2;
        if (core is null || _mediaPollInProgress) return;
        _mediaPollInProgress = true;
        try
        {
            var snapshots = new List<MediaSnapshot>();
            var mainSnapshot = await ExecuteMediaInfoAsync(core.ExecuteScriptAsync);
            if (mainSnapshot is not null) snapshots.Add(mainSnapshot);
            foreach (var frame in _mediaFrames.ToArray())
            {
                try
                {
                    var frameSnapshot = await ExecuteMediaInfoAsync(frame.ExecuteScriptAsync);
                    if (frameSnapshot is not null) snapshots.Add(frameSnapshot);
                }
                catch (Exception) { }
            }
            var snapshot = snapshots.FirstOrDefault(candidate => candidate.IsPlaying) ??
                           snapshots.FirstOrDefault(candidate => candidate.HasMedia) ?? mainSnapshot;
            if (snapshot is null)
            {
                if (core.IsDocumentPlayingAudio && _lastMediaInfo is null)
                {
                    _lastMediaInfo = CreateFallbackMediaInfo(core, true);
                    MediaInfoChanged?.Invoke(this, new BrowserMediaInfoChangedEventArgs(_lastMediaInfo));
                }
                return;
            }

            if (snapshot.HasMedia || core.IsDocumentPlayingAudio || _lastMediaInfo is not null)
            {
                var address = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri : null;
                var source = address?.IsFile == true ? "Local Files" :
                    string.IsNullOrWhiteSpace(snapshot.Source) ? address?.Host ?? "Website" : snapshot.Source;
                var artwork = Uri.TryCreate(snapshot.ArtworkUrl, UriKind.Absolute, out var artworkUri) &&
                              artworkUri.Scheme is "http" or "https"
                    ? artworkUri.AbsoluteUri
                    : null;
                _lastMediaInfo = new BrowserMediaInfo(
                    string.IsNullOrWhiteSpace(snapshot.Title) ? core.DocumentTitle : snapshot.Title,
                    string.IsNullOrWhiteSpace(snapshot.Artist) ? "Unknown artist" : snapshot.Artist,
                    source,
                    snapshot.Collection ?? string.Empty,
                    artwork,
                    Math.Max(0, snapshot.CurrentTime),
                    Math.Max(0, snapshot.Duration),
                    core.IsMuted ? 0 : Math.Clamp(snapshot.Volume, 0, 1),
                    core.IsDocumentPlayingAudio || snapshot.IsPlaying);
                MediaInfoChanged?.Invoke(this, new BrowserMediaInfoChangedEventArgs(_lastMediaInfo));
            }
        }
        catch (Exception)
        {
            if (core.IsDocumentPlayingAudio && _lastMediaInfo is null)
            {
                _lastMediaInfo = CreateFallbackMediaInfo(core, true);
                MediaInfoChanged?.Invoke(this, new BrowserMediaInfoChangedEventArgs(_lastMediaInfo));
            }
        }
        finally
        {
            _mediaPollInProgress = false;
        }
    }

    private static async Task<MediaSnapshot?> ExecuteMediaInfoAsync(Func<string, Task<string>> executeScriptAsync)
    {
        var json = await executeScriptAsync(MediaInfoScript);
        return JsonSerializer.Deserialize<MediaSnapshot>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static BrowserMediaInfo CreateFallbackMediaInfo(CoreWebView2 core, bool isPlaying)
    {
        var address = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri : null;
        return new BrowserMediaInfo(
            string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Unknown track" : core.DocumentTitle,
            "Unknown artist",
            address?.IsFile == true ? "Local Files" : address?.Host ?? "Website",
            string.Empty,
            null,
            0,
            0,
            1,
            isPlaying);
    }

    private void ClearMediaInfo()
    {
        _mediaPollTimer?.Stop();
        if (_lastMediaInfo is null) return;
        _lastMediaInfo = null;
        MediaInfoChanged?.Invoke(this, new BrowserMediaInfoChangedEventArgs(null));
    }

    private async Task<bool> ExecuteMediaCommandAsync(string command)
    {
        var core = Browser.CoreWebView2;
        if (core is null) return false;
        var script = MediaCommandScript.Replace("__COMMAND__", JsonSerializer.Serialize(command), StringComparison.Ordinal);
        try
        {
            var result = await core.ExecuteScriptAsync(script);
            var succeeded = string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
            foreach (var frame in _mediaFrames.ToArray())
            {
                try
                {
                    var frameResult = await frame.ExecuteScriptAsync(script);
                    succeeded |= string.Equals(frameResult, "true", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception) { }
            }
            return succeeded;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Media command '{command}' failed: {exception}");
            return false;
        }
    }

    private const string MediaSessionBridgeScript = """
        (() => {
          if (window.__meowserMediaBridge) return;
          const session = navigator.mediaSession;
          const handlers = new Map();
          if (session && typeof session.setActionHandler === 'function') {
            const nativeSet = session.setActionHandler.bind(session);
            try {
              Object.defineProperty(session, 'setActionHandler', {
                configurable: true,
                value: (action, handler) => {
                  if (typeof handler === 'function') handlers.set(action, handler);
                  else handlers.delete(action);
                  return nativeSet(action, handler);
                }
              });
            } catch (_) { }
          }
          window.__meowserMediaBridge = {
            dispatch: (action, details = {}) => {
              const handler = handlers.get(action);
              if (!handler) return false;
              try { handler({ action, ...details }); return true; }
              catch (_) { return false; }
            }
          };
        })();
        """;

    private const string MediaInfoScript = """
        (() => {
          const deepQueryAll = selector => {
            const matches = [];
            const visit = root => {
              matches.push(...root.querySelectorAll(selector));
              for (const element of root.querySelectorAll('*')) if (element.shadowRoot) visit(element.shadowRoot);
            };
            visit(document);
            return matches;
          };
          const media = deepQueryAll('audio,video');
          const playing = media.find(item => !item.paused && !item.ended);
          const item = playing || media.find(item => item.readyState > 0 && !item.ended) || media[0];
          const metadata = navigator.mediaSession && navigator.mediaSession.metadata;
          const artwork = metadata && metadata.artwork
            ? [...metadata.artwork].sort((a, b) => (parseInt(b.sizes) || 0) - (parseInt(a.sizes) || 0))[0]
            : null;
          const meta = selector => document.querySelector(selector)?.content || '';
          const isSpotify = /(^|\.)spotify\.com$/i.test(location.hostname);
          const parseTimes = value => {
            const matches = String(value || '').match(/\b(?:\d{1,2}:)?\d{1,2}:\d{2}(?:\.\d+)?\b/g) || [];
            return matches.map(text => text.split(':').map(Number).reduce((seconds, part) => seconds * 60 + part, 0));
          };
          const spotifyTimeLabels = isSpotify ? deepQueryAll('.playback-bar__progress-time') : [];
          const spotifyPosition = isSpotify
            ? deepQueryAll('[data-testid="playback-position"]')[0] || spotifyTimeLabels[0]
            : null;
          const spotifyDuration = isSpotify
            ? deepQueryAll('[data-testid="playback-duration"]')[0] || spotifyTimeLabels[1]
            : null;
          const spotifyProgress = isSpotify
            ? deepQueryAll('[data-testid="playback-progressbar"], [data-testid="progress-bar"], [role="slider"][aria-valuetext]')[0]
            : null;
          const spotifyPlayer = isSpotify
            ? deepQueryAll('[data-testid="now-playing-bar"], [data-testid="footer-player"]')[0] || document.querySelector('footer') || document
            : null;
          const spotifyVisibleTimes = isSpotify
            ? [...spotifyPlayer.querySelectorAll('*')]
                .filter(element => element.children.length === 0)
                .map(element => parseTimes(element.innerText || element.textContent || '')[0])
                .filter(Number.isFinite)
            : [];
          const spotifyPositionTime = parseTimes([
            spotifyPosition?.getAttribute('aria-valuetext'), spotifyPosition?.getAttribute('aria-label'), spotifyPosition?.textContent,
          ].filter(Boolean).join(' '))[0];
          const spotifyProgressTimes = parseTimes([
            spotifyProgress?.getAttribute('aria-valuetext'), spotifyProgress?.getAttribute('aria-label')
          ].filter(Boolean).join(' '));
          const spotifyDurationTimes = parseTimes([
            spotifyDuration?.getAttribute('aria-valuetext'), spotifyDuration?.getAttribute('aria-label'), spotifyDuration?.textContent,
          ].filter(Boolean).join(' '));
          const spotifyCurrentTime = spotifyPositionTime ?? spotifyVisibleTimes[0] ?? spotifyProgressTimes[0];
          const spotifyTrackDuration = spotifyDurationTimes[spotifyDurationTimes.length - 1] ??
            spotifyVisibleTimes[1] ??
            (spotifyProgressTimes.length > 1 ? spotifyProgressTimes[spotifyProgressTimes.length - 1] : undefined);
          const siteName = meta('meta[property="og:site_name"]');
          const album = metadata?.album || meta('meta[property="music:album"]');
          return {
            hasMedia: Boolean(item || metadata),
            title: metadata?.title || item?.getAttribute('title') || meta('meta[property="og:title"]') || document.title || '',
            artist: metadata?.artist || meta('meta[property="music:musician"]') || meta('meta[name="author"]'),
            collection: album,
            source: siteName || location.hostname || '',
            artworkUrl: artwork?.src || meta('meta[property="og:image"]') || meta('meta[name="twitter:image"]'),
            currentTime: Number.isFinite(spotifyCurrentTime) ? spotifyCurrentTime : item && Number.isFinite(item.currentTime) ? item.currentTime : 0,
            duration: Number.isFinite(spotifyTrackDuration) ? spotifyTrackDuration : item && Number.isFinite(item.duration) ? item.duration : 0,
            volume: item ? (item.muted ? 0 : item.volume) : 1,
            isPlaying: Boolean(playing)
          };
        })()
        """;

    private const string MediaCommandScript = """
        (() => {
          const command = __COMMAND__;
          const deepQueryAll = selector => {
            const matches = [];
            const visit = root => {
              matches.push(...root.querySelectorAll(selector));
              for (const element of root.querySelectorAll('*')) if (element.shadowRoot) visit(element.shadowRoot);
            };
            visit(document);
            return matches;
          };
          const media = deepQueryAll('audio,video');
          const active = media.find(item => !item.paused && !item.ended) || media.find(item => !item.ended) || media[0];
          const isSpotify = /(^|\.)spotify\.com$/i.test(location.hostname);
          const bridge = window.__meowserMediaBridge;
          const dispatch = (name, details = {}) => bridge?.dispatch(name, details) || false;
          const buttons = deepQueryAll('button,[role="button"]');
          const buttonLabel = button => [button.getAttribute('aria-label'), button.getAttribute('title'),
            button.getAttribute('data-testid'), button.innerText].filter(Boolean).join(' ').toLowerCase();
          const clickButton = patterns => {
            const button = buttons.find(candidate => patterns.some(pattern => pattern.test(buttonLabel(candidate))));
            if (!button) return false;
            button.click();
            return true;
          };
          const clickSpotifyControl = (testId, labels) => {
            const control = deepQueryAll(`[data-testid="${testId}"]`)[0];
            if (control && !control.disabled && control.getAttribute('aria-disabled') !== 'true') {
              control.click();
              return true;
            }
            return clickButton(labels);
          };
          const setRange = (patterns, ratio) => {
            const controls = deepQueryAll('input[type="range"],[role="slider"]');
            const control = controls.find(candidate => patterns.some(pattern =>
              pattern.test([candidate.getAttribute('aria-label'), candidate.getAttribute('title'),
                candidate.getAttribute('data-testid'), candidate.getAttribute('name')].filter(Boolean).join(' ').toLowerCase())));
            if (!control) return false;
            const min = Number(control.min ?? control.getAttribute('aria-valuemin') ?? 0);
            const max = Number(control.max ?? control.getAttribute('aria-valuemax') ?? 100);
            const value = min + Math.max(0, Math.min(1, ratio)) * (max - min);
            if (control instanceof HTMLInputElement) {
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
              setter.call(control, String(value));
              control.dispatchEvent(new Event('input', { bubbles: true }));
              control.dispatchEvent(new Event('change', { bubbles: true }));
            } else {
              control.focus();
              const current = Number(control.getAttribute('aria-valuenow') || min);
              const key = ratio <= 0 ? 'Home' : ratio >= 1 ? 'End' : value < current ? 'ArrowLeft' : 'ArrowRight';
              const count = key === 'Home' || key === 'End' ? 1 : Math.min(100, Math.max(1, Math.round(Math.abs(value - current))));
              for (let index = 0; index < count; index++) {
                control.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
                control.dispatchEvent(new KeyboardEvent('keyup', { key, bubbles: true }));
              }
            }
            return true;
          };
          if (command === 'toggle') {
            if (isSpotify && clickSpotifyControl('control-button-playpause', [/play[-_ ]?pause/i, /^play\b/i, /^pause\b/i])) return true;
            const playing = (navigator.mediaSession?.playbackState === 'playing') || (active && !active.paused);
            const action = playing ? 'pause' : 'play';
            if (!dispatch(action)) {
              if (active) { if (playing) active.pause(); else active.play(); }
              else {
                const spotifyToggle = deepQueryAll('[data-testid="control-button-playpause"]')[0];
                if (spotifyToggle) spotifyToggle.click();
                else if (!clickButton(playing
                  ? [/^pause\b/i, /play[-_ ]?pause/i]
                  : [/^play\b/i, /play[-_ ]?pause/i])) return false;
              }
            }
            return true;
          }
          if (command === 'previous') {
            if (isSpotify && clickSpotifyControl('control-button-skip-back', [/skip[-_ ]?back/i, /^previous\b/i, /back track/i])) return true;
            if (!dispatch('previoustrack')) {
              if (active) active.currentTime = 0;
              else {
                const spotifyPrevious = deepQueryAll('[data-testid="control-button-skip-back"]')[0];
                if (spotifyPrevious) spotifyPrevious.click();
                else if (!clickButton([/skip[-_ ]?back/i, /^previous\b/i, /back track/i])) return false;
              }
            }
            return true;
          }
          if (command === 'next') {
            if (isSpotify && clickSpotifyControl('control-button-skip-forward', [/skip[-_ ]?forward/i, /^next\b/i, /next track/i, /next song/i])) return true;
            if (!dispatch('nexttrack')) {
              if (active && Number.isFinite(active.duration)) active.currentTime = Math.min(active.duration, active.currentTime + 10);
              else {
                const spotifyNext = deepQueryAll('[data-testid="control-button-skip-forward"]')[0];
                if (spotifyNext) spotifyNext.click();
                else if (!clickButton([/skip[-_ ]?forward/i, /^next\b/i, /next track/i, /next song/i])) return false;
              }
            }
            return true;
          }
          if (command.startsWith('seek:')) {
            const [secondsText, durationText] = command.slice(5).split(':');
            const seconds = Number(secondsText);
            const duration = Number(durationText);
            if (!Number.isFinite(seconds)) return false;
            if (isSpotify && setRange([/progress/i, /seek/i, /position/i], duration > 0 ? seconds / duration : 0)) return true;
            if (!dispatch('seekto', { seekTime: seconds, fastSeek: false })) {
              if (active) active.currentTime = Math.min(Number.isFinite(active.duration) ? active.duration : seconds, seconds);
              else if (!setRange([/progress/i, /seek/i, /position/i], duration > 0 ? seconds / duration : 0)) return false;
            }
            return true;
          }
          if (command.startsWith('volume:')) {
            const volume = Math.max(0, Math.min(1, Number(command.slice(7))));
            if (!Number.isFinite(volume)) return false;
            if (isSpotify && setRange([/volume/i, /sound/i], volume)) return true;
            if (media.length === 0) {
              if (setRange([/volume/i, /sound/i], volume)) return true;
              const spotifyVolume = deepQueryAll('[data-testid="volume-bar"] input[type="range"], [data-testid="volume-bar"]').at(-1);
              if (spotifyVolume) {
                const min = Number(spotifyVolume.min || 0);
                const max = Number(spotifyVolume.max || 100);
                if (spotifyVolume instanceof HTMLInputElement) {
                  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                  setter.call(spotifyVolume, String(min + volume * (max - min)));
                  spotifyVolume.dispatchEvent(new Event('input', { bubbles: true }));
                  spotifyVolume.dispatchEvent(new Event('change', { bubbles: true }));
                } else spotifyVolume.click();
                return true;
              }
              return false;
            }
            for (const item of media) { item.volume = volume; item.muted = volume === 0; }
            return true;
          }
          return false;
        })()
        """;

    private sealed record MediaSnapshot(bool HasMedia, string? Title, string? Artist, string? Source,
        string? Collection, string? ArtworkUrl, double CurrentTime, double Duration, double Volume, bool IsPlaying);

    private bool SetCompatibilityUserAgent(Uri address, CoreWebView2 core)
    {
        if (_defaultUserAgent is null) return false;
        var host = address.Host;
        var isPillowsSite = host.Equals("pillows.su", StringComparison.OrdinalIgnoreCase) ||
                            host.EndsWith(".pillows.su", StringComparison.OrdinalIgnoreCase);
        var desiredUserAgent = isPillowsSite
            ? string.Join(' ', _defaultUserAgent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.StartsWith("Edg/", StringComparison.OrdinalIgnoreCase)))
            : _defaultUserAgent;
        if (core.Settings.UserAgent.Equals(desiredUserAgent, StringComparison.Ordinal)) return false;
        core.Settings.UserAgent = desiredUserAgent;
        return true;
    }

    private static async Task<CoreWebView2Environment> CreatePrivateEnvironmentAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        return await CoreWebView2Environment.CreateAsync(null, folder);
    }
}
