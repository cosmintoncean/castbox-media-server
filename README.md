# NestCast Server

NestCast Server is a free Windows app that shares the movies, shows and pictures on your PC with **NestCast TV** on your Android TV. Choose your folders once and they appear on the TV, ready to play. No account, no cloud, no ads.

Get the TV app here: [NestCast TV](https://github.com/cosmintoncean/nestcast-tv-android).

## What it does

- **Share your folders.** Pick the folders with your movies and shows. NestCast keeps them available to your TV automatically.
- **Personal folders.** Add folders with your own pictures and videos; they are shown separately on the TV.
- **Share your screen.** Send one of your PC's screens, with sound, to the TV or to a Chromecast. NestCast asks you before anything is shared.
- **Stays awake while it streams.** Your PC won't go to sleep in the middle of a movie.
- **Runs quietly.** It can start with Windows and keeps working from the system tray.
- **Dark and light mode.** Switch from the **View** menu.
- **Optional MediaLog sync.** If you use MediaLog, finished videos can be marked as watched there.

## Install

1. Download `CastBox-Setup.exe` from the [latest release](https://github.com/cosmintoncean/castbox-media-server/releases/latest). (The file keeps its old name so that updates keep working.)
2. Run it. Windows 10 or 11 (64-bit) is required; nothing else needs to be installed.
3. Open **NestCast Server**, add your folders and choose **Start server**.
4. If your TV can't connect, choose **Configure firewall** once.
5. On the TV, add the **connection address** shown at the bottom of the window (it looks like `http://192.168.1.25:5078`).

Updates: **Updates → Check for updates** finds new versions and can install them for you.

## Safety: keep it at home

NestCast Server has **no password or login**. Anyone who can reach your PC on the network can browse and watch the folders you share. It is designed for a **trusted home network** and **must not be exposed directly to the internet**: don't forward its port on your router, and don't run it on a public or shared network. To watch away from home, use a VPN to your home network instead.

## Privacy

NestCast collects nothing and has no ads or tracking. Your files stream straight from your PC to your TV and are never uploaded. It only contacts the internet to:

- check GitHub for a newer version when you ask it to;
- look up a show's poster title on TVmaze (only if you've connected MediaLog);
- talk to MediaLog, only if you connect your own account.

Read the full [privacy policy](https://github.com/cosmintoncean/nestcast-tv-android/blob/main/PRIVACY.md).

## Support

NestCast is free. If you like it, you can leave a tip on [Ko-fi](https://ko-fi.com/comaapps) (**Support → Tip jar on Ko-fi**). It's made by [ComaApps](https://comaapps.netlify.app/).

## Licence

NestCast Server is free software under the [GNU General Public License v3.0](LICENSE). The installer also includes an FFmpeg build (GPL, from gyan.dev) that runs as a separate program for sharing your screen; its licence is installed in the `ffmpeg` folder.

## For developers

Build instructions and technical notes are in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
