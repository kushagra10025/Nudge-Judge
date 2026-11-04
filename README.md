# NudgeJudge

NudgeJudge is a browser router for the desktop. You set it as your system's default browser. When an app opens a link, NudgeJudge checks the URL against your rules and opens it in the browser or profile you chose, such as work links in a Chrome work profile, banking in a Firefox private window, and everything else in your usual browser. When no rule matches, a small chooser window asks you where to open the link, and can save your answer as a rule.

Built with [Avalonia UI](https://avaloniaui.net/) and .NET 10 (C#), using a strict MVVM structure. Linux and Windows come first. macOS is planned, with stubs where platform code is needed.

> **Status: early development.** The UI, the rule engine, browser scanning, default-browser registration and start on login (Linux and Windows), single-instance mode and the native (AOT) build work. Packaging and macOS support aren't built yet. See [Current state](#current-state).

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
- **ReDoS protection.** Domain, wildcard and path-prefix patterns never touch a regex engine: they compile to plain string comparisons (a `*` glob is matched by finding its literal pieces left to right). Regular expressions run on .NET's non-backtracking engine (`RegexOptions.NonBacktracking`), which matches in linear time. On top of that, patterns are limited to 512 characters and each regex match has a 50 ms timeout. Lookarounds, backreferences and atomic groups are rejected when you save the rule.
- **Private config.** The config file and activity log are written atomically with owner-only permissions (`0600` file, `0700` directory on Unix). See [How data is saved](#how-data-is-saved).
- **Handler registration.** The OS tools are run with explicit argument lists, never a shell. The Linux `Exec` line follows the Desktop Entry quoting rules, and a test checks that it parses back to the same argv. Launch paths containing control characters are refused. Every argument the OS passes in is validated as a URL like any other link.

## How data is saved

`config.json` and `activity.log` are rewritten whole on every change. Each write follows the same steps:

1. **Lock.** The writer takes an exclusive lock on a sidecar file (`config.json.lock`, `activity.log.lock`), so a standalone launch and the running instance can't interleave. A config update that can't get the lock within 2 seconds goes ahead without it rather than freeze the UI.
2. **Re-read if changed.** If another process rewrote the config since this one last saw it, the store reloads it first and then applies its change, so neither side's edits are lost. Updates find rules and browsers by id, never by list position.
3. **Atomic replace.** The new contents go to a temp file in the same directory, are flushed to disk, then renamed over the old file. Readers see the old version or the new one, never half of each. On Linux and macOS the directory is fsynced too, so the rename survives a power cut.

When something goes wrong:

| Situation | What happens |
|---|---|
| A save fails (disk full, file locked) | The change stays in memory, a banner reports it, and the next change retries the save. Nothing is thrown to the UI. |
| `config.json` can't be parsed | It's kept as `config.json.broken-<timestamp>` (never overwritten) and the app starts from defaults. |
| The config is from an older version | The original is kept as `config.json.v<N>.bak`, then the upgraded file is written. |
| The config is from a newer version | It's opened read-only: changes work for the session but are never saved, so the newer version's settings stay intact. |
| You spin a number field in Settings | It saves 400 ms after the last change (and on exit), so spinning the activity limit down and back up doesn't delete entries. |

### Windows: known gaps and planned fixes

The steps above run on Windows too, but the test suite hasn't run there yet, and two Windows-specific behaviours aren't covered.

**1. The rename isn't forced to disk.** Windows has no way to fsync a directory, and .NET's `File.Move` doesn't ask for write-through. After a power cut, the config is never corrupt (NTFS journals its metadata), but it can come back as the previous version, losing the last change.

*Fix:* on Windows, do the replace with `MoveFileExW(temp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` through P/Invoke instead of `File.Move`. The call only returns once the rename is on disk. This would replace the Unix directory fsync in `AppPaths.WritePrivateFileAtomic`, leaving Linux and macOS unchanged.

**2. Antivirus and indexers can make a save fail.** Defender, Windows Search and sync clients (OneDrive, Dropbox) often open a freshly written file for a few milliseconds without allowing it to be deleted. A rename that lands in that window fails with "access denied" or a sharing violation. Today:

- **Config:** the change stays in memory with a warning, and the next change retries. It's lost only if the app quits before another change is made.
- **Activity log:** that entry stays in memory but is never written to disk.

Older Windows 10 builds may also refuse a rename over a file another process has open, even when that process allows deletion.

*Fix:* in `WritePrivateFileAtomic`, on Windows only, retry the rename when it fails with `ERROR_ACCESS_DENIED` (5), `ERROR_SHARING_VIOLATION` (32) or `ERROR_LOCK_VIOLATION` (33). Allow about 5 attempts with a short backoff (e.g. 10, 20, 40, 60, 80 ms) before giving up and reporting the error as today. VS Code and npm handle the same problem this way.

**3. Verify on a real Windows machine.** Add a GitHub Actions workflow that runs `dotnet test` on both `ubuntu-latest` and `windows-latest`. The storage tests in `NudgeJudge.Core.Tests/ConfigStoreTests.cs` run on both platforms: the failed-save test puts a directory where the file should be instead of using Unix permissions, and the Unix file-mode checks are skipped on Windows.

## Performance

NudgeJudge sits between every link and your browser, so it's built to add as little delay as possible.

| Measured on one Linux laptop | Before | Now |
|---|---|---|
| Launch to browser start, native build | ~1.4 s | **22 ms** (median of 15 runs) |
| Launch to browser start, `dotnet run` build | ~1.4 s | 249 ms |
| A later launch handing its link to the running instance, native build | | 15–44 ms |
| Matching one link against 66 rules | 264 µs, 75 KB allocated | **6.6 µs, 64 B** |
| First match, including compiling the rules (JIT build) | 495 ms | 230 ms |

"Before" is the code before this work (commit `28f074b`). The laptop's CPU governor was on `powersave`, with a load average around 4, so treat the numbers as rough. Launch times run from starting the process to the browser process starting. They include about 5 ms for the test "browser", a shell script.

### How a link is opened quickly

- **Links are routed before the UI loads.** `Main` builds only the routing half of the app (`CoreServices`: config, rules, activity log, router) and opens the browser before Avalonia starts. If every link matched a rule and the app isn't staying in the tray, it exits without loading the UI at all. Links that need the chooser are handed to the UI once it starts. When "keep running in tray" is on, the link opens first and the tray and single-instance server start after it.
- **Native AOT.** `NudgeJudge.Desktop` publishes as an ahead-of-time compiled executable, so no time goes to JIT compilation. The JIT build spends about 0.27 s of CPU compiling on every launch; the native build uses about 0.02 s in total.
- **Smaller startup costs removed.** The config store keeps the last-read file bytes to notice changes made by another process, instead of hashing them, which used to load OpenSSL (about 3 ms). Concurrent GC is off, so no background GC thread is created for a process that usually lives 20 ms.
- **The activity log is written after the browser starts.** Saving it (with `fsync`) takes the process another 40–100 ms to exit, but none of that delays the link.

What's left of the 20 ms is mostly runtime start-up and loading ICU, which international domain names and locale formatting need. On Linux, .NET probes for each ICU version from newest to oldest, which costs about 1 ms.

### How rules are matched quickly

- **Each rule is compiled once.** The compiled form is kept on the rule, with a copy of the conditions it came from. A rule edited in place is noticed and recompiled. The router goes further: it holds the config's rules ready-compiled (`PreparedRules`) and drops them whenever the config changes. That relies on rules only changing through `IConfigStore.Update`, which raises `Changed`.
- **Rules compile when matching first reaches them.** A link that matches the first rule never builds the regexes further down, which matters at startup: building the first regex costs tens of milliseconds.
- **No regex for domains, wildcards or path prefixes.** They're plain string comparisons, about 13–36 ns per rule. A `*` glob is matched by checking its prefix and suffix, then finding the pieces in between left to right. Hosts are lower-cased once per link and compared ordinally. A test checks 544 pattern/URL pairs against the old regex translation and gets identical results.
- **The link is parsed once.** Host, Unicode host and path are worked out once per link, not once per pattern. The full URL is built only if a regex needs it.
- **The router skips the per-rule report.** `FindMatch` returns only the winning rule. The rule tester still uses `Match`, which records what every rule did.
- **User regexes stay on the non-backtracking engine,** at about 0.1–0.3 µs each, and dominate the time when a rule set has many of them. In a benchmark, .NET's compiled backtracking engine was no faster for typical URL patterns, and switching would give up the protection against patterns that hang.

### Measuring it yourself

Use the native build, a throwaway config (`XDG_CONFIG_HOME`), and a "browser" that records when it started, such as a script that writes `date +%s%N` to a file. If you also point `XDG_RUNTIME_DIR` somewhere else, keep that path short. The single-instance socket path is limited to 108 bytes on Linux (104 on macOS). Past that, the server can't start and every launch runs on its own, which looks like forwarding but isn't.

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

# Build the native app (ahead-of-time compiled, no JIT)
dotnet publish NudgeJudge.Desktop -c Release -r linux-x64   # or win-x64
```

**Use the native build day to day.** It opens a routed link in about 20 ms, against about 250 ms for `dotnet run` (see [Performance](#performance)). It needs a C toolchain: `clang` or `gcc` on Linux (gcc is used when clang isn't installed), and Visual Studio's "Desktop development with C++" workload on Windows.

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
  Services/               IWindowService, IDialogService, IThemeService, IUiDispatcher + Avalonia implementations
  Styles/                 Theme.axaml (colour and icon resources), Styles.axaml (shared styles)
  App.axaml(.cs)          Composition root and tray icon

NudgeJudge.Desktop/       Executable for Linux, Windows and macOS; routes launch links before starting the UI; publishes as Native AOT
NudgeJudge.Core.Tests/    xUnit tests for matching, validation, ReDoS safety, argument building, browser discovery and registration
NudgeJudge.UI.Tests/      Avalonia headless tests (xunit v3) that drive real views, e.g. the default-browser setup in Settings
```

Every incoming link goes through `LinkRouter.Route(string)`. That's the command line of the first launch, and links that later launches forward over the single-instance channel. The design system is documented in [DESIGN.md](DESIGN.md), and product context in [PRODUCT.md](PRODUCT.md).

## Current state

**Working**

- [x] Rule engine: named rules with compound conditions (all/any, negation, OR'd alternatives) on the link (four match types) and the source app; ordered first-match-wins, per-rule trace, rules compiled once and recompiled when edited, domain/wildcard/path-prefix matching without regex (see [Performance](#performance)). Configs from before conditions are upgraded on load (the pattern becomes a link condition and, when unnamed, the name)
- [x] Source app detection on Linux (`/proc` parent chain, `.desktop` names, `CHROME_DESKTOP`) and Windows (Toolhelp parent chain with recycled-pid check, file descriptions, foreground window), logged in Activity and forwarded over the single-instance channel
- [x] Temporary rules that expire after N hours or at reboot (Linux `boot_id`; uptime-based on other platforms)
- [x] URL validation, argv-only browser launch, ReDoS-safe matching
- [x] Fast startup: launch links are routed before the UI loads (and the UI is skipped entirely when nothing needs it), with a Native AOT build that opens a routed link in about 20 ms
- [x] JSON config with atomic, owner-only writes, a cross-process lock and reload-before-write, retried failed saves, backups before migration, and read-only mode for configs from newer versions; keeps a copy of a corrupt file and starts from defaults (see [How data is saved](#how-data-is-saved))
- [x] Main window: Rules (live tester + route diagram + editor), Browsers (editor with a preview of the exact command it will run, "try" button), Activity (with filtering), Settings
- [x] Chooser window: tabs for links that arrive together, number-key shortcuts, open once / for N hours / until restart / always
- [x] Tray icon and menu, keep-running-in-tray setting, routing on/off with a primary browser for pass-through
- [x] Light, dark and follow-system themes
- [x] Browser scanning: Linux `.desktop` entries (including Snap and Flatpak) and Windows `StartMenuInternet`, with a private-window entry per browser and Firefox profiles (both `profiles.ini` and the new profile manager) and Chrome/Chromium-family profiles when there's more than one. A scan that adds browsers removes the sample browsers and rules
- [x] Default-browser registration: Linux (`.desktop` entry + `xdg-mime`) and Windows (Capabilities registration + Default Apps hand-off). Status is checked whenever the main window comes forward and catches a half registration (only HTTP or only HTTPS) and a stale one (the app moved since registering). When .NET is a per-user install the app's own launcher can't find without `DOTNET_ROOT` (e.g. `~/.dotnet`, set only in a shell profile), the handler is registered as `<that runtime>/dotnet NudgeJudge.Desktop.dll`, because desktop apps start link handlers without the terminal's environment
- [x] Persistent activity log: rolling file with a configurable limit, an off switch, and "create rule from this entry"
- [x] Single instance with secure local messaging: the first launch holds a per-user lock and runs a server; later launches forward their links (or "show the window") and exit within a fraction of a second. Unix domain socket in an owner-only directory (`$XDG_RUNTIME_DIR/nudgejudge` on Linux), socket `0600`, peer uid checked with `SO_PEERCRED` on Linux; per-user named pipe on Windows (current-user ACL, server owner verified, first-instance check against squatting). A fresh random token per session is stored in a `0600` file and checked in constant time; messages are size-, count- and time-limited; every URL is validated again on receipt. If no server answers within 5 seconds, a launch handles its links itself rather than drop them
- [x] Launch at startup, switched in Settings: an XDG autostart entry on Linux, a per-user `Run` value on Windows. It starts in the tray (`--background`), follows the app if it moves, and reflects changes made in the system's own startup settings
- [x] 788 core test cases and 37 headless UI tests

**Limitations right now**

- Registering from `dotnet run` points the OS at your build output; register again from the installed app once packaging exists. Registration can't be undone from inside the app yet (pick another browser in your system settings instead).
- Default-browser registration and launch at startup aren't available on macOS yet.
- Browser scanning isn't available on macOS yet.
- A launch that only came to open links stays running as the server only when "keep running in tray" is on; otherwise it exits once its links are handled.
- Single-instance mode is untested on Windows and macOS so far.
- The native (AOT) build has only been tested on Linux.
- On Windows, saves aren't yet written through to disk or retried when antivirus briefly locks the file (see [Windows: known gaps and planned fixes](#windows-known-gaps-and-planned-fixes)).
- macOS isn't tested.

## Upcoming work

Roughly in order:

1. **Windows save hardening.** Write-through renames, retries on transient sharing errors, and CI on Windows (see [above](#windows-known-gaps-and-planned-fixes)).
2. **More protocols.** Optional handlers beyond `http`/`https` (e.g. `mailto:`, custom schemes), off by default.
3. **Packaging.** Linux AppImage or Flatpak, Windows installer (MSIX or Inno Setup), and the Linux executable renamed to `nudgejudge`. Packages should ship the native (AOT) build.
4. **macOS.** Real implementations for the stubs (browser discovery via LaunchServices, default-browser registration via `LSSetDefaultHandlerForURLScheme`), a menu-bar icon, and testing.

## Contributing

The project is at an early stage. Before larger changes, read [DESIGN.md](DESIGN.md) for the visual rules and keep view models free of Avalonia window types; go through the interfaces in `NudgeJudge/Services/` instead. New matching or validation logic in `NudgeJudge.Core` should come with tests.

## AI Usage

Designed the primary system architecture and built the core library, while Claude provided AI assistance to generate the testing suite and refine the UI presentation.

### Skills

* UI/UX - [Impeccable](https://github.com/pbakaus/impeccable)
* Agent - [Graphify](https://github.com/Graphify-Labs/graphify)

## License

NudgeJudge is released under the [MIT License](LICENSE).
