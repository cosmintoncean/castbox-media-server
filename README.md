# CastBox Media Server

Windows desktop app (.NET 8, WinForms with embedded Kestrel) that serves video files from chosen folders to the CastBox Android TV app over HTTP on the home LAN.

- Settings: `%LOCALAPPDATA%\CastBox\settings.json` (port, default 5078, and media folders).
- Endpoints: `/api/health`, `/api/library`, `/api/media/{id}` (see `docs/api-contracts.md` in the CastBox workspace).
- Build: `dotnet build CastBox.Windows.csproj -c Release`. Publish with `.\Build-CastBox.ps1`; build the installer with `.\Build-Installer.ps1` (needs Inno Setup 6).
- There is no authentication. Use only on a trusted LAN.

## Appearance

Use **View → Dark mode / Light mode** to switch between the charcoal/amber and light/amber palettes. The preference is currently session-only and defaults to dark mode on launch.


## Full UI redesign

The Windows control panel now uses a larger, responsive layout inspired by the CastBox mock-up, with a larger brand header, grouped library actions, rounded amber-accented buttons, and a View menu for switching between dark and light themes. Dark mode is the default for each launch.
