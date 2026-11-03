# NudgeJudge

NudgeJudge is a browser router for the desktop. You set it as your system's default browser. When an app opens a link, NudgeJudge checks the URL against your rules and opens it in the browser or profile you chose, such as work links in a Chrome work profile, banking in a Firefox private window, and everything else in your usual browser. When no rule matches, a small chooser window asks you where to open the link, and can save your answer as a rule.

Built with [Avalonia UI](https://avaloniaui.net/) and .NET 10 (C#), using a strict MVVM structure. Linux and Windows come first. macOS is planned, with stubs where platform code is needed.

> **Status: early development.** The UI, the rule engine, browser scanning and default-browser registration (Linux and Windows) and single-instance mode work. Start on login is not built yet. See [Current state](#current-state).

## Features

- **Rules, checked top to bottom.** The first rule that matches picks the browser. Every rule has a name (shown in the list and in Activity) and one or more conditions, joined by **all** or **any**. Each condition can be negated ("doesn't match", "is not") and holds any number of alternatives, OR'd together. For example, *YouTube from Teams*: link is `www.youtube.com` or `youtu.be`, **and** opened from `ms-teams.exe`, opens in Chrome. A plain *YouTube* rule below it then sends every other YouTube link to Firefox.
  - **Link conditions** use one of four match types:
    - **Wildcard:** `*.atlassian.net`, `github.com/acme/*` (a leading `*.` also matches the bare domain)
    - **Exact domain:** `www.youtube.com`
    - **Path prefix:** `github.com/acme` matches `/acme` and `/acme/…` but not `/acmecorp`
    - **Regular expression:** matched against the full URL
  - **Opened from conditions** match the app the link was clicked in: a program name with or without `.exe` (`ms-teams`, `slack`), its title (`Microsoft Teams`), a pattern (`*teams*`) or a full path. Program names you've seen in Activity are suggested as you type.
- **Source app detection.** When an app opens a link, NudgeJudge walks up its own process tree past openers and shells (`xdg-open`, `gio`, `sh`, `OpenWith.exe`, …) to the app that started it. If that chain is broken (the opener exited first), it falls back to the app that owns the foreground window (Windows), then to the `CHROME_DESKTOP` hint Electron apps leave in the environment (Linux). Friendly names come from the program's file description (Windows) or its `.desktop` file (Linux). Detection is best effort: when it can't tell, the link still routes, and "opened from" conditions simply don't match. A link forwarded to the running instance carries the source with it.
- **Live rule tester.** Open "Test a link" on the Rules page (Ctrl+T), paste a link (and optionally the app it came from), and a route diagram shows it running past each rule, which rule caught it, and how long matching took.
- **Chooser for unmatched links.** Links that arrive in a burst queue up as tabs. Press 1–9 to pick a browser and Esc to skip. You can open a link once, or save the choice as a rule for N hours, until restart, or permanently. When the chooser knows which app the link came from, it can limit the saved rule to links from that app.
- **Browser profiles.** Each entry is a program plus arguments, so one browser can appear several times: a specific profile, a private window, extra flags.
- **Activity log.** Shows every link handled, the app it was opened from, and the rule or choice that sent it where it went. It's kept between runs in `activity.log` next to the config (one JSON object per line, owner-only). Settings → Activity sets how many entries to keep (100 by default; the oldest are deleted past that) or turns logging off. "Create rule" on an entry opens the rule editor with a pattern suggested from the link and the browser it went to; "Opened from" then starts with that entry's app.
- **Tray menu.** Turn routing on or off (when off, links go straight to your primary browser), open the app, or quit.
- **Light and dark themes**, following the system by default.
- **Default-browser setup** from Settings → "Make default":
  - **Linux:** installs `~/.local/share/applications/nudgejudge.desktop` and makes it the `x-scheme-handler/http` and `https` default via `xdg-mime`. It deliberately does not claim `text/html`, so local HTML files keep opening in a real browser.
  - **Windows:** registers NudgeJudge per user (HKCU, no admin) as a browser with Capabilities, then opens *Default apps*. Windows only lets the user pick the default, so you click "Set default" there. NudgeJudge checks again when you return to its window.

## Security

NudgeJudge receives URLs from any app on the machine, so every URL is treated as untrusted:

- **URL validation.** Only absolute `http`/`https` URLs with a host are accepted (max 8,192 characters, no whitespace or control characters). Everything else is rejected and logged.
- **No shell.** Browsers are started with an explicit argument list (`ProcessStartInfo.ArgumentList`), so neither the URL nor an argument template can inject commands. A validated URL always starts with `http(s):`, so the browser can't read it as a command-line flag.
- **ReDoS protection.** All patterns, including those generated from wildcards, run on .NET's non-backtracking regex engine (`RegexOptions.NonBacktracking`), which matches in linear time. On top of that, patterns are limited to 512 characters and each match has a 50 ms timeout. Lookarounds, backreferences and atomic groups are rejected when you save the rule.
- **Private config.** The config file is written atomically with owner-only permissions (`0600` file, `0700` directory on Unix).
- **Handler registration.** The OS tools are run with explicit argument lists, never a shell. The Linux `Exec` line follows the Desktop Entry quoting rules, and a test checks that it parses back to the same argv. Launch paths containing control characters are refused. Every argument the OS passes in is validated as a URL like any other link.

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

On first run, the Rules page asks how you want to start: empty, or with a **sample** setup of browsers and rules (marked "Sample" in the UI). Nothing is created unless you pick the samples. They can be removed from the Rules page at any time, and a browser scan replaces them with the browsers you have installed.

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
  Rules/                  PatternCompiler (pattern → matcher), SourceAppPattern (app name → matcher),
                          RuleEngine (conditions → one predicate; ordered matching + per-rule trace)
  Security/               UrlValidator
  Config/                 JsonConfigStore, AppPaths (per-OS paths, private atomic writes), SampleData
  Browsers/               BrowserLauncher (argv-only launch); Discovery/ finds installed browsers, private modes and profiles
  Platform/               SystemBoot (detects reboots for "until restart" rules); SourceAppDetector + per-OS process tables
                          (which app opened the link); default-browser registrars (Linux xdg-mime,
                          Windows registry + Default Apps, macOS stub), AppLaunchCommand, ICommandRunner
  Routing/                LinkRouter (validate → pass through / match / ask), ActivityLog + its rolling file
  Ipc/                    Single instance: InstanceLock, InstanceServer/InstanceClient, wire protocol, Unix socket and named-pipe transports

NudgeJudge/               Avalonia UI library
  ViewModels/             CommunityToolkit.Mvvm view models; they reach windows only through interfaces in Services/
  Views/                  MainWindow, the Rules/Browsers/Activity/Settings pages, ChooserWindow
  Controls/               RouteDiagram, MatchKindGlyph, Icon (custom-drawn controls)
  Services/               IWindowService, IDialogService, IThemeService, IUiDispatcher + Avalonia implementations
  Styles/                 Theme.axaml (colour and icon resources), Styles.axaml (shared styles)
  App.axaml(.cs)          Composition root and tray icon

NudgeJudge.Desktop/       Executable for Linux, Windows and macOS
NudgeJudge.Core.Tests/    xUnit tests for matching, validation, ReDoS safety, argument building, browser discovery and registration
NudgeJudge.UI.Tests/      Avalonia headless tests (xunit v3) that drive real views, e.g. the default-browser setup in Settings
```

Every incoming link goes through `LinkRouter.Route(string)`. That's the command line of the first launch, and links that later launches forward over the single-instance channel. The design system is documented in [DESIGN.md](DESIGN.md), and product context in [PRODUCT.md](PRODUCT.md).

## Current state

**Working**

- [x] Rule engine: named rules with compound conditions (all/any, negation, OR'd alternatives) on the link (four match types) and the source app; ordered first-match-wins, per-rule trace, cached compiled patterns. Configs from before conditions are upgraded on load (the pattern becomes a link condition and, when unnamed, the name)
- [x] Source app detection on Linux (`/proc` parent chain, `.desktop` names, `CHROME_DESKTOP`) and Windows (Toolhelp parent chain with recycled-pid check, file descriptions, foreground window), logged in Activity and forwarded over the single-instance channel
- [x] Temporary rules that expire after N hours or at reboot (Linux `boot_id`; uptime-based on other platforms)
- [x] URL validation, argv-only browser launch, ReDoS-safe matching
- [x] JSON config with atomic, owner-only writes; keeps a copy of a corrupt file and starts from defaults
- [x] Main window: Rules (live tester + route diagram + editor), Browsers (editor with a preview of the exact command it will run, "try" button), Activity (with filtering), Settings
- [x] Chooser window: tabs for links that arrive together, number-key shortcuts, open once / for N hours / until restart / always
- [x] Tray icon and menu, keep-running-in-tray setting, routing on/off with a primary browser for pass-through
- [x] Light, dark and follow-system themes
- [x] Browser scanning: Linux `.desktop` entries (including Snap and Flatpak) and Windows `StartMenuInternet`, with a private-window entry per browser and Firefox profiles (both `profiles.ini` and the new profile manager) and Chrome/Chromium-family profiles when there's more than one. A scan that adds browsers removes the sample browsers and rules
- [x] Default-browser registration: Linux (`.desktop` entry + `xdg-mime`) and Windows (Capabilities registration + Default Apps hand-off). Status is checked whenever the main window comes forward and catches a half registration (only HTTP or only HTTPS) and a stale one (the app moved since registering). When .NET is a per-user install the app's own launcher can't find without `DOTNET_ROOT` (e.g. `~/.dotnet`, set only in a shell profile), the handler is registered as `<that runtime>/dotnet NudgeJudge.Desktop.dll`, because desktop apps start link handlers without the terminal's environment
- [x] Persistent activity log: rolling file with a configurable limit, an off switch, and "create rule from this entry"
- [x] Single instance with secure local messaging: the first launch holds a per-user lock and runs a server; later launches forward their links (or "show the window") and exit within a fraction of a second. Unix domain socket in an owner-only directory (`$XDG_RUNTIME_DIR/nudgejudge` on Linux), socket `0600`, peer uid checked with `SO_PEERCRED` on Linux; per-user named pipe on Windows (current-user ACL, server owner verified, first-instance check against squatting). A fresh random token per session is stored in a `0600` file and checked in constant time; messages are size-, count- and time-limited; every URL is validated again on receipt. If no server answers within 5 seconds, a launch handles its links itself rather than drop them
- [x] 191 core unit tests and 29 headless UI tests

**Limitations right now**

- Registering from `dotnet run` points the OS at your build output; register again from the installed app once packaging exists. Registration can't be undone from inside the app yet (pick another browser in your system settings instead).
- Default-browser registration isn't available on macOS yet.
- Browser scanning isn't available on macOS yet.
- A launch that only came to open links stays running as the server only when "keep running in tray" is on; otherwise it exits once its links are handled.
- Single-instance mode is untested on Windows and macOS so far.
- macOS isn't tested.

## Upcoming work

Roughly in order:

1. **Start on login.**
   - Linux: XDG autostart entry.
   - Windows: `Run` registry key.
2. **More protocols.** Optional handlers beyond `http`/`https` (e.g. `mailto:`, custom schemes), off by default.
3. **Packaging.** Linux AppImage or Flatpak, Windows installer (MSIX or Inno Setup), and the Linux executable renamed to `nudgejudge`.
4. **macOS.** Real implementations for the stubs (browser discovery via LaunchServices, default-browser registration via `LSSetDefaultHandlerForURLScheme`), a menu-bar icon, and testing.

## Contributing

The project is at an early stage. Before larger changes, read [DESIGN.md](DESIGN.md) for the visual rules and keep view models free of Avalonia window types; go through the interfaces in `NudgeJudge/Services/` instead. New matching or validation logic in `NudgeJudge.Core` should come with tests.

## AI Usage

Designed the primary system architecture and built the core library, while Claude provided AI assistance to generate the testing suite and refine the UI presentation.

### Skills

* UI/UX - [Impeccable](https://github.com/pbakaus/impeccable)
* Agent - [Graphify](https://github.com/Graphify-Labs/graphify)
