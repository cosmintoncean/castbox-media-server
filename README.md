# CastBox Media Server

Windows desktop server for streaming a local movie/TV library to the CastBox Android TV app.

## Repository/release plan

Suggested public repository: `castbox-media-server`. Keep source code and release automation in this repository. GitHub Releases contains the downloadable `CastBox-Setup.exe` installer. Tag releases with semantic versions such as `v0.1.1`; pushing a tag triggers a Windows GitHub Actions workflow that publishes the self-contained app, builds the installer with Inno Setup, and attaches it to the GitHub Release.

## Local development/build

Requirements for building from source: .NET 8 SDK and Inno Setup 6.

```powershell
.\Build-Installer.ps1
```

Output: `installer-output\CastBox-Setup.exe`.

The installed app is self-contained and does not require the .NET runtime to be separately installed. Installer is per-user and writes application files to `%LOCALAPPDATA%\Programs\CastBox`. User settings remain under `%LOCALAPPDATA%\CastBox\settings.json` and are not included in the installer payload.

## Configure the updater before publishing

In `Program.cs`, replace:

```csharp
private const string GitHubRepository = "YOUR_GITHUB_USERNAME/castbox-media-server";
```

with the real `OWNER/REPOSITORY` for the public GitHub repository. Update `CurrentVersion` to match the version being shipped. Update `MyAppVersion` in `installer/CastBox.iss` for local builds; CI substitutes the tag version automatically.

The in-app Updates menu queries the latest public GitHub Release and expects an asset named exactly `CastBox-Setup.exe`. It displays release notes and asks before downloading and launching the installer. Do not use the updater with an untrusted repository. Code signing is recommended before public distribution; until signed, Windows SmartScreen may warn users.

## First GitHub setup

1. Create a new **public** GitHub repository named `castbox-media-server` (or choose another name and update the constant above).
2. Upload/push the contents of this folder to the repository's default branch.
3. Confirm GitHub Actions is enabled and allowed to create releases.
4. After replacing `GitHubRepository`, commit the change and push a tag such as `v0.1.1`:

```powershell
git add .
git commit -m "Prepare CastBox release pipeline"
git push origin main
git tag v0.1.1
git push origin v0.1.1
```

5. GitHub Actions should build `CastBox-Setup.exe` and attach it to the matching Release. Verify the release page before using the in-app updater.

## Important

- The app currently scans the configured folders on library requests; indexing/cache improvements are separate work.
- The updater and installer source need a Windows build/test pass before public release. Test on a non-critical PC first.
- The updater requires a public GitHub repository and a release asset named `CastBox-Setup.exe`.
