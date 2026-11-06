# Status and roadmap

[← Back to README](../README.md)

## Working

- [x] Rule engine: named rules with compound conditions (all/any, negation, OR'd alternatives) on the link (four match types) and the source app; ordered first-match-wins, per-rule trace, rules compiled once and recompiled when edited, domain/wildcard/path-prefix matching without regex (see [Performance](performance.md)). Configs from before conditions are upgraded on load (the pattern becomes a link condition and, when unnamed, the name)
- [x] Source app detection on Linux (`/proc` parent chain, `.desktop` names, `CHROME_DESKTOP`) and Windows (Toolhelp parent chain with recycled-pid check, file descriptions, foreground window), logged in Activity and forwarded over the single-instance channel
- [x] Temporary rules that expire after N hours or at reboot (Linux `boot_id`; uptime-based on other platforms)
- [x] URL validation, argv-only browser launch, ReDoS-safe matching
- [x] Fast startup: launch links are routed before the UI loads (and the UI is skipped entirely when nothing needs it), with a Native AOT build that opens a routed link in about 20 ms
- [x] JSON config with atomic, owner-only writes, a cross-process lock and reload-before-write, retried failed saves, backups before migration, and read-only mode for configs from newer versions; keeps a copy of a corrupt file and starts from defaults (see [How data is saved](data-storage.md))
- [x] Get started page for first launch: start empty or from samples, then find browsers, add a first rule and make NudgeJudge the default browser in place; leaves the sidebar once setup is done
- [x] Main window: Rules (live tester + route diagram + editor), Browsers (editor with a preview of the exact command it will run, "try" button), Activity (with filtering), Settings
- [x] Chooser window: tabs for links that arrive together, number-key shortcuts, open once / for N hours / until restart / always
- [x] Tray icon and menu, keep-running-in-tray setting, routing on/off with a primary browser for pass-through
- [x] Light, dark and follow-system themes
- [x] Browser scanning: Linux `.desktop` entries (including Snap and Flatpak) and Windows `StartMenuInternet`, with a private-window entry per browser and Firefox profiles (both `profiles.ini` and the new profile manager) and Chrome/Chromium-family profiles when there's more than one. A scan that adds browsers removes the sample browsers and rules
- [x] Default-browser registration: Linux (`.desktop` entry + `xdg-mime`) and Windows (Capabilities registration + Default Apps hand-off). Status is checked whenever the main window comes forward and catches a half registration (only HTTP or only HTTPS) and a stale one (the app moved since registering). When .NET is a per-user install the app's own launcher can't find without `DOTNET_ROOT` (e.g. `~/.dotnet`, set only in a shell profile), the handler is registered as `<that runtime>/dotnet NudgeJudge.Desktop.dll`, because desktop apps start link handlers without the terminal's environment
- [x] Persistent activity log: rolling file with a configurable limit, an off switch, and "create rule from this entry"
- [x] Single instance with secure local messaging (see [Security](security.md#single-instance-messaging))
- [x] Launch at startup, switched in Settings: an XDG autostart entry on Linux, a per-user `Run` value on Windows. It starts in the tray (`--background`), follows the app if it moves, and reflects changes made in the system's own startup settings
- [x] Update checks and one-click updates from GitHub Releases (Velopack), with a sidebar notice and Settings → Updates / About
- [x] Browser extension (Chrome, Edge, Brave, Vivaldi, Firefox): a modifier-click (Ctrl+Alt on Linux, Alt+Shift on Windows), right-click and toolbar send links to NudgeJudge over native messaging; links the rules keep in that browser just open there; opt-in automatic routing for rules marked "Also catch in the browser". See [Browser extension](browser-extension.md)
- [x] 966 core test cases and 72 headless UI tests, plus the extension's unit tests

## Limitations right now

- Registering from `dotnet run` points the OS at your build output; register again from the installed app once packaging exists. Registration can't be undone from inside the app yet (pick another browser in your system settings instead). Uninstalling on Windows removes it, along with the startup entry, when it points at the copy being uninstalled.
- Default-browser registration and launch at startup aren't available on macOS yet.
- Browser scanning isn't available on macOS yet.
- A launch that only came to open links stays running as the server only when "keep running in tray" is on; otherwise it exits once its links are handled.
- Single-instance mode is untested on Windows and macOS so far.
- The native (AOT) build has only been tested on Linux.
- On Windows, saves aren't yet written through to disk or retried when antivirus briefly locks the file (see [Windows: known gaps and planned fixes](data-storage.md#windows-known-gaps-and-planned-fixes)).
- macOS isn't tested.

## Upcoming work

Roughly in order:

1. **Windows save hardening.** Write-through renames, retries on transient sharing errors, and CI on Windows (see [Windows: known gaps](data-storage.md#windows-known-gaps-and-planned-fixes)).
2. **More protocols.** Optional handlers beyond `http`/`https` (e.g. `mailto:`, custom schemes), off by default.
3. **Browser extension in the stores.** First uploads to the Chrome Web Store, Edge Add-ons and Firefox Add-ons, then their IDs in the host manifest (see [Publishing](browser-extension.md#publishing)).
4. **Packaging.** The Velopack release workflow (Windows `Setup.exe`, Linux AppImage) needs a first real run; then a Flatpak, and the Linux executable renamed to `nudgejudge`.
5. **macOS.** Real implementations for the stubs (browser discovery via LaunchServices, default-browser registration via `LSSetDefaultHandlerForURLScheme`), a menu-bar icon, and testing.
