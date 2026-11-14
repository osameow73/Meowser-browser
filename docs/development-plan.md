# Meowser development plan

## Product direction

Meowser is a Windows desktop browser built with WPF and Microsoft WebView2. The application owns the browser experience (window chrome, navigation, tabs, preferences and stored data); WebView2 supplies the web rendering engine.

## Architecture

- **Meowser.App (WPF):** composition root, windows, views, view models, commands, theme resources and app lifecycle. Views depend on browser abstractions rather than directly owning browser rules.
- **Meowser.Browser.Core:** browser-facing contracts and framework-independent navigation/domain behavior. No WPF, WebView2 or persistence references.
- **Meowser.Browser.WebView2:** adapter implementing the browser contracts with WebView2. Owns WebView2 initialization, navigation events and browser-specific configuration.
- **Meowser.Data.Sqlite:** SQLite storage for bookmarks, history, downloads and theme/settings data.
- **Meowser.Browser.Core.Tests:** unit tests for address resolution and browser-domain behavior; integration tests where practical.
- **Meowser.Data.Sqlite.Tests:** database tests using isolated temporary SQLite files.

Dependencies point inward: App composes Core, the WebView2 adapter and the SQLite storage project. The adapter implements Core contracts. Core never depends on UI, storage or the rendering engine. External dependencies are limited to WebView2 and the SQLite provider.

## Project structure (target)

```text
Meowser.sln
Directory.Build.props
docs/
  development-plan.md
src/
  Meowser.App/
    App.xaml
    Views/MainWindow.xaml
    ViewModels/
    Themes/
    Composition/
  Meowser.Browser.Core/
    Navigation/
    Sessions/
  Meowser.Browser.WebView2/
    WebView2BrowserSurface.cs
  Meowser.Data.Sqlite/
    Migrations/
    Repositories/
tests/
  Meowser.Browser.Core.Tests/
  Meowser.Data.Sqlite.Tests/
```

## Incremental milestones

1. **Browser shell (complete, build verification pending):** WPF window, Meowser styling, back/forward/reload controls, address bar, Enter-to-navigate, URL/search resolution, WebView2 initialization and navigation. Browser surface is behind a Core contract.
2. **Interaction and resilience (implemented, build verification pending):** loading state, navigation errors, WebView2 runtime/setup messaging, keyboard shortcuts and URL resolver unit tests. External protocol handling and safe disposal remain follow-up work for this milestone.
3. **Tabs and sessions (implemented, build verification pending):** tab model, independent WebView2 instances, tab lifecycle, and JSON session restore. Test tab/session logic independently of WPF.
4. **Persistence (implemented, verification pending):** bookmarks, history, downloads and theme preferences stored in SQLite with parameterized queries and isolated database tests.
5. **Themes and personalization (implemented, verification pending):** ten curated, role-based palettes with dynamic contrast-safe browser surfaces and persisted theme selection.
6. **Privacy and quality (core features implemented, verification pending):** private windows use a separate temporary WebView2 profile and do not write normal history/download records; downloads prompt for a destination. Remaining follow-up: accessibility pass, updater/installer, and full regression verification.
7. **Start page and tab organization (implemented, verification pending):** Meowser-branded quick search, frequent site/bookmark shortcuts, lazy WebView2 creation for empty tabs, top-positioned draggable tabs and persisted order.

Each milestone should be a small change with a build and focused tests before moving on. Avoid replacing stable modules when adding the next capability.

## Current milestone acceptance criteria

- Starts as a native WPF desktop window.
- Shows browser controls and an embedded WebView2.
- Back, forward, reload and address entry work; plain hostnames become HTTPS URLs and other text becomes a web search.
- WebView2-specific code stays in the adapter project; navigation/address logic stays testable outside WPF.
- Targets .NET 10 Windows. The current development machine must have the .NET 10 SDK and the WebView2 runtime to build/run this milestone.
