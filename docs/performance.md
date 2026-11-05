# Performance

[← Back to README](../README.md)

NudgeJudge sits between every link and your browser, so it's built to add as little delay as possible.

| Measured on one Linux laptop | Before | Now |
|---|---|---|
| Launch to browser start, native build | ~1.4 s | **22 ms** (median of 15 runs) |
| Launch to browser start, `dotnet run` build | ~1.4 s | 249 ms |
| A later launch handing its link to the running instance, native build | | 15–44 ms |
| Matching one link against 66 rules | 264 µs, 75 KB allocated | **6.6 µs, 64 B** |
| First match, including compiling the rules (JIT build) | 495 ms | 230 ms |

"Before" is the code before this work (commit `28f074b`). The laptop's CPU governor was on `powersave`, with a load average around 4, so treat the numbers as rough. Launch times run from starting the process to the browser process starting. They include about 5 ms for the test "browser", a shell script.

## How a link is opened quickly

- **Links are routed before the UI loads.** `Main` builds only the routing half of the app (`CoreServices`: config, rules, activity log, router) and opens the browser before Avalonia starts. If every link matched a rule and the app isn't staying in the tray, it exits without loading the UI at all. Links that need the chooser are handed to the UI once it starts. When "keep running in tray" is on, the link opens first, and then the launch hands over to the tray host.
- **The tray holds a few MB, not the UI.** In the tray, only the small `NudgeJudge.Tray` executable runs: about 3–5 MB of private memory (18–25 MB resident, mostly shared libraries), against about 32 MB private and 127 MB resident when the hidden Avalonia app sat there. Windows and the chooser run in a separate process that exits when they close. The cost of this is that a link needing the chooser starts that process: the chooser is on screen about 0.2 s after the link (0.2–0.24 s over six runs, native build), most of it Avalonia starting up. Links that match a rule are unaffected, and a later launch still gets its answer from the host in about 0.1 s. See [Architecture → Processes](architecture.md#processes).
- **Native AOT.** `NudgeJudge.Desktop` publishes as an ahead-of-time compiled executable, so no time goes to JIT compilation. The JIT build spends about 0.27 s of CPU compiling on every launch; the native build uses about 0.02 s in total.
- **Smaller startup costs removed.** The config store keeps the last-read file bytes to notice changes made by another process, instead of hashing them, which used to load OpenSSL (about 3 ms). Concurrent GC is off, so no background GC thread is created for a process that usually lives 20 ms.
- **The activity log is written after the browser starts.** Saving it (with `fsync`) takes the process another 40–100 ms to exit, but none of that delays the link.

What's left of the 20 ms is mostly runtime start-up and loading ICU, which international domain names and locale formatting need. On Linux, .NET probes for each ICU version from newest to oldest, which costs about 1 ms.

## How rules are matched quickly

- **Each rule is compiled once.** The compiled form is kept on the rule, with a copy of the conditions it came from. A rule edited in place is noticed and recompiled. The router goes further: it holds the config's rules ready-compiled (`PreparedRules`) and drops them whenever the config changes. That relies on rules only changing through `IConfigStore.Update`, which raises `Changed`.
- **Rules compile when matching first reaches them.** A link that matches the first rule never builds the regexes further down, which matters at startup: building the first regex costs tens of milliseconds.
- **No regex for domains, wildcards or path prefixes.** They're plain string comparisons, about 13–36 ns per rule. A `*` glob is matched by checking its prefix and suffix, then finding the pieces in between left to right. Hosts are lower-cased once per link and compared ordinally. A test checks 544 pattern/URL pairs against the old regex translation and gets identical results.
- **The link is parsed once.** Host, Unicode host and path are worked out once per link, not once per pattern. The full URL is built only if a regex needs it.
- **The router skips the per-rule report.** `FindMatch` returns only the winning rule. The rule tester still uses `Match`, which records what every rule did.
- **User regexes stay on the non-backtracking engine,** at about 0.1–0.3 µs each, and dominate the time when a rule set has many of them. In a benchmark, .NET's compiled backtracking engine was no faster for typical URL patterns, and switching would give up the protection against patterns that hang.

## Measuring it yourself

Use the native build, a throwaway config (`XDG_CONFIG_HOME`), and a "browser" that records when it started, such as a script that writes `date +%s%N` to a file.

> [!WARNING]
> If you also point `XDG_RUNTIME_DIR` somewhere else, keep that path short. The single-instance socket path is limited to 108 bytes on Linux (104 on macOS). Past that, the server can't start and every launch runs on its own, which looks like forwarding but isn't.
