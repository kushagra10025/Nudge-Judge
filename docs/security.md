# Security

[← Back to README](../README.md)

NudgeJudge receives URLs from any app on the machine, so every URL is treated as untrusted.

| Area | Protection |
|---|---|
| **URL validation** | Only absolute `http`/`https` URLs with a host are accepted (max 8,192 characters, no whitespace or control characters). Everything else is rejected and logged. |
| **No shell** | Browsers are started with an explicit argument list (`ProcessStartInfo.ArgumentList`), so neither the URL nor an argument template can inject commands. A validated URL always starts with `http(s):`, so the browser can't read it as a command-line flag. |
| **ReDoS protection** | Domain, wildcard and path-prefix patterns never touch a regex engine: they compile to plain string comparisons (a `*` glob is matched by finding its literal pieces left to right). Regular expressions run on .NET's non-backtracking engine (`RegexOptions.NonBacktracking`), which matches in linear time. Patterns are limited to 512 characters and each regex match has a 50 ms timeout. Lookarounds, backreferences and atomic groups are rejected when you save the rule. |
| **Private config** | The config file and activity log are written atomically with owner-only permissions (`0600` file, `0700` directory on Unix). See [How data is saved](data-storage.md). |
| **Import** | An import file is untrusted. It's refused unless it's a valid export within size and count limits, with no control characters in programs or arguments. Names are stripped of control and text-direction characters. Nothing is applied before the user reviews it. A browser's program or arguments never change without an explicit tick, and an imported browser that isn't a recognised browser, is a script, isn't installed, or has risky flags is marked *needs review*: no link is opened in it until the user saves it in the browser editor. See [Export and import](features.md#export-and-import). |
| **Handler registration** | The OS tools are run with explicit argument lists, never a shell. The Linux `Exec` line follows the Desktop Entry quoting rules, and a test checks that it parses back to the same argv. Launch paths containing control characters are refused. Every argument the OS passes in is validated as a URL like any other link. |

## Browser extension

The [browser extension](browser-extension.md) reaches the app through native messaging, not a port or URL scheme.

- **Who can connect:** the browser only starts the host for the extension IDs listed in its manifest. The manifest and launcher are written per user (no admin), and the launcher quotes every argument.
- **What the host accepts:** every URL is validated like any other link, and requests are capped at 64 KB. A malformed frame ends the session.
- **Where links go:** the host never opens a browser itself. Links go to the running instance over the single-instance channel below, or to a fresh launch of the app with its own stdio, so the browser's pipe never leaks into another browser.
- **In the page:** the extension only acts on trusted clicks (`event.isTrusted`), so a page can't script fake gestures. Its content script and navigation access are optional permissions, off until the user grants them.

## Single-instance messaging

The first launch holds a per-user lock and runs a server; later launches forward their links (or "show the window") and exit within a fraction of a second.

- **Linux / macOS:** Unix domain socket in an owner-only directory (`$XDG_RUNTIME_DIR/nudgejudge` on Linux), socket `0600`, peer uid checked with `SO_PEERCRED` on Linux.
- **Windows:** per-user named pipe with a current-user ACL, the server's owner verified, and a first-instance check against squatting.
- **Both:** a fresh random token per session is stored in a `0600` file and checked in constant time; messages are size-, count- and time-limited; every URL is validated again on receipt.

If no server answers within 5 seconds, a launch handles its links itself rather than drop them.
