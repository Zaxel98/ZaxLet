# ZaxLet

**A tiny dynamic island for Windows 11: music with live lyrics, phone notifications, usage and timers, all in one pill at the top of your screen.**

[![Release](https://img.shields.io/github/v/release/Zaxel98/ZaxLet?label=release)](https://github.com/Zaxel98/ZaxLet/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Zaxel98/ZaxLet/total?label=downloads)](https://github.com/Zaxel98/ZaxLet/releases)

[Why](#why-zaxlet) · [Features](#features) · [Install](#install) · [Quick start](#quick-start) · [Privacy](#privacy) · [FAQ](#faq) · [Build](#build-from-source)

## Why ZaxLet

- **Light.** About 30 MB of RAM at idle, a 7 MB installer and no WebView.
- **Yours.** No account, no server, no telemetry. Heavy extras are optional downloads you start yourself.
- **Connected.** An Android companion app brings phone notifications to the PC and lets you answer chats from the keyboard.
- **Out of the way.** Hold Alt to push the pill aside, and it steps back by itself when a fullscreen app is running.

## Features

**Music and lyrics**
- Track, cover art and controls for Spotify, YouTube and any player Windows can see.
- Synced lyrics that fill word by word, following the singer. The fill listens to the audio playing on your PC (never recorded) so instrumental breaks hold still.
- Lyrics are found automatically across several sources, and Japanese lyrics can be shown as romaji.

**Phone**
- Notifications from the apps you pick appear in the pill. Chats such as WhatsApp can be replied to from the PC.
- Send files both ways through the Shelf, or use the phone camera as a webcam.

**Everyday tools**
- Timer, Pomodoro and stopwatch; eye-break and water reminders.
- Calendar with to-dos, and prayer times computed offline.
- Screenshots (full, area, window) and screen recording.

**System and AI usage**
- CPU, RAM, network, battery and temperatures at a glance.
- Remaining quota and spend for Claude and Codex.

The idle pill, the tabs and their order are all configurable in Settings.

## Install

Requires Windows 11 and the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the installer offers to fetch it if it is missing).

1. Download `ZaxLetSetup.exe` from the [latest release](https://github.com/Zaxel98/ZaxLet/releases/latest).
2. Run it. It installs for the current user and does not need admin rights.
3. If SmartScreen warns that the publisher is unknown, choose **More info**, then **Run anyway**. The installer is not code-signed.

Two features download extra files only when you ask, from Settings > Add-ons: romaji for kanji (about 14 MB) and ffmpeg for screen recording (about 100 MB). Each file is verified against a pinned SHA-256.

**Uninstall:** Windows Settings > Apps > Installed apps > ZaxLet. You are asked whether to delete your data as well.

## Quick start

1. Move the cursor to the top centre of the screen and the pill opens. Pick a tab from the icons.
2. Open Settings with the gear icon to choose your location, tabs and what the idle pill shows.
3. To link a phone, open Settings > Phone devices, scan the first QR code to install the app, then scan the second to pair. Allow the Windows Firewall prompt for private networks and grant notification access on the phone.

Handy keys: hold **Alt** over the pill to hide it for a moment, **Ctrl+Alt+I** to hide it for longer.

## Privacy

- ZaxLet has no server and sends nothing to its author.
- It only makes the requests behind features you use: song title, artist and duration to find lyrics; a place name for prayer times if you choose online lookup; and your own Claude/Codex logins for the usage numbers, sent only to those services.
- Vocal detection analyses the playing audio on your PC and keeps nothing.
- The phone link is encrypted with a pinned certificate and accepts paired devices only. Message contents are never logged.

Your settings, tasks and cached lyrics stay in `%LOCALAPPDATA%\ZaxLet`.

## FAQ

**The phone will not connect.**
Check that both devices are on the same Wi-Fi (or both on [Tailscale](https://tailscale.com)), that Windows Firewall allows private networks, and that the phone app is exempt from battery limits. After changing networks, scan the second QR code again.

**It works on home Wi-Fi but not at school, work or a café.**
Guest and public networks often block devices from talking to each other. Use Tailscale or your phone's hotspot there.

**My phone and PC use different Tailscale accounts.**
They are then on separate networks. Share the PC to the phone's account from the Tailscale admin console (Machines > Share).

**The pill covers something.**
Hold Alt while hovering to hide it, or press Ctrl+Alt+I.

**Can I change the language?**
The interface is currently Indonesian. This documentation is in English.

## Build from source

Needs the .NET 8 SDK.

```powershell
git clone https://github.com/Zaxel98/ZaxLet.git
cd ZaxLet
tools\build.cmd
dist\ZaxLet.exe
```

Run the self-checks with `dotnet run` inside `tests\ZaxLet.Tests`. The Android app, installer and virtual-camera build scripts are in `android`, `installer` and `native`; how everything fits together is in [docs/technical.md](docs/technical.md).

## Contributing

Bug reports and ideas are welcome in [Issues](https://github.com/Zaxel98/ZaxLet/issues). ZaxLet is a personal project tested mainly on one PC and one phone, so details that look off on your setup are useful to hear about.

## License

Not decided yet; until one is added, all rights are reserved by the author. Third-party components and services are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
