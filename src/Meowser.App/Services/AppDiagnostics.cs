using System.IO;

namespace Meowser.App.Services;

public static class AppDiagnostics
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Meowser", "startup.log");

    public static void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
