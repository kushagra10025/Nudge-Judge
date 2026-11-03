# NudgeJudge

NudgeJudge is a browser router for the desktop. You set it as your system's default browser. When an app opens a link, NudgeJudge checks the URL against your rules and opens it in the browser or profile you chose, such as work links in a Chrome work profile, banking in a Firefox private window, and everything else in your usual browser. When no rule matches, a small chooser window asks you where to open the link, and can save your answer as a rule.

Built with [Avalonia UI](https://avaloniaui.net/) and .NET 10 (C#), using a strict MVVM structure. Linux and Windows come first. macOS is planned, with stubs where platform code is needed.

> **Status: early development.** The UI and the rule engine work. Becoming the system default browser, running as a single background instance, and finding installed browsers automatically are not built yet. See [Current state](#current-state).

## Features

- **Rules, checked top to bottom.** The first rule that matches picks the browser. Four match types:
  - **Wildcard:** `*.atlassian.net`, `github.com/acme/*` (a leading `*.` also matches the bare domain)
  - **Exact domain:** `www.youtube.com`
  - **Path prefix:** `github.com/acme` matches `/acme` and `/acme/…` but not `/acmecorp`
  - **Regular expression:** matched against the full URL
- **Live rule tester.** Paste a link and a route diagram shows it running past each rule, which rule caught it, and how long matching took.
- **Chooser for unmatched links.** Links that arrive in a burst queue up as tabs. Press 1–9 to pick a browser and Esc to skip. You can open a link once, or save the choice as a rule for N hours, until restart, or permanently.
- **Browser profiles.** Each entry is a program plus arguments, so one browser can appear several times: a specific profile, a private window, extra flags.
- **Activity log.** Shows every link handled in the current session, with the rule or choice that sent it where it went.
- **Tray menu.** Turn routing on or off (when off, links go straight to your primary browser), open the app, or quit.
- **Light and dark themes**, following the system by default.

## Security

NudgeJudge receives URLs from any app on the machine, so every URL is treated as untrusted:

- **URL validation.** Only absolute `http`/`https` URLs with a host are accepted (max 8,192 characters, no whitespace or control characters). Everything else is rejected and logged.
- **No shell.** Browsers are started with an explicit argument list (`ProcessStartInfo.ArgumentList`), so neither the URL nor an argument template can inject commands. A validated URL always starts with `http(s):`, so the browser can't read it as a command-line flag.
- **ReDoS protection.** All patterns, including those generated from wildcards, run on .NET's non-backtracking regex engine (`RegexOptions.NonBacktracking`), which matches in linear time. On top of that, patterns are limited to 512 characters and each match has a 50 ms timeout. Lookarounds, backreferences and atomic groups are rejected when you save the rule.
- **Private config.** The config file is written atomically with owner-only permissions (`0600` file, `0700` directory on Unix).

## Getting started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
# Run the app (opens the main window)
dotnet run --project NudgeJudge.Desktop

# Simulate the OS handing NudgeJudge a link
dotnet run --project NudgeJudge.Desktop -- https://example.com

# Start in the tray only
dotnet run --project NudgeJudge.Desktop -- --background

# Run the tests
dotnet test
```

On first run, NudgeJudge creates a config with **sample** browsers and rules (marked "Sample" in the UI) so the screens aren't empty. Check their paths, or replace them with your own on the Browsers page.

The config file is stored at:

| OS | Path |
|---|---|
| Linux | `$XDG_CONFIG_HOME/nudgejudge/config.json` (default `~/.config/nudgejudge/config.json`) |
| Windows | `%APPDATA%\NudgeJudge\config.json` |
| macOS | `~/Library/Application Support/NudgeJudge/config.json` |

> **Linux tray:** works on KDE, Cinnamon and most other desktops. GNOME needs the [AppIndicator extension](https://extensions.gnome.org/extension/615/appindicator-support/).

## Project structure

```
NudgeJudge.Core/          Plain .NET library with no UI dependencies
  Models/                 Rule, BrowserProfile, AppSettings, AppConfig
  Rules/                  PatternCompiler (pattern → matcher), RuleEngine (ordered matching + per-rule trace)
  Security/               UrlValidator
  Config/                 JsonConfigStore, AppPaths (per-OS paths, private atomic writes), SampleData
  Browsers/               BrowserLauncher (argv-only launch); Discovery/ finds installed browsers, private modes and profiles
  Platform/               SystemBoot (detects reboots for "until restart" rules), default-browser registration interface + stub
  Routing/                LinkRouter (validate → pass through / match / ask), ActivityLog

NudgeJudge/               Avalonia UI library
  ViewModels/             CommunityToolkit.Mvvm view models; they reach windows only through interfaces in Services/
  Views/                  MainWindow, the Rules/Browsers/Activity/Settings pages, ChooserWindow
  Controls/               RouteDiagram, MatchKindGlyph, Icon (custom-drawn controls)
  Services/               IWindowService, IDialogService, IThemeService, IUiDispatcher + Avalonia implementations
  Styles/                 Theme.axaml (colour and icon resources), Styles.axaml (shared styles)
  App.axaml(.cs)          Composition root and tray icon

NudgeJudge.Desktop/       Executable for Linux, Windows and macOS
NudgeJudge.Core.Tests/    xUnit tests for matching, validation, ReDoS safety, argument building and browser discovery
```

Every incoming link goes through `LinkRouter.Route(string)`. Today that's the command line; later the single-instance messaging channel will feed the same method. The design system is documented in [DESIGN.md](DESIGN.md), and product context in [PRODUCT.md](PRODUCT.md).

## Current state

**Working**

- [x] Rule engine: four match types, ordered first-match-wins, per-rule trace, cached compiled patterns
- [x] Temporary rules that expire after N hours or at reboot (Linux `boot_id`; uptime-based on other platforms)
- [x] URL validation, argv-only browser launch, ReDoS-safe matching
- [x] JSON config with atomic, owner-only writes; keeps a copy of a corrupt file and starts from defaults
- [x] Main window: Rules (live tester + route diagram + editor), Browsers (editor with a preview of the exact command it will run, "try" button), Activity (with filtering), Settings
- [x] Chooser window: tabs for links that arrive together, number-key shortcuts, open once / for N hours / until restart / always
- [x] Tray icon and menu, keep-running-in-tray setting, routing on/off with a primary browser for pass-through
- [x] Light, dark and follow-system themes
- [x] Browser scanning: Linux `.desktop` entries (including Snap and Flatpak) and Windows `StartMenuInternet`, with a private-window entry per browser and Firefox profiles (both `profiles.ini` and the new profile manager) and Chrome/Chromium-family profiles when there's more than one. A scan that adds browsers removes the sample browsers and rules
- [x] 83 unit tests for the core

**Limitations right now**

- NudgeJudge **cannot yet register as the default browser**. To try it, run it with a URL argument (see [Getting started](#getting-started)).
- There's **no single-instance mode**: each launch with a URL handles that link in its own process and exits.
- Browser scanning isn't available on macOS yet.
- The activity log is in memory only and clears when the app quits.
- macOS isn't tested.

## Upcoming work

Roughly in order:

1. **Single instance with secure local messaging.** The first process becomes the server and later launches send it their URLs, then exit right away. Named pipe on Windows, Unix domain socket on Linux and macOS. The server generates a random session token at startup and stores it in a `0600` file. Every message must include the token, and every URL is validated again on receipt.
2. **Default-browser registration.**
   - Linux: install a `.desktop` entry and run `xdg-settings set default-web-browser`.
   - Windows: register Capabilities/URL associations in the registry, then send the user to the system Default Apps settings.
   - macOS: stub for now; later `LSSetDefaultHandlerForURLScheme`.
3. **Start on login.**
   - Linux: XDG autostart entry.
   - Windows: `Run` registry key.
4. **Persistent activity log**, with a size limit and an option to turn it off; "create rule from this entry".
5. **More protocols.** Optional handlers beyond `http`/`https` (e.g. `mailto:`, custom schemes), off by default.
6. **Packaging.** Linux AppImage or Flatpak, Windows installer (MSIX or Inno Setup), and the Linux executable renamed to `nudgejudge`.
7. **macOS.** Real implementations for the stubs (browser discovery via LaunchServices, default-browser registration), a menu-bar icon, and testing.

## Contributing

The project is at an early stage. Before larger changes, read [DESIGN.md](DESIGN.md) for the visual rules and keep view models free of Avalonia window types; go through the interfaces in `NudgeJudge/Services/` instead. New matching or validation logic in `NudgeJudge.Core` should come with tests.

## AI Usage

Designed the primary system architecture and built the core library, while Claude provided AI assistance to generate the testing suite and refine the UI presentation.

### Skills

* UI/UX - [Impeccable](https://github.com/pbakaus/impeccable)
* Agent - [Graphify](https://github.com/Graphify-Labs/graphify)
