# Architecture

[← Back to README](../README.md)

Built with [Avalonia UI](https://avaloniaui.net/) and .NET 10 (C#), using a strict MVVM structure.

![A link from Teams passing rules 1 to 3 and diverting at rule 4 to a Chrome work profile](images/how-it-works.svg)

Every incoming link goes through `LinkRouter.Route(string)`. That's the command line of the first launch, and links that later launches forward over the single-instance channel. The router validates the URL, then either passes it through (routing off), matches it against the rules, or asks via the chooser.

## Project structure

```
NudgeJudge.Core/          Plain .NET library with no UI dependencies
  Models/                 Rule, BrowserProfile, AppSettings, AppConfig
  Rules/                  PatternCompiler (pattern → matcher), SourceAppPattern (app name → matcher),
                          RuleEngine (conditions → one predicate, compiled once; ordered matching + per-rule trace;
                          PreparedRules for the router), LinkInput (per-link host/path, worked out once) and StarGlob
                          (regex-free `*` globs)
  Security/               UrlValidator
  Config/                 JsonConfigStore, AppPaths (per-OS paths, private atomic writes), SampleData
  Browsers/               BrowserLauncher (argv-only launch); Discovery/ finds installed browsers, private modes and profiles
  Platform/               SystemBoot (detects reboots for "until restart" rules); SourceAppDetector + per-OS process tables
                          (which app opened the link); default-browser registrars (Linux xdg-mime,
                          Windows registry + Default Apps, macOS stub), AppLaunchCommand, ICommandRunner
  Routing/                LinkRouter (validate → pass through / match / ask), ActivityLog + its rolling file
  Ipc/                    Single instance: InstanceLock, InstanceServer/InstanceClient, wire protocol, Unix socket and named-pipe transports
  CoreServices.cs         The routing half of the app (config, rules, activity log, router) without UI, built before the UI starts

NudgeJudge/               Avalonia UI library
  ViewModels/             CommunityToolkit.Mvvm view models; they reach windows only through interfaces in Services/
  Views/                  MainWindow, the Rules/Browsers/Activity/Settings pages, ChooserWindow
  Controls/               RouteDiagram, MatchKindGlyph, Icon (custom-drawn controls)
  Services/               IWindowService, IDialogService, IThemeService, IUiDispatcher + Avalonia implementations;
                          IUpdateService + its Velopack implementation (GitHub Releases), AppInfo (version, project links)
  Styles/                 Theme.axaml (colour and icon resources), Styles.axaml (shared styles)
  App.axaml(.cs)          Composition root

NudgeJudge.Desktop/       Executable for Linux, Windows and macOS; routes launch links before starting the UI; publishes as Native AOT
NudgeJudge.Tray/          Small native executable that stays in the tray: single-instance server, routing, a native tray icon
                          (Win32 Shell_NotifyIcon, or StatusNotifierItem + dbusmenu over D-Bus), no Avalonia
NudgeJudge.Core.Tests/    xUnit tests for matching, validation, ReDoS safety, argument building, browser discovery and registration
NudgeJudge.UI.Tests/      Avalonia headless tests (xunit v3) that drive real views, e.g. the default-browser setup in Settings
```

## Processes

What runs depends on whether "keep running in tray" is on.

- **Off.** One `NudgeJudge.Desktop` process per launch, as described above: links are routed before Avalonia starts, and the process exits when its windows close.
- **On.** The launch that becomes the primary instance starts `NudgeJudge.Tray`, lets go of the single-instance lock for it, and hands over anything its own launch needs: links for the chooser, or a request for the main window. Then it exits. The tray host owns the tray icon and the single-instance server, and routes links with `CoreServices` itself. When a window is needed, it starts `NudgeJudge.Desktop --ui` with the first order on the command line (`--order=`, the request as base64 JSON). The UI shows the chooser or main window straight away, without waiting for its own server, which starts in the background. Later orders reach it over a second channel (`ui.lock`, `ui.token`, `nudgejudge-ui.sock`). The router raises `ChoiceRequested` before it writes the activity log, so the host starts the UI process while that log write (with its fsync) is still happening. That UI process has no tray icon and exits when its windows close. While it's running, the host forwards incoming links to it, so the chooser and the Activity page stay live.

The split keeps memory low while the app waits in the tray (about 3–5 MB private on Linux, against about 32 MB of private memory and 127 MB resident before). Avalonia, Skia and the fonts are only loaded while a window is open. The tray host has to be its own executable: a native binary keeps type data for all of its code in private memory (the `.hydrated` section), and in `NudgeJudge.Desktop` that's 9 MB, most of it Avalonia's.

Settings changed in the UI process reach the host through `config.json`. The host re-reads it (`JsonConfigStore.Refresh`) before routing each link, when the tray menu opens, and when the UI process exits. If "keep running in tray" was turned off by then, the host quits too. Inside an AppImage, the host is started as `$APPIMAGE --tray-host`, which replaces itself with the tray executable (`execv`), so the image stays mounted for as long as the host runs.

## Related docs

- [DESIGN.md](../DESIGN.md): the "Signal Box" design system
- [PRODUCT.md](../PRODUCT.md): product context
- [Performance](performance.md): how startup and matching are kept fast
- [Security](security.md) and [How data is saved](data-storage.md)
