using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Meowser.Browser.Core.Sessions;
using Meowser.Browser.WebView2;

namespace Meowser.App.ViewModels;

public sealed class BrowserTabViewModel : INotifyPropertyChanged
{
    private string _title;
    private Visibility _tabVisibility = Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    public BrowserTabState State { get; }
    public FrameworkElement Content { get; private set; }
    public WebView2BrowserSurface? BrowserSurface { get; private set; }
    public Visibility TabVisibility
    {
        get => _tabVisibility;
        private set { _tabVisibility = value; OnPropertyChanged(); }
    }
    public string Title
    {
        get => _title;
        private set { _title = value; OnPropertyChanged(); }
    }

    public BrowserTabViewModel(BrowserTabState state, FrameworkElement content)
    {
        State = state;
        Content = content;
        _title = state.Title;
        if (content is WebView2BrowserSurface surface) AttachBrowserSurface(surface);
    }

    public void SetBrowserSurface(WebView2BrowserSurface surface)
    {
        BrowserSurface = surface;
        Content = surface;
        OnPropertyChanged(nameof(BrowserSurface));
        OnPropertyChanged(nameof(Content));
        AttachBrowserSurface(surface);
    }

    public void HideFromTabStrip() => TabVisibility = Visibility.Collapsed;
    public void ShowInTabStrip() => TabVisibility = Visibility.Visible;

    private void AttachBrowserSurface(WebView2BrowserSurface surface)
    {
        BrowserSurface = surface;
        surface.PageTitleChanged += (_, args) => Title = string.IsNullOrWhiteSpace(args.Title) ? "New Tab" : args.Title;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
