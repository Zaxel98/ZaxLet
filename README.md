# ZaxLet

**A tiny dynamic island for Windows 11: music with live lyrics, phone notifications, usage and timers, all in one pill at the top of your screen.**

[![Release](https://img.shields.io/github/v/release/Zaxel98/ZaxLet?label=release)](https://github.com/Zaxel98/ZaxLet/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Zaxel98/ZaxLet/total?label=downloads)](https://github.com/Zaxel98/ZaxLet/releases)

[Why](#why-zaxlet) · [Features](#features) · [Install](#install) · [Quick start](#quick-start) · [Privacy](#privacy) · [FAQ](#faq) · [Build](#build-from-source)

## Why ZaxLet

- **Light.** About 75 MB of RAM at idle, a 7 MB installer and no WebView.
- **Yours.** No account, no server, no telemetry. Heavy extras are optional downloads you start yourself.
- **Connected.** An Android companion app brings phone notifications to the PC and lets you answer chats from the keyboard.
- **Out of the way.** Hold Alt or right-click the pill to hide it, and it steps back by itself when a fullscreen app is running. Open it by hovering or, if you prefer, by left click.

## Features

**Music and lyrics**
- Track, cover art and controls for Spotify, YouTube and any player Windows can see.
- Synced lyrics that fill word by word, following the singer. The fill listens to the audio playing on your PC (never recorded) so instrumental breaks hold still.
- Lyrics are found automatically across several sources, even for YouTube videos with messy titles such as "Artist - Song (Audio)". Click or drag the progress bar to jump around, also for YouTube in a browser. For compilation videos with a track list in the description, the title, lyrics and progress bar follow the song that is playing now.
- A "Shazam-style" button identifies a song with no usable title by listening to the PC's audio (needs your own free AudD token).
- Lyrics in other scripts can be shown in Latin letters: romaji (Japanese), pinyin (Mandarin), Korean, Russian and other Cyrillic, Greek and Arabic. Only Japanese kanji and Mandarin need the optional Latin script pack (one download).
- Lyric timing can be shifted per song (saved), and a "Wrong lyrics?" button throws out the shown lyrics and looks for another version.
- Volume and shuffle in the Music tab, Spotify ads recognised (no lyrics lookup, not counted), and a weekly recap of your top songs and artists, kept only on this PC. With nothing playing, the tab shows the last song, the recap and an Open player button.
- Complex features come with a step-by-step guide that opens the first time (phone connection, Discord), and an info button to open it again. Advanced users get extra rows through Settings > General > Advanced mode.

**Phone**
- Notifications from the apps you pick appear in the pill. Chats such as WhatsApp can be replied to from the PC.
- Send files both ways through the Shelf, or use the phone camera as a webcam.
- Send text both ways: share text to ZaxLet HP and it lands on the PC clipboard; the Shelf has a "Text to phone" button for the other direction.
- One-time codes (OTP) in notifications get a Copy button, and the code stays out of the clipboard history.
- Pair by scanning a QR code in the phone app. The app restarts itself after a reboot, reconnects when the network changes, can send the phone's audio to the PC, and reports its battery level (tooltip on the phone dot, with a low-battery warning).

**Everyday tools**
- Timer, Pomodoro and stopwatch; eye-break and water reminders. Focus mode in the Timer tab holds phone notifications (OTP codes still pass) and summarises them afterwards.
- Meeting mode: Zoom, Meet, Teams and other calls are recognised from microphone use; the pill gets a mic mute button, music pauses and notifications are held.
- Wishlist tab: copy a product link and click Paste link. With the small browser extension (Firefox, Chrome, Edge, Brave; folder `extension` next to ZaxLet.exe with `zaxlet-wishlist.xpi` for Firefox, setup guide inside the tab; release Firefox needs the .xpi signed by Mozilla, which is free through addons.mozilla.org "On your own") the title, price and picture come from the page in your own browser and go only to ZaxLet on this PC (127.0.0.1, token-protected); the pill even offers to save a product link you just copied. Without the extension ZaxLet takes the shop name and a title from the link, and you can paste the picture and the price by hand. ZaxLet never tries to get around a shop's bot protection.
- Optional weather (open-meteo) with a rain alert, imsak and break-fast reminders, a JAKIM method, and a weekly schedule for other worship.
- Calendar with to-dos, and prayer times computed offline, with an optional adhan sound of your own choosing.
- Clipboard history (kept in memory only; copies from password managers are skipped).
- Screenshots (full, area, window) and screen recording.
- Quick action box (own shortcut): `t 10m`, `fokus 25`, `catat <text>`, `w <link>`, `minum 250`, `tema neon`, `buka monitor`, `perf`, `bt`, plus a calculator and unit converter (`= 12*5`, `5 km mi`).
- Performance overlay for games (FPS, CPU, GPU, VRAM, RAM, disk, network; click-through, off by default), optionally in the Monitor tab too. Optional auto-clear of the standby RAM cache (needs admin, off by default).
- Battery of Bluetooth headsets that report it to Windows, with a low-battery warning.

**In the island itself**
- Scroll over the island for volume, double-click the closed island to play or pause, swipe two fingers sideways to switch tabs.
- Small popups for volume, brightness, Caps Lock, Bluetooth connections and microphone mute, plus which app is using the microphone or camera, and finished downloads.
- Right-click hides the island (move to the top edge to peek, right-click again to keep it shown). Optionally it moves away by itself when it covers another app's buttons.
- One-choice notification style (Quiet, Balanced, Full) for new users, with every switch still available for advanced ones; accent colour can follow Windows; an optional keyboard shortcut opens and closes the island; apps whose music should never appear can be ignored; scripts on the same PC can show a message in the pill through a token-protected local webhook.
- Themes (Black, Aurora, Matcha, Senja, Neon), island size, monitor choice, and a choice of always shown, only when something is happening, or hidden until opened from the tray. Music and lyrics can stay on screen over borderless fullscreen games.
- A context line in the idle pill (weather and next prayer, today's tasks, water left, tomorrow's schedule), an automatic battery saver, and a Ctrl+F search across all settings.
- Updates itself from GitHub Releases (checked against SHA-256), and settings can be backed up to one file.

**System and AI usage**
- CPU, RAM, network, battery and temperatures at a glance.
- Remaining quota and spend for Claude and Codex.

The idle pill, the tabs and their order are all configurable in Settings.

## Install

Requires Windows 11 and the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the installer offers to fetch it if it is missing).

1. Download `ZaxLetSetup.exe` from the [latest release](https://github.com/Zaxel98/ZaxLet/releases/latest).
2. Run it. It installs for the current user and does not need admin rights.
3. If SmartScreen warns that the publisher is unknown, choose **More info**, then **Run anyway**. The installer is not code-signed.

Two features download extra files only when you ask, from Settings > Add-ons: the Latin script pack (about 15 MB, one download that adds the Japanese kanji dictionary and the Mandarin pinyin table; Korean, Cyrillic, Greek and Arabic are already built in) and ffmpeg for screen recording (about 100 MB). Each file is verified against a pinned SHA-256.

**Uninstall:** Windows Settings > Apps > Installed apps > ZaxLet. You are asked whether to delete your data as well.

## Quick start

1. Move the cursor to the top centre of the screen and the pill opens. Pick a tab from the icons.
2. Open Settings with the gear icon to choose your location, tabs and what the idle pill shows.
3. To link a phone, open Settings > Phone > Phone connection guide: it shows one QR code to install the app and one to pair, then walks through notification access. Allow the Windows Firewall prompt for private networks and grant notification access on the phone.

Handy keys: hold **Alt** over the pill to hide it for a moment, **Ctrl+Alt+I** to hide it for longer.

## Privacy

- ZaxLet has no server and sends nothing to its author.
- It only makes the requests behind features you use: song title, artist and duration to find lyrics; the title and channel of a video of 8 minutes or more to youtube.com, to read its track list (can be turned off in Settings > Music); 12 seconds of PC audio to api.audd.io, only when you press the song-recognition button and have entered your own AudD token; a place name for prayer times if you choose online lookup; and your own Claude/Codex logins for the usage numbers, sent only to those services.
- Vocal detection analyses the playing audio on your PC and keeps nothing.
- Update checks ask GitHub for the latest release of this repository (at most once a day, can be turned off), and downloads are verified against a SHA-256 published with the release.
- The clipboard history, microphone/camera indicator and download alerts all stay on your PC.
- The exact position of a YouTube video is read from the page's progress bar through Windows accessibility, only while that tab is the active one. This stays on your PC and can be turned off in Settings > Music.
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
Yes: Settings > General > Language offers Indonesian and English (automatic follows Windows). The change applies after ZaxLet restarts; the settings page has a button for that.

**Why is there no usage for Claude or Codex on my other PC?**
ZaxLet reads the logins that the Claude Code and Codex command-line tools store on that same PC (`~/.claude`, `~/.codex`, or the folders in `CLAUDE_CONFIG_DIR` / `CODEX_HOME`). If that login is missing, ZaxLet falls back to the quota figures that the Claude Desktop app itself records (percent for the 5-hour and weekly windows, without reset times, with their age shown). For exact numbers and reset times, run `claude auth login` once in a terminal; it uses the same subscription. The Usage tab says exactly what is missing.

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

[MIT](LICENSE). Third-party components and services are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
