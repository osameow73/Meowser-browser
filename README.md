# Meowser

Meowser is a lightweight desktop browser shell built on .NET 10 and Microsoft WebView2.

The current browser UI includes a Meowser start page with quick search and frequent site shortcuts; ten curated color palettes; separate private windows; and an experimental music panel that reads WebView2 media metadata and exposes playback controls where supported by the site.

## Install Meowser

For a normal install, download and run `Meowser-win-Setup.exe` from the [latest GitHub release](https://github.com/osameow73/Meowser-browser/releases). The installer is per-user and does not need administrator access. It creates Start menu and desktop shortcuts. The first install downloads the Microsoft WebView2 Runtime if it is not already present.

After installation, open **Settings → Software updates** and choose **Check for updates**. Meowser is preconfigured to use this repository, downloads available releases, and restarts only after you accept. Updates replace program files while browser data remains in `%LOCALAPPDATA%\Meowser`.

To create a local installer from source, install the .NET 10 SDK, then run from the project folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

The setup program and update packages are written to `outputs\Meowser-1.0.0\packages`. To publish future releases automatically, push a new version tag such as `v1.0.1`. The included GitHub Actions workflow builds the installer and publishes Velopack update assets from the tag version. For local builds, pass the version with `-Version`, for example `-Version 1.0.1`.

Meowser currently ships unsigned. Windows may display a SmartScreen warning until the app has a code-signing certificate and reputation.

## Run from source

Install the **.NET 10 SDK** and the **Microsoft Edge WebView2 Runtime** on Windows. Then open a terminal in this folder and run:

```powershell
dotnet run --project .\src\Meowser.App\Meowser.App.csproj
```

The first run restores NuGet packages, including the MIT-licensed Microsoft SQLite provider, so it needs access to NuGet. For a full solution build and unit tests:

```powershell
dotnet build .\Meowser.sln
dotnet test .\tests\Meowser.Browser.Core.Tests\Meowser.Browser.Core.Tests.csproj
dotnet test .\tests\Meowser.Data.Sqlite.Tests\Meowser.Data.Sqlite.Tests.csproj
```

Meowser stores its database and WebView2 browser profile under `%LOCALAPPDATA%\Meowser`, outside the install directory, so normal app updates preserve browser data.

## Shortcuts

- `Ctrl+T`: new tab
- `Ctrl+W`: close the current tab
- `Ctrl+Shift+N`: open a private window
- `Ctrl+D`: bookmark or unbookmark the current page
- `Ctrl+L`: focus and select the address bar
- `Alt+Left` / `Alt+Right`: back / forward
- `F5` or `Ctrl+R`: reload
- Drag a tab left or right to reorder it

See [docs/development-plan.md](docs/development-plan.md) for the architecture and staged roadmap.
