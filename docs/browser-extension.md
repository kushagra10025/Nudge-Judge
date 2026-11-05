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
3. Open the extension's options page. It should say **Connected to NudgeJudge**. To use the modifier-click, click
   **Allow** under *Click with* to give it access to all sites, then reload tabs that were already open.

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
| **Modifier + click**: by default <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+click on Linux, <kbd>Alt</kbd>+<kbd>Shift</kbd>+click on Windows, <kbd>⌥</kbd><kbd>⇧</kbd>+click on macOS | The link | It's followed like a plain click (same tab, or its own target). |
| **Toolbar button** or <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd> | The current page | Nothing happens; a green ✓ shows on the button. |

Why the defaults differ:

- **The browser already uses these modifier clicks:** <kbd>Ctrl</kbd>/<kbd>⌘</kbd>+click opens a new tab,
  <kbd>Shift</kbd>+click a new window, <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+click a foreground tab, <kbd>Alt</kbd>+click
  downloads (Chrome), and <kbd>Ctrl</kbd>+click right-clicks on macOS.
- **<kbd>Alt</kbd>+<kbd>Shift</kbd> is free in every browser, but not on many Linux desktops.** GNOME, Cinnamon and
  Xfce use <kbd>Alt</kbd>+click to move windows and <kbd>Alt</kbd>+<kbd>Shift</kbd>+click to snap them, so the click
  never reaches the page. <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+click is free there.
- **macOS can't use <kbd>Ctrl</kbd>,** because <kbd>Ctrl</kbd>+click is right-click.

The options page lets you pick any of <kbd>Alt</kbd>+<kbd>Shift</kbd>, <kbd>Ctrl</kbd>+<kbd>Alt</kbd>,
<kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>Shift</kbd> or off, and marks your platform's default. A gesture matches only its
exact modifiers, so it never fires alongside one of the browser's.

The options page's **Recent links** list shows the last 20 links the extension sent, newest first. Each line has the
site (only the host is kept), what sent it (right-click, toolbar or shortcut, modifier-click, automatic) and what
happened:

| Lamp | Label | Meaning |
|---|---|---|
| hollow ring | Stayed here | Your rules pick this browser; the link opened here as usual. |
| green | Sent to running NudgeJudge | NudgeJudge was already running (window or tray) and routed it. |
| filled ink | Started NudgeJudge | NudgeJudge wasn't running, so the link started it, separately from the browser. |
| red ring | Started NudgeJudge, tied to this browser | Started, but it closes when this browser does (see [below](#is-nudgejudge-tied-to-the-browser)). |
| red | Failed | Couldn't be sent; the reason follows. |

Hover a line for the explanation. The list updates live while the page is open, and **Clear** empties it.

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

The gestures send a link when *you* decide to. Automatic routing sends it when *a rule* says to, for the sites you've
marked, with no special keys. It's made of two switches, and both are off by default:

| Where | Switch | What it means |
|---|---|---|
| NudgeJudge, rule editor (under *Open in*) | **Also catch in the browser** | "Enforce this rule even for links I open inside a browser." |
| Extension, options page | **Catch links for rules marked "Also catch in the browser"** | "This browser may watch where its tabs are going." |

### An example

You have a rule **`*.zoom.us` → Firefox** with *Also catch in the browser* ticked, and you're in Chrome with automatic
routing on. You click a Zoom link in Gmail, a plain click:

1. **The extension already knows which sites to watch.** It asked NudgeJudge earlier (`{type: "autoRoutes"}`) and got
   `*.zoom.us`. It keeps the list for the browser session and refreshes it every 5 minutes.
2. **Chrome starts going to `us02web.zoom.us/j/…`.** That host is on the list.
3. **The extension sends the link to NudgeJudge,** which runs **all** your rules on it, not just the marked one. The
   list only decides what's worth asking about.
4. **The link opens in Firefox, and the Chrome tab goes back to Gmail.** If the new page had already loaded, the tab
   goes back in history; if not, it returns to the previous page. A tab opened just for that link is closed.

If the rules had picked Chrome, nothing would happen and the page would load normally.

### Which rules can be caught

Only rules that can be narrowed to sites: domain, wildcard or path-prefix link conditions. For a rule with a regex,
a wildcard that could match any host (like `*` or `*/meeting`), or only an "opened from" or time condition, the
extension would have to ask about every page you visit. The editor shows a red note under the checkbox, and the rule
isn't watched.

### What it leaves alone

- **Pages inside frames.** Only the top frame of a tab is watched.
- **Tabs that came from outside the browser.** A tab with no earlier page and no opener tab was opened by an app,
  usually NudgeJudge itself. Sending its link back could bounce it between browsers.
- **A link NudgeJudge opened in the last 15 seconds.** NudgeJudge answers "stay" for it. This check uses the Activity
  log, so it only works while the log is on.
- **The same link twice in 10 seconds,** for redirect chains and reloads.

### What it needs

The extension's options page asks for access to all sites and for `webNavigation` (to see navigations as they
start) when you turn it on. NudgeJudge has to be reachable, but doesn't have to be running (see below).

## When NudgeJudge isn't running

The extension works even when NudgeJudge is closed and not in the tray, because the browser starts NudgeJudge itself.

Turning on **Settings → Browser extension** saved a launcher script and told each browser where it is. Those files stay
after you quit NudgeJudge; only turning the setting off (or uninstalling) removes them. On every send, the browser runs
the launcher, which starts a short-lived **host** process that reads your rules, answers, and exits:

| Situation | What happens |
|---|---|
| The rules keep the link in this browser ("stay") | The host answers "stay" and exits. Nothing else starts. |
| NudgeJudge is running (window or tray) | The host hands the link to it over the single-instance channel and exits. |
| NudgeJudge isn't running, *keep running in the tray* on (the default) | The host starts NudgeJudge with the link and exits. NudgeJudge routes it, becomes the primary instance and moves to the tray, so it's the server for links from other apps and from the extension from then on. |
| NudgeJudge isn't running, *keep running in the tray* off | The same, but NudgeJudge exits once the link is routed (or once you've chosen in the chooser), as it does for a link from any other app. You asked for nothing to stay running. |
| Two links arrive while NudgeJudge is still starting | Both hosts may start it; the second launch finds the first holding the single-instance lock and hands its link over. Nothing opens twice. |

The first link after a cold start is a little slower, the time it takes NudgeJudge to start.

### Nothing is left orphaned or tied to the browser

There are two processes to think about: the host, and the NudgeJudge it may start.

- **The host never outlives its request.** The browser sends one message and closes the host's stdin, and the host
  exits on that. If the browser crashes, stdin closes too. The longest a host can wait is the 15-second reply timeout
  of a running NudgeJudge that stopped answering.
- **A NudgeJudge started by the host belongs to no one.** The host exits at once, so NudgeJudge is re-parented by the
  OS like any background app, and it's started detached from the browser on purpose. Browsers clean up after native
  programs: they may kill the host's whole process group (Linux), and Firefox runs them in a Windows job that kills
  everything in it when it closes. NudgeJudge mustn't go down with the host, or closing the browser would close
  NudgeJudge. So it's started (`DetachedProcess`):
  - **On Linux,** in a session of its own (`setsid`), with stdin, stdout and stderr on `/dev/null`.
  - **On Windows,** outside the browser's job (`CREATE_BREAKAWAY_FROM_JOB`, which Firefox's job allows), in its own
    process group, inheriting no handles.

  Either way the browser's pipes never reach NudgeJudge or the browsers it opens.
- **Closing the browser leaves NudgeJudge running in the tray,** exactly as if a link from another app had started
  it. Quit it from the tray menu as usual.

### Is NudgeJudge tied to the browser?

A browser whose job doesn't allow breakaway refuses the flag. NudgeJudge is then started inside the job rather than not
at all, and it closes when that browser does. The tray host and window processes it starts end up in the same job.
Firefox allows breakaway; this path hasn't been tested on Windows yet. When it happens, you can tell from either side:

- **In the extension.** The host knows it couldn't break away and replies `via: "startedTied"`. The send shows in
  **Recent links** as *Started NudgeJudge, tied to this browser*. A grey **!** appears on
  the toolbar button, with a tooltip explaining what happened. The options page shows *NudgeJudge is running inside this
  browser*, with the time, until NudgeJudge is next started separately or you dismiss it.
- **In Activity.** Every NudgeJudge process checks once at start-up whether it's inside a kill-on-close job
  (`ProcessJob.ClosesWithLauncher`, using `QueryInformationJobObject`). If it is, every link it handles is marked
  *Handled by a NudgeJudge that closes with the program that started it*. Links handled by a NudgeJudge running on its
  own carry no mark. The check covers any launcher with such a job, so a terminal or IDE that starts NudgeJudge this
  way is caught too.

To avoid it, start NudgeJudge yourself, or turn on **Launch at startup**. A NudgeJudge started at sign-in is never in a
browser's job, and every later link goes to it (`via: "running"`).

On Linux the same thing would only happen without `setsid(1)`. That's part of util-linux, which every mainstream distro
installs. The extension reports it the same way, but Activity doesn't mark it.

## Protocol

Messages are JSON. Native messaging frames them with a 4-byte length in the machine's byte order (little-endian on
every platform we ship). Requests from the browser are capped at 64 KB.

| Request | Reply |
|---|---|
| `{ "v": 1, "type": "ping" }` | `{ "v": 1, "ok": true, "action": "pong", "app": "1.4.0" }` |
| `{ "v": 1, "type": "open", "url": "https://…", "auto": false }` | `{ "v": 1, "ok": true, "action": "stay" }`, or `{ "v": 1, "ok": true, "action": "sent", "via": "running" \| "started" \| "startedTied" }` |
| `{ "v": 1, "type": "autoRoutes" }` | `{ "v": 1, "ok": true, "action": "autoRoutes", "hosts": ["*.zoom.us", "meet.google.com"] }` |
| Anything invalid | `{ "v": 1, "ok": false, "error": "…" }` |

- **`stay`:** the link would open in the browser that sent it. "The same browser" means the same browser family
  (Chrome, Firefox, Edge…), started with no extra arguments. A private window, a named profile, or any other argument
  never counts: the app can't tell which profile the click came from, so it opens those explicitly. The caller is
  found from the native host's parent process.
- **`via`** (with `sent`) says how the link got there: `running` means handed to a running NudgeJudge, `started` means
  NudgeJudge was started separately from the browser, and `startedTied` means it was started but couldn't leave the
  browser's job (see [below](#is-nudgejudge-tied-to-the-browser)).
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
  and a `--source=` argument carrying the calling browser, fully detached (`DetachedProcess`, see
  [below](#when-nudgejudge-isnt-running)).

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
