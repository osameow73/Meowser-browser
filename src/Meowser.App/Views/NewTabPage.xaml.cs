using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Meowser.Data.Sqlite;

namespace Meowser.App.Views;

public partial class NewTabPage : UserControl
{
    public event Action<string>? NavigateRequested;

    public NewTabPage(IReadOnlyList<FrequentlyVisitedRecord> sites)
    {
        InitializeComponent();
        var links = sites.Select(site => new QuickLink(site.Title, site.Url, GetHost(site.Url), GetMonogram(site.Title, site.Url))).ToList();
        FrequentSitesPanel.ItemsSource = links;
        EmptySitesMessage.Visibility = links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => QuickSearchBox.Focus();
    }

    private void QuickSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(QuickSearchBox.Text)) return;
        NavigateRequested?.Invoke(QuickSearchBox.Text);
        e.Handled = true;
    }

    private void FrequentSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickLink link }) NavigateRequested?.Invoke(link.Url);
    }

    private static string GetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase) : url;

    private static string GetMonogram(string title, string url)
    {
        var source = string.IsNullOrWhiteSpace(title) || title == "New Tab" ? GetHost(url) : title.Trim();
        return new string(source.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
    }

    private sealed record QuickLink(string Title, string Url, string Host, string Monogram);
}
