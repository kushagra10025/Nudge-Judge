# Releasing

[← Back to README](../README.md)

Installers and updates are built with [Velopack](https://velopack.io) and hosted on GitHub Releases. To release, push a semantic-version tag:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

[`.github/workflows/release.yml`](../.github/workflows/release.yml) then builds the native app for `win-x64` and `linux-x64` with that version, packs it with `vpk` (a `Setup.exe` for Windows and an AppImage for Linux, plus delta packages against the previous release) and uploads both to one release. Installed copies pick it up on their next check.

## Testing the update flow locally

Install the `vpk` tool (same version as the `Velopack` package), then pack two versions into the same folder and install the older one:

```bash
dotnet tool install -g vpk --version 1.2.161
dotnet publish NudgeJudge.Desktop -c Release -r linux-x64 -p:Version=0.1.0 -o publish
vpk pack --packId NudgeJudge --packVersion 0.1.0 --packDir publish --mainExe NudgeJudge.Desktop
```

> [!NOTE]
> Copies run with `dotnet run` or from a plain publish folder aren't installed, so Settings → Updates says updates aren't available there and nothing is checked.
