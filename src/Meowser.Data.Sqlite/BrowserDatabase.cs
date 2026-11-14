using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Meowser.Data.Sqlite;

public sealed record BookmarkRecord(long Id, string Title, string Url, DateTimeOffset CreatedAt);
public sealed record HistoryRecord(long Id, string Title, string Url, DateTimeOffset VisitedAt);
public sealed record DownloadRecord(long Id, string FileName, string Path, string Url, DateTimeOffset StartedAt, string State);
public sealed record FrequentlyVisitedRecord(string Title, string Url, long Visits);
public sealed record ThemeRecord(string Name, string Chrome, string Background, string Surface, string Sidebar,
    string Accent, string Text, string Muted, string Border, string Input, string Hover);

public sealed class BrowserDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    public string DatabasePath { get; }

    public BrowserDatabase(string? databasePath = null)
    {
        if (databasePath is not null)
        {
            DatabasePath = databasePath;
            _connection = OpenAndInitialize(databasePath);
            return;
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meowser");
        Directory.CreateDirectory(directory);
        var activePath = Path.Combine(directory, "meowser.active.db");
        using var bootstrapMutex = new Mutex(false, @"Local\Meowser.BrowserDatabaseBootstrap");
        var ownsMutex = false;
        try
        {
            try { ownsMutex = bootstrapMutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new IOException("Timed out waiting for Meowser database initialization.");

            if (!File.Exists(activePath))
            {
                var primaryPath = Path.Combine(directory, "meowser.db");
                var recoveryPath = Path.Combine(directory, "meowser.recovery.db");
                var candidates = new[] { recoveryPath, primaryPath }
                    .Where(File.Exists)
                    .OrderByDescending(GetDatabaseSnapshotTime)
                    .ToArray();

                if (candidates.Length > 0)
                {
                    Exception? lastFailure = null;
                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            CreateStableSnapshot(candidate, activePath);
                            lastFailure = null;
                            break;
                        }
                        catch (Exception exception) when (exception is IOException or SqliteException)
                        {
                            lastFailure = exception;
                        }
                    }
                    if (lastFailure is not null) throw lastFailure;
                }
            }

            DatabasePath = activePath;
            _connection = OpenAndInitialize(activePath);
        }
        finally
        {
            if (ownsMutex) bootstrapMutex.ReleaseMutex();
        }
    }

    private static DateTime GetDatabaseSnapshotTime(string path)
    {
        var time = File.GetLastWriteTimeUtc(path);
        var walPath = path + "-wal";
        if (File.Exists(walPath) && File.GetLastWriteTimeUtc(walPath) > time)
            time = File.GetLastWriteTimeUtc(walPath);
        return time;
    }

    private static void CreateStableSnapshot(string sourcePath, string activePath)
    {
        var stagingPath = activePath + ".initializing";
        DeleteDatabaseFiles(stagingPath);
        try
        {
            File.Copy(sourcePath, stagingPath);
            var sourceWal = sourcePath + "-wal";
            if (File.Exists(sourceWal) && new FileInfo(sourceWal).Length > 0)
                File.Copy(sourceWal, stagingPath + "-wal");

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = stagingPath,
                DefaultTimeout = 15,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                using (var timeout = connection.CreateCommand())
                {
                    timeout.CommandText = "PRAGMA busy_timeout = 15000;";
                    timeout.ExecuteNonQuery();
                }
                using (var integrity = connection.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA integrity_check;";
                    var result = Convert.ToString(integrity.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new SqliteException($"The browser database snapshot failed its integrity check: {result}", 10);
                }
                using (var journal = connection.CreateCommand())
                {
                    journal.CommandText = "PRAGMA journal_mode = DELETE;";
                    var mode = Convert.ToString(journal.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                        throw new SqliteException($"Could not switch the browser database to rollback-journal mode: {mode}", 10);
                }
            }

            File.Move(stagingPath, activePath);
        }
        catch
        {
            DeleteDatabaseFiles(stagingPath);
            throw;
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + ".journal" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private static SqliteConnection OpenAndInitialize(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 15,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using (var busyTimeout = connection.CreateCommand())
            {
                busyTimeout.CommandText = "PRAGMA busy_timeout = 15000;";
                busyTimeout.ExecuteNonQuery();
            }

            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture) >= 1)
                    return connection;
            }

            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Schema;
            command.ExecuteNonQuery();
            transaction.Commit();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private const string Schema = """
            CREATE TABLE IF NOT EXISTS bookmarks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                url TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                visited_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_history_visited ON history(visited_at DESC);
            CREATE TABLE IF NOT EXISTS downloads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name TEXT NOT NULL,
                path TEXT NOT NULL,
                url TEXT NOT NULL,
                started_at TEXT NOT NULL,
                state TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version = 1;
            """;

    public void AddHistory(string title, string url)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO history(title,url,visited_at) VALUES($title,$url,$at)";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryRecord> GetHistory(int limit = 500) => ReadList(
        "SELECT id,title,url,visited_at FROM history ORDER BY visited_at DESC LIMIT $limit",
        reader => new HistoryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3))),
        (command) => command.Parameters.AddWithValue("$limit", limit));

    public IReadOnlyList<FrequentlyVisitedRecord> GetFrequentlyVisited(int limit = 8) => ReadList(
        "SELECT COALESCE(NULLIF(MAX(title),''), url), url, COUNT(*) FROM history GROUP BY url ORDER BY COUNT(*) DESC, MAX(visited_at) DESC LIMIT $limit",
        reader => new FrequentlyVisitedRecord(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)),
        command => command.Parameters.AddWithValue("$limit", limit));

    public void ClearHistory()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM history";
        command.ExecuteNonQuery();
    }

    public void AddBookmark(string title, string url)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO bookmarks(title,url,created_at) VALUES($title,$url,$at)";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BookmarkRecord> GetBookmarks() => ReadList(
        "SELECT id,title,url,created_at FROM bookmarks ORDER BY title COLLATE NOCASE",
        reader => new BookmarkRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3))));

    public void RemoveBookmark(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public long AddDownload(string fileName, string path, string url)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO downloads(file_name,path,url,started_at,state) VALUES($name,$path,$url,$at,'Started'); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", fileName);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return (long)command.ExecuteScalar()!;
    }

    public void UpdateDownload(long id, string state)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE downloads SET state=$state WHERE id=$id";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DownloadRecord> GetDownloads() => ReadList(
        "SELECT id,file_name,path,url,started_at,state FROM downloads ORDER BY started_at DESC LIMIT 200",
        reader => new DownloadRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseDate(reader.GetString(4)), reader.GetString(5)));

    public void SaveTheme(ThemeRecord theme)
    {
        using var transaction = _connection.BeginTransaction();
        SaveSetting("theme.name", theme.Name, transaction);
        SaveSetting("theme.chrome", theme.Chrome, transaction);
        SaveSetting("theme.background", theme.Background, transaction);
        SaveSetting("theme.surface", theme.Surface, transaction);
        SaveSetting("theme.sidebar", theme.Sidebar, transaction);
        SaveSetting("theme.accent", theme.Accent, transaction);
        SaveSetting("theme.text", theme.Text, transaction);
        SaveSetting("theme.muted", theme.Muted, transaction);
        SaveSetting("theme.border", theme.Border, transaction);
        SaveSetting("theme.input", theme.Input, transaction);
        SaveSetting("theme.hover", theme.Hover, transaction);
        transaction.Commit();
    }

    public ThemeRecord? GetTheme()
    {
        var values = new Dictionary<string, string>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT key,value FROM settings WHERE key LIKE 'theme.%'";
        using var reader = command.ExecuteReader();
        while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        if (!values.TryGetValue("theme.name", out var name)) return null;
        var keys = new[] { "chrome", "background", "surface", "sidebar", "accent", "text", "muted", "border", "input", "hover" };
        if (keys.Any(key => !values.ContainsKey($"theme.{key}"))) return null;
        return new ThemeRecord(name, values["theme.chrome"], values["theme.background"], values["theme.surface"],
            values["theme.sidebar"], values["theme.accent"], values["theme.text"], values["theme.muted"],
            values["theme.border"], values["theme.input"], values["theme.hover"]);
    }

    public string? GetSetting(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void SaveSetting(string key, string value, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<T> ReadList<T>(string sql, Func<SqliteDataReader, T> map, Action<SqliteCommand>? configure = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        using var reader = command.ExecuteReader();
        var values = new List<T>();
        while (reader.Read()) values.Add(map(reader));
        return values;
    }

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
    public void Dispose() => _connection.Dispose();
}
