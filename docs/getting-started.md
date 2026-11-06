# Getting started

[← Back to README](../README.md)

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For the native build: a C toolchain. `clang` or `gcc` on Linux (gcc is used when clang isn't installed), or Visual Studio's "Desktop development with C++" workload on Windows.

## Run from source

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

## Build the native app

```bash
# Ahead-of-time compiled, no JIT
dotnet publish NudgeJudge.Desktop -c Release -r linux-x64   # or win-x64
```

> [!TIP]
> **Use the native build day to day.** It opens a routed link in about 20 ms, against about 250 ms for `dotnet run` (see [Performance](performance.md)).

## First run

On first run, NudgeJudge opens on the **Get started** page, which asks how you want to start: empty, or with a **sample** setup of browsers and rules (marked "Sample" in the UI). Nothing is created unless you pick the samples. They can be removed from the Rules page at any time, and a browser scan replaces them with the browsers you have installed.

Get started then lists the steps to routed links: find your browsers, add a rule, and make NudgeJudge your default browser (the **Make default** button does it in place). Routing is on out of the box, so turning it on only appears as a step if it's been switched off. Until NudgeJudge is the default browser, the sidebar says **Not receiving links** and shows the routing switch on its own row, which takes effect once links arrive. Get started stays first in the sidebar until there are rules and NudgeJudge is the default browser.

Then go to **Settings → Make default** so links reach NudgeJudge. See [Default-browser setup](features.md#default-browser-setup).

Config file locations are listed in [How data is saved](data-storage.md#where).

> [!NOTE]
> **Linux tray:** works on KDE, Cinnamon and most other desktops. GNOME needs the [AppIndicator extension](https://extensions.gnome.org/extension/615/appindicator-support/).
