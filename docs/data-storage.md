# How data is saved

[← Back to README](../README.md)

## Where

| OS | Config path |
|---|---|
| Linux | `$XDG_CONFIG_HOME/nudgejudge/config.json` (default `~/.config/nudgejudge/config.json`) |
| Windows | `%APPDATA%\NudgeJudge\config.json` |
| macOS | `~/Library/Application Support/NudgeJudge/config.json` |

`activity.log` sits next to the config.

## How

`config.json` and `activity.log` are rewritten whole on every change. Each write follows the same steps:

1. **Lock.** The writer takes an exclusive lock on a sidecar file (`config.json.lock`, `activity.log.lock`), so a standalone launch and the running instance can't interleave. Waiters queue on a second file (`….lock.turn`) while they wait, so a process writing in a burst (many links at once) can't keep taking the lock back before a waiting one gets a turn. A config update that can't get the lock within 2 seconds goes ahead without it rather than freeze the UI.
2. **Re-read if changed.** If another process rewrote the config since this one last saw it, the store reloads it first and then applies its change, so neither side's edits are lost. Updates find rules and browsers by id, never by list position.
3. **Atomic replace.** The new contents go to a temp file in the same directory, are flushed to disk, then renamed over the old file. Readers see the old version or the new one, never half of each. On Linux and macOS the directory is fsynced too, so the rename survives a power cut.

## When something goes wrong

| Situation | What happens |
|---|---|
| A save fails (disk full, file locked) | The change stays in memory, a banner reports it, and the next change retries the save. Nothing is thrown to the UI. |
| `config.json` can't be parsed | It's kept as `config.json.broken-<timestamp>` (never overwritten) and the app starts from defaults. |
| The config is from an older version | The original is kept as `config.json.v<N>.bak`, then the upgraded file is written. |
| The config is from a newer version | It's opened read-only: changes work for the session but are never saved, so the newer version's settings stay intact. |
| You spin a number field in Settings | It saves 400 ms after the last change (and on exit), so spinning the activity limit down and back up doesn't delete entries. |

## Windows: known gaps and planned fixes

The steps above run on Windows too, but the test suite hasn't run there yet, and two Windows-specific behaviours aren't covered.

**1. The rename isn't forced to disk.** Windows has no way to fsync a directory, and .NET's `File.Move` doesn't ask for write-through. After a power cut, the config is never corrupt (NTFS journals its metadata), but it can come back as the previous version, losing the last change.

*Fix:* on Windows, do the replace with `MoveFileExW(temp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` through P/Invoke instead of `File.Move`. The call only returns once the rename is on disk. This would replace the Unix directory fsync in `AppPaths.WritePrivateFileAtomic`, leaving Linux and macOS unchanged.

**2. Antivirus and indexers can make a save fail.** Defender, Windows Search and sync clients (OneDrive, Dropbox) often open a freshly written file for a few milliseconds without allowing it to be deleted. A rename that lands in that window fails with "access denied" or a sharing violation. Today:

- **Config:** the change stays in memory with a warning, and the next change retries. It's lost only if the app quits before another change is made.
- **Activity log:** that entry stays in memory but is never written to disk.

Older Windows 10 builds may also refuse a rename over a file another process has open, even when that process allows deletion.

*Fix:* in `WritePrivateFileAtomic`, on Windows only, retry the rename when it fails with `ERROR_ACCESS_DENIED` (5), `ERROR_SHARING_VIOLATION` (32) or `ERROR_LOCK_VIOLATION` (33). Allow about 5 attempts with a short backoff (e.g. 10, 20, 40, 60, 80 ms) before giving up and reporting the error as today. VS Code and npm handle the same problem this way.

**3. Verify on a real Windows machine.** Add a GitHub Actions workflow that runs `dotnet test` on both `ubuntu-latest` and `windows-latest`. The storage tests in `NudgeJudge.Core.Tests/ConfigStoreTests.cs` run on both platforms: the failed-save test puts a directory where the file should be instead of using Unix permissions, and the Unix file-mode checks are skipped on Windows.
