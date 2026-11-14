using System.IO;
using System.Windows;
using Meowser.App.Services;
using Microsoft.Data.Sqlite;
using Velopack;

namespace Meowser.App;

public partial class App : Application
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    public App()
    {
        AppDiagnostics.Write("App constructor started.");
        DispatcherUnhandledException += (_, args) =>
        {
            var message = $"{DateTimeOffset.Now:O}{Environment.NewLine}{args.Exception}{Environment.NewLine}{Environment.NewLine}";
            AppDiagnostics.Write($"Unhandled UI exception: {args.Exception}");
            try
            {
                var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meowser");
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory, "startup-error.log"), message);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            MessageBox.Show(args.Exception.ToString(), "Meowser error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDiagnostics.Write("WPF startup event.");
        base.OnStartup(e);
        try
        {
            var window = new Views.MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (SqliteException exception)
        {
            AppDiagnostics.Write($"Database startup failed: {exception}");
            MessageBox.Show("Meowser could not access its local browser database. Check that the drive is available and writable. " +
                            "Your saved browser data has not been deleted.",
                "Meowser database error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
