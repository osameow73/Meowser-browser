using Meowser.Data.Sqlite;

namespace Meowser.Data.Sqlite.Tests;

public sealed class BrowserDatabaseTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"meowser-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void Bookmark_CanBeAddedListedAndRemoved()
    {
        using var database = new BrowserDatabase(_databasePath);

        database.AddBookmark("Example", "https://example.com/");
        database.AddBookmark("Example", "https://example.com/");
        var bookmark = Assert.Single(database.GetBookmarks());
        Assert.Equal("Example", bookmark.Title);

        database.RemoveBookmark(bookmark.Id);
        Assert.Empty(database.GetBookmarks());
    }

    [Fact]
    public void History_CanBeRecordedAndCleared()
    {
        using var database = new BrowserDatabase(_databasePath);

        database.AddHistory("Example", "https://example.com/");

        Assert.Equal("https://example.com/", Assert.Single(database.GetHistory()).Url);
        database.ClearHistory();
        Assert.Empty(database.GetHistory());
    }

    [Fact]
    public void ThemeAndSettings_ArePersistent()
    {
        using (var database = new BrowserDatabase(_databasePath))
        {
            database.SaveTheme(new ThemeRecord("Midnight", "#FF111111", "#FF222222", "#FF222222", "#FF111111",
                "#FF333333", "#FFFFFFFF", "#FFCCCCCC", "#FF444444", "#FF222222", "#FF333333"));
            database.SetSetting("restoreTabs", "false");
        }

        using var reopened = new BrowserDatabase(_databasePath);
        Assert.Equal("Midnight", reopened.GetTheme()?.Name);
        Assert.Equal("false", reopened.GetSetting("restoreTabs"));
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
