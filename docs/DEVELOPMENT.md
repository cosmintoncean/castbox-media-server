# Development notes

For people who want to build or change NestCast Server. Users: see the [README](../README.md).

## What it is

A .NET 8 WinForms app with an embedded Kestrel web server (`CastBox.Windows.csproj`; the code and installer keep the old CastBox name in file names, namespaces and the data folder). It serves the chosen folders to the NestCast TV app over HTTP on the home network. The tests are in `tests/CastBox.Tests` (xunit).

## Building

```powershell
dotnet build CastBox.Windows.csproj -c Release
dotnet test tests\CastBox.Tests
.\Build-CastBox.ps1      # publishes a self-contained app
.\Build-Installer.ps1    # builds the installer (needs Inno Setup 6)
```

## Releasing

Raise the version in `Program.cs` (`CurrentVersion`) and `installer/CastBox.iss`, commit, then push a tag such as `v0.8.5`. The GitHub Actions workflow in `.github/workflows/release.yml` sets the version from the tag, downloads a pinned FFmpeg build (checked against its SHA-256), builds the installer and attaches `CastBox-Setup.exe` to the release. The in-app updater looks for an asset with that exact name, so don't rename it.

## Settings and data

Settings (port, folders, allowed TVs) are stored in `%LOCALAPPDATA%\CastBox\settings.json`, with the picture thumbnail cache beside it. Set `CASTBOX_DATA_DIR` to use a different folder, which is how to try the app without touching your real settings. The MediaLog token is stored encrypted with Windows DPAPI. The default port is 5078. The dark/light mode is not saved between runs.

`CASTBOX_UI_SCALE` (for example `1.5`) pretends the screen is scaled, text included, to check the layout on a normal display.

## HTTP API

The TV app is the only client. There is no authentication.

- `GET /api/health`, `GET /api/library`, `GET /api/media/{id}` (range requests supported).
- `GET /api/personal/roots`, `/browse`, `/{id}`, `/{id}/thumb`: personal pictures and videos, browsed a folder at a time.
- `GET /api/medialog`, `POST /api/watched`: the MediaLog account behind this PC, and finished-video reports.
- `/api/screens/...`: screen sharing (request, approval on the PC, then an HLS or stream endpoint). Chromecast sharing uses FFmpeg to write a live HLS playlist that the Chromecast fetches.

The code in `Program.cs` and `ScreenShare/` is the reference for exact shapes.

## Screen sharing and FFmpeg

Screen capture and encoding run in a separate FFmpeg process, shipped in the `ffmpeg` folder next to `CastBox.exe` (the Inno installer and the release workflow add it). It is GPL-licensed and not linked into the app; its licence text is installed with it.

## Streaming and sleep

`KeepAwake.cs` asks Windows not to sleep while video or picture requests are open (and for five minutes after) or a screen is shared.

## UI

`UiScale.cs` scales the windows on high-DPI screens and fits them to the screen. The header artwork is `castbox-logo.png` and `CastBox.ico`. The "by ComaApps" badge uses the Space Grotesk font in `Fonts/` (SIL Open Font License).
