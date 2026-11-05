<div align="center">

<img src="NudgeJudge/Assets/nudgejudge.png" alt="NudgeJudge logo" width="112">

# NudgeJudge

**A browser router for the desktop.** Every link goes to the right browser, profile or private window.

[![Release](https://img.shields.io/github/v/release/kushagra10025/Nudge-Judge?include_prereleases&sort=semver&color=C97C06)](https://github.com/kushagra10025/Nudge-Judge/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-1B7F4D)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Avalonia UI](https://img.shields.io/badge/Avalonia-12-8B44AC)
![Platforms](https://img.shields.io/badge/platforms-Linux%20%7C%20Windows-0F1F18)

[Features](docs/features.md) · [Getting started](docs/getting-started.md) · [Browser extension](docs/browser-extension.md) · [Architecture](docs/architecture.md) · [Roadmap](docs/roadmap.md)

</div>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/rules-dark.png">
  <img alt="NudgeJudge's Rules page: a YouTube link opened from Teams passes rules 1 to 3 and is caught by rule 4, which sends it to Chrome Work" src="docs/images/rules-light.png">
</picture>

## What it does

You set NudgeJudge as your system's default browser. When an app opens a link, NudgeJudge checks it against your rules and opens it where you chose: work links in a Chrome work profile, banking in a Firefox private window, everything else in your usual browser. When no rule matches, a small chooser asks where to open the link, and can save your answer as a rule.

![A link from Teams passing rules 1 to 3 and diverting at rule 4 to a Chrome work profile](docs/images/how-it-works.svg)

> [!IMPORTANT]
> **Early development.** The UI, rule engine, browser scanning, default-browser registration, start on login (Linux and Windows), single-instance mode and the native (AOT) build work. Packaging and macOS support aren't built yet. See the [roadmap](docs/roadmap.md).

## Highlights

| | |
|---|---|
| 🚦 **Rules, top to bottom** | Named rules with compound conditions (all/any, negation, alternatives). Match by wildcard, exact domain, path prefix or regex, and by time of day or day of week. |
| 🪟 **Knows where a link came from** | "Opened from" conditions match the app you clicked in, e.g. *YouTube from Teams → Chrome Work*. |
| 🧩 **Browser extension** | <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+click (Linux) or <kbd>Alt</kbd>+<kbd>Shift</kbd>+click (Windows) a link inside Chrome, Edge or Firefox to route it too. Ordinary clicks are never touched. [Details](docs/browser-extension.md) |
| 🧪 **Live rule tester** | Paste a link (<kbd>Ctrl</kbd>+<kbd>T</kbd>) and watch it run past each rule on a route diagram. |
| 🗂️ **Chooser for the rest** | Unmatched links queue as tabs. Press <kbd>1</kbd>–<kbd>9</kbd> to pick, and save the choice for N hours, until restart, or for good. |
| 👤 **Profiles and private windows** | Each browser entry is a program plus arguments. Scanning finds installed browsers, Firefox and Chromium profiles. |
| ⚡ **Fast** | Native AOT build opens a routed link in about **20 ms**; matching 66 rules takes **6.6 µs**. [Details](docs/performance.md) |
| 🔒 **Treats every URL as untrusted** | Strict validation, no shell, ReDoS-safe matching, owner-only files. [Details](docs/security.md) |
| 🔄 **Self-updating** | Checks GitHub Releases and updates in one click (Velopack). |

## Screenshots

<table>
  <tr>
    <td width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/images/chooser-dark.png">
        <img alt="Chooser window with two queued links" src="docs/images/chooser-light.png">
      </picture>
      <p align="center"><sub><b>Chooser</b>: pick a browser for links no rule caught</sub></p>
    </td>
    <td width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/images/activity-dark.png">
        <img alt="Activity log of routed links" src="docs/images/activity-light.png">
      </picture>
      <p align="center"><sub><b>Activity</b>: every link, the app it came from, and where it went</sub></p>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/images/browsers-dark.png">
        <img alt="Browsers page with profiles and a private window" src="docs/images/browsers-light.png">
      </picture>
      <p align="center"><sub><b>Browsers</b>: profiles, private windows, custom flags</sub></p>
    </td>
    <td width="50%">
      <picture>
        <source media="(prefers-color-scheme: dark)" srcset="docs/images/settings-dark.png">
        <img alt="Settings page" src="docs/images/settings-light.png">
      </picture>
      <p align="center"><sub><b>Settings</b>: default browser, routing, chooser, activity</sub></p>
    </td>
  </tr>
</table>

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project NudgeJudge.Desktop                          # open the main window
dotnet run --project NudgeJudge.Desktop -- https://example.com   # simulate the OS handing it a link
dotnet publish NudgeJudge.Desktop -c Release -r linux-x64        # native build (or win-x64)
```

Then open **Settings → Make default**. Full instructions, including the native build's toolchain and first-run samples, are in [Getting started](docs/getting-started.md).

## Installing the browser extension

The extension sends links you click *inside* a browser to NudgeJudge. Until it's in the browser stores, install it by hand from the [latest release](https://github.com/kushagra10025/Nudge-Judge/releases/latest). Use the release that matches your NudgeJudge version.

**First, in NudgeJudge:** open **Settings → Browser extension** and turn on **Take links from the browser extension**.

### Firefox

1. On the release page, right-click **`nudgejudge-extension-<version>-firefox.xpi`** and choose **Save Link As…**.
2. In Firefox, open `about:addons`, click the ⚙ menu and choose **Install Add-on From File…**.
3. Pick the `.xpi` you saved and click **Add**.

The `.xpi` is signed by Mozilla, so it stays installed after a restart. To update, install the newer `.xpi` the same way.

A release with only **`…-firefox-unsigned.zip`** has no signed build. Regular Firefox won't install it permanently. You can try it until Firefox restarts: open `about:debugging#/runtime/this-firefox`, click **Load Temporary Add-on…** and pick the zip.

### Chrome, Edge, Brave, Vivaldi and other Chromium browsers

1. Download **`nudgejudge-extension-<version>-chromium.zip`** from the release page.
2. Unzip it into a folder you'll keep, e.g. `~/NudgeJudge-extension`. The browser loads the extension from that folder every time, so don't delete or move it.
3. Open the browser's extensions page: `chrome://extensions`, `edge://extensions`, `brave://extensions` or `vivaldi://extensions`.
4. Turn on **Developer mode** (top right).
5. Click **Load unpacked** and pick the folder from step 2: the one with `manifest.json` directly in it.

To update, unzip the newer release into the same folder, replacing its contents. Then click the reload button ⟳ on the extension's card.

> [!NOTE]
> Use the `chromium.zip` from the release, not a store package or your own `npm run zip`. Its built-in key gives the extension the ID NudgeJudge accepts. Any other build gets a different ID, and the options page says "NudgeJudge doesn't recognise this copy of the extension".

### Check that it works

Open the extension's options page (right-click its toolbar icon → **Options** in Chromium browsers; **Manage Extension → Options** in Firefox). It should say **Connected to NudgeJudge**.

To send a link:
- **Right-click it → Open with NudgeJudge.** Always available.
- **Modifier-click:** <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+click on Linux, <kbd>Alt</kbd>+<kbd>Shift</kbd>+click on Windows. Click **Allow** under *Click with* on the options page first, then reload tabs that were already open.
- **The current page:** the toolbar button or <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd>.

More, including automatic routing, in [Browser extension](docs/browser-extension.md).

## Documentation

| Doc | What's in it |
|---|---|
| [Features](docs/features.md) | Rules, match types, source-app detection, chooser, activity, updates, default-browser setup |
| [Getting started](docs/getting-started.md) | Build, run, test and first-run setup |
| [Browser extension](docs/browser-extension.md) | Gestures, automatic routing, how it reaches the app, publishing |
| [Security](docs/security.md) | How untrusted URLs and local messaging are handled |
| [How data is saved](docs/data-storage.md) | Config paths, atomic writes, recovery, Windows gaps |
| [Performance](docs/performance.md) | Benchmarks and how startup and matching stay fast |
| [Architecture](docs/architecture.md) | Project layout and how a link flows through the code |
| [Releasing](docs/releasing.md) | Tagging a release and testing updates locally |
| [Status and roadmap](docs/roadmap.md) | What works, current limitations, what's next |
| [DESIGN.md](DESIGN.md) · [PRODUCT.md](PRODUCT.md) | Design system and product context |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## AI usage

Designed the primary system architecture and built the core library, while Claude provided AI assistance to generate the testing suite and refine the UI presentation.

Skills used:

- UI/UX: [Impeccable](https://github.com/pbakaus/impeccable)
- Agent: [Graphify](https://github.com/Graphify-Labs/graphify)

## License

NudgeJudge is released under the [MIT License](LICENSE).
