using System.IO;
using System.Text.Json;
using Meowser.Browser.Core.Sessions;

namespace Meowser.App.Services;

public sealed class BrowserSessionStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Meowser", "session.json");

    public BrowserSessionSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<BrowserSessionSnapshot>(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(BrowserSessionSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
