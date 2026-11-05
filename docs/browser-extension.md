# Browser extension

[← Back to README](../README.md)

NudgeJudge routes links that *other apps* open. Links you click *inside* a browser never leave it. The companion
extension closes that gap: with a deliberate gesture, a link you click in the browser goes to NudgeJudge, which
opens it wherever your rules say.

It's built to stay out of the way. Ordinary clicks are never touched; only an explicit gesture sends a link.

## Setting it up

1. In NudgeJudge, open **Settings → Browser extension** and turn on **Take links from the browser extension**. This
   tells your browsers where to find NudgeJudge.
2. Install the extension in each browser you use: Chrome, Edge, Brave, Vivaldi, Chromium or Firefox.
3. Open the extension's options page. It should say **Connected to NudgeJudge**. To use the modifier-click, pick it
   there and allow access to all sites.

**From source:** run `npm install` and `npm run build` in `NudgeJudge.Extension/`. Then:
- **Chromium browsers:** turn on developer mode in `chrome://extensions`, click *Load unpacked* and pick
  `.output/chrome-mv3`.
- **Firefox:** run `npm run build:firefox`, then in `about:debugging#/runtime/this-firefox` click *Load Temporary
  Add-on* and pick `.output/firefox-mv3/manifest.json`.

## How it works

```
gesture in a page ─► content script ─► background worker
   ─► browser.runtime.sendNativeMessage("com.nudgejudge.host", { type: "open", url })
   ─► the browser starts NudgeJudge in native-host mode (a ~20 ms AOT start)
        ├─ the rules pick the browser the link is already in → reply "stay": the link opens here as usual
        └─ otherwise → hand the link to the running instance (or start the app with it) → reply "sent"
```

The extension talks to the app through [native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging):
the browser starts a program that the app registered, and exchanges JSON with it over stdin/stdout. Only the
extension IDs listed in the app's registration can reach it. There's no prompt, no open port and no resident
process.

We ruled out the other two options:

| Option | Why not |
|---|---|
| Custom URL scheme (`nudgejudge://…`) | The browser asks for confirmation on every link (in Chrome, once per site). |
| Localhost HTTP server | Any web page can post to it, and it needs a resident port. |

## Gestures

| Gesture | What it sends | If the rules keep it in this browser |
|---|---|---|
| **Right-click → Open with NudgeJudge** | The link | It opens in a new background tab, like *Open link in new tab*. |
| **Modifier + click** (default <kbd>Alt</kbd>+<kbd>Shift</kbd>+click, <kbd>⌥</kbd><kbd>⇧</kbd>+click on macOS) | The link | It's followed like a plain click (same tab, or its own target). |
| **Toolbar button** or <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd> | The current page | Nothing happens; a green ✓ shows on the button. |

The browser already uses these modifier clicks: <kbd>Ctrl</kbd>/<kbd>⌘</kbd>+click opens a new tab,
<kbd>Shift</kbd>+click a new window, <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+click a foreground tab, <kbd>Alt</kbd>+click
downloads (Chrome), and <kbd>Ctrl</kbd>+click right-clicks on macOS. <kbd>Alt</kbd>+<kbd>Shift</kbd> is free in
browsers. Some Linux desktops grab <kbd>Alt</kbd>+click to move windows, so the options page offers alternatives:
<kbd>Ctrl</kbd>+<kbd>Alt</kbd>, <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd>, or off. A gesture matches only
its exact modifiers, so it never fires alongside one of the browser's.

When a send fails, a red **!** shows on the toolbar button, and its tooltip says why. If NudgeJudge can't be reached
after something you did on purpose (toolbar, shortcut, menu), the options page opens with setup instructions.

The content script ignores synthetic events (`event.isTrusted` must be true), so a page can't script fake modified
clicks to push links into your router. On "stay" it clicks the link again without modifiers. That honours the
link's target, its frame and the page's own handlers. That click isn't trusted, so it doesn't come back to us.

## Permissions

The basic install asks only for `nativeMessaging`, `contextMenus`, `storage`, `activeTab` and `scripting`. These
cover the context menu, the toolbar button and the shortcut, and show no "read your data on all websites" warning.

Two features need optional permissions, which the options page requests when you turn them on:

| Feature | Optional permission | Why |
|---|---|---|
| Modifier-click | `<all_urls>` | A content script has to listen for the click on every page. It's registered at runtime (`scripting.registerContentScripts`) only while the gesture is on and allowed. Tabs that were already open need a reload. |
| Automatic routing | `<all_urls>` + `webNavigation` | To see navigations as they start. |

## Automatic routing (opt-in)

In NudgeJudge, a rule can be marked **Also catch in the browser** (in the rule editor, under *Open in*).

1. **The extension gets a list of sites.** It asks for the hosts of the marked rules (`{type: "autoRoutes"}`), keeps
   the list for the browser session and refreshes it every 5 minutes. Only host-based rules qualify: domain,
   wildcard or path-prefix links. If a rule has a regex, or a wildcard that could match any host, the editor says so
   and the rule isn't watched.
2. **A navigation to one of those sites is checked.** This covers link clicks, typed addresses and redirects in the
   top frame of a tab. The extension sends the URL to NudgeJudge with `auto: true`, and NudgeJudge runs its full
   rules on it.
3. **If the link went elsewhere, the tab is put back.** It goes back in history, or returns to the previous page if
   the new one hadn't loaded yet. A tab opened just for that link is closed. If the rules pick this browser, nothing
   happens.

Two guards keep a link from bouncing between browsers:
- **The extension leaves alone tabs that came from outside the browser.** A tab with no page before this one and no
  opener tab is one NudgeJudge or another app opened.
- **NudgeJudge answers "stay" for an automatic request about a link it opened in the last 15 seconds.** It checks
  this against the Activity log, so the guard relies on the log being on.

## Protocol

Messages are JSON. Native messaging frames them with a 4-byte length in the machine's byte order (little-endian on
every platform we ship). Requests from the browser are capped at 64 KB.

| Request | Reply |
|---|---|
| `{ "v": 1, "type": "ping" }` | `{ "v": 1, "ok": true, "action": "pong", "app": "1.4.0" }` |
| `{ "v": 1, "type": "open", "url": "https://…", "auto": false }` | `{ "v": 1, "ok": true, "action": "sent" \| "stay" }` |
| `{ "v": 1, "type": "autoRoutes" }` | `{ "v": 1, "ok": true, "action": "autoRoutes", "hosts": ["*.zoom.us", "meet.google.com"] }` |
| Anything invalid | `{ "v": 1, "ok": false, "error": "…" }` |

- **`stay`:** the link would open in the browser that sent it. "The same browser" means the same browser family
  (Chrome, Firefox, Edge…), started with no extra arguments. A private window, a named profile, or any other argument
  never counts: the app can't tell which profile the click came from, so it opens those explicitly. The caller is
  found from the native host's parent process.
- **`sent`:** NudgeJudge has the link. The running instance routes it, or the app is started with it. Activity logs
  it with the calling browser as its source, so "opened from" conditions match on it (e.g. *links from Firefox*).

The C# side is `NudgeJudge.Core/NativeMessaging/NativeMessaging.cs`. The TypeScript side is
`NudgeJudge.Extension/utils/protocol.ts`. Change both together, and bump `v` for anything incompatible.

## App side

| Piece | Where |
|---|---|
| Framing, request handling | `NudgeJudge.Core/NativeMessaging/NativeMessaging.cs` |
| The "stay" decision, auto-route hosts | `NudgeJudge.Core/NativeMessaging/NativeHostBackend.cs` |
| Launcher and manifests | `NudgeJudge.Core/NativeMessaging/NativeHostRegistration.cs` |
| Native-host mode (`--native-host`) | `NudgeJudge.Desktop/Program.cs` |
| Settings → Browser extension | `NudgeJudge/ViewModels/SettingsViewModel.cs` |
| Rule editor → Also catch in the browser | `NudgeJudge/ViewModels/RuleEditorViewModel.cs` |

Turning the setting on writes:

- **A launcher in the config folder:** `native-host.sh` (Linux) or `native-host.bat` (Windows). It runs the app with
  `--native-host` and passes on the browser's arguments. A manifest can name only one program and no arguments, and
  in development the app runs as `dotnet NudgeJudge.Desktop.dll`.
- **A host manifest `com.nudgejudge.host.json` for each browser:**
  - Linux: `~/.mozilla/native-messaging-hosts/` (and `$XDG_CONFIG_HOME/mozilla/…` when that exists), plus
    `NativeMessagingHosts/` in each Chromium browser's config folder that exists: Chrome (stable, beta, dev),
    Chromium, Brave, Edge (stable, beta, dev) and Vivaldi.
  - Windows: two manifests in the config folder, pointed to from
    `HKCU\Software\{Google\Chrome, Chromium, Microsoft\Edge, BraveSoftware\Brave-Browser, Mozilla}\NativeMessagingHosts\com.nudgejudge.host`.
    Vivaldi reads Chrome's key.

The registration is refreshed every time the UI starts, the same way the startup entry follows the app when it
moves; that also covers browsers installed since. On Windows, the uninstaller removes it.

How host mode handles a request:

- **Its own output:** it runs before Velopack, clears trace listeners and swaps `Console.Out`, so only protocol frames
  reach stdout.
- **Handing links over:** it never opens a browser itself. If no instance is running, it starts the app with the link
  and a `--source=` argument carrying the calling browser. That app gets fresh stdio, and on Windows the host's own
  handles are made non-inheritable first, so the extension's pipe never leaks into a browser.

### Limitations

- **Flatpak and Snap browsers** run sandboxed and can't start native hosts (Flatpak Firefox would need the
  WebExtensions portal). The options page shows "Can't reach NudgeJudge".
- **macOS** isn't wired up yet, like the rest of the macOS port.
- **"Stay" can't see profiles.** A rule that targets a specific Chrome profile always opens that profile, even when
  you clicked in it already. The link then opens in a new tab there rather than in place.

## Extension side

The extension lives in [`NudgeJudge.Extension/`](../NudgeJudge.Extension/). It's built with [WXT](https://wxt.dev),
in plain TypeScript with no UI framework, and isn't part of the .NET solution. It's Manifest V3 in every browser,
Firefox included (140+), so there's one permission model.

```
NudgeJudge.Extension/
  wxt.config.ts            the manifest (dev key for Chromium, gecko ID for Firefox)
  entrypoints/
    background.ts          native messaging, context menu, toolbar, shortcut, automatic routing
    content.ts             modifier-click (registered at runtime, only when allowed)
    options/               the only UI: connection status, gesture, automatic routing
  utils/                   protocol, gesture matching, host globs, settings
  tests/                   vitest unit tests
```

```sh
cd NudgeJudge.Extension
npm install
npm run dev            # Chrome with the extension loaded and live reload
npm run dev:firefox
npm test               # unit tests
npm run compile        # type check
npm run zip && npm run zip:firefox   # store packages in .output/
```

### Extension IDs

The host manifests list the IDs that may connect. They're kept in `NativeHostRegistration`:

- **Chromium dev and local builds:** pinned by the public `key` in `wxt.config.ts`. ID:
  `bmfgmoobkfipdpiabahjlafpfehjkjfg`. `npm run zip` builds in `store` mode, which leaves the key out, because the
  stores assign their own ID.
- **Firefox:** `extension@nudgejudge` (`browser_specific_settings.gecko.id`), the same everywhere.
- **Chrome Web Store / Edge Add-ons:** each store assigns an ID on first upload. Add both to
  `NativeHostRegistration.ChromiumExtensionIds` and ship that app release before (or with) the store listing,
  otherwise store installs get "NudgeJudge doesn't recognise this copy of the extension".

## Publishing

`.github/workflows/extension.yml` runs on every change under `NudgeJudge.Extension/`. It type-checks and tests the
extension, then keeps `nudgejudge-extension-<version>-{chrome,firefox,sources}.zip` as the run's artifact. It
deliberately doesn't create a GitHub release: the app's updater reads this repository's releases, and a release
without Velopack's feed mustn't become the latest.

To publish a version:

1. Bump `version` in `NudgeJudge.Extension/package.json`, then merge.
2. Download the artifact from that workflow run.
3. Upload it to each store:
   - **Chrome Web Store** ([dashboard](https://chrome.google.com/webstore/devconsole)): the `chrome` zip. On first
     upload, copy the item ID into `ChromiumExtensionIds`.
   - **Edge Add-ons** ([Partner Center](https://partner.microsoft.com/dashboard/microsoftedge)): the same `chrome`
     zip. Add its item ID too.
   - **Firefox Add-ons** ([AMO](https://addons.mozilla.org/developers/)): the `firefox` zip, plus the `sources` zip
     when AMO asks for source code (build with `npm ci && npm run build:firefox`).

For reviewers, explain `nativeMessaging` as: "Sends the link the user chose to the NudgeJudge desktop app, which opens
it in the browser the user's rules pick." The optional `<all_urls>` and `webNavigation` permissions are only
requested for the modifier-click and automatic routing.

## Plan

| Step | Status |
|---|---|
| 1. **App: native host.** Host mode, framing, the "stay" decision, registrar, Settings switch | ✅ Done |
| 2. **Extension, first version.** Context menu, toolbar button and shortcut, options page with connection status | ✅ Done |
| 3. **Modifier-click** behind the optional permission, with "stay" handling in the page | ✅ Done |
| 4. **Store submissions.** CI builds the packages; the uploads are manual (above). Waiting on store accounts and IDs | ⏳ To do |
| 5. **Automatic routing.** Rules marked *Also catch in the browser* | ✅ Done |
