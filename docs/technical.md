# ZaxLet: technical notes

How the main pieces work, for anyone reading or extending the code. The general overview is in the [README](../README.md).

## Source and data

- Windows app: `src/ZaxLet` (C#, WPF, `net8.0-windows10.0.19041.0`). One NuGet package only (QRCoder); everything else is built-in .NET or Windows APIs. WPF renders in software mode to avoid loading a graphics driver (tens of MB of RAM).
- User data: `%LOCALAPPDATA%\ZaxLet` (`settings.json`, `tasks.json`, `shelf.json`, `devices.json`, `inbox\`, `lyrics\`, `error.log`). A folder from the earlier name `Island` is moved there on first start.
- Dev flags (not needed for normal use): `--tab=<name>` open and hold one tab, `--shot` screenshot, `--timer=<seconds>`, `--settings`, `--page=<phone|water|idle|prayer…>`, `--camera`, `--demo-notifs=N`, `--romaji-test`, `--lyrics-test=Title|Artist|seconds`, `--flow-test[=seconds]`, `--karaoke-trace=seconds[,songs]`. Env `ZAXLET_URL_FILE=<file>` writes the phone-link URL to a file.
- Screen recording needs `ffmpeg.exe` on PATH or in the data folder (or the ffmpeg add-on).

## Idle pill and hiding

- Idle shows the battery icon (filled to the charge level) and the clock; the second row is for lyrics, timers and alerts. Tabs: Music, Usage, Monitor, Today, Prayer, Shelf (with phone link), Capture, Timer, Eyes, Settings, plus Water and Communication.
- A phone notification uses one row in idle (it replaces lyrics/timers); the full conversation (last few messages, three visible, the rest scrolls) lives in the Communication tab.
- Fullscreen app or hidden pill: a new notification shows the pill briefly (Settings > Phone notifications).
- Temporary hide: hold Alt while hovering over the pill area (release to restore; hover opens it fully). For longer: the `ctrl+alt+i` hotkey (configurable) or the tray menu; it returns by itself after N minutes. Hover-open is skipped while Alt is down, and Alt also collapses an open pill.

## Settings

Settings has nine areas: General (autostart, hover, close delay, fullscreen, accent colour, clock format), Idle pill (battery, AI usage, download speed, waveform, lyrics), Music, Usage, Monitor, Calendar, Prayer (location, method, madhhab, safety margin), Tools and Tabs. Changes apply immediately and are stored in `settings.json`.

Durations accept free text such as `30s`, `10m`, `1.5 h`, `1h30m`. Location can be automatic (IP via ipwho.is), the device (Windows sensor, asks permission) or a city search (Open-Meteo); coordinates never need typing.

## Lyrics

Synced lyrics are filled word by word at the singer's pace; words that do not fit wrap to the next line and are never cut. Sources only give per-line times, so word times are estimated from letter counts (Fast/Normal/Slow in Settings > Music). Clicking a line seeks to it; the Sync buttons shift by 0.2 s. Lyrics are cached in `lyrics\`, so a song looked up once shows instantly even if the server is down. Turning lyrics off (player button or Settings) also turns them off in the idle pill and stops downloads.

### Sources

Automatic, no settings, tried in this order: LRCLIB (most accurate, matched by duration) → LyricsPlus (aggregator of Apple Music/QQ/Deezer, professional timing) → NetEase → QQ Music → Kugou (most complete, especially Asian songs). Maximum 10 sources; new ones are added to the `chain` list in `MediaService.Fetch`.

- Three query variants: full title + cleaned artist, main title without a "-…-" / "~…~" subtitle + artist, and main title alone. Artists are cleaned of "(CV. …)", "feat.", "starring …".
- If every source misses, a last pass queries NetEase/QQ/Kugou without the source-side duration filter and accepts a result only if its timestamps cover 60%–105% of the real duration (handles tracks whose Spotify duration differs from the catalogue).
- Sanity checks apply to every source: fake timestamps (uniform line spacing ≤ 2.5 s, plain lyrics pretending to be synced) and lyrics that end before 40% of the song are rejected; old cache entries that fail the same checks are ignored. Every candidate must match title and duration (±6 s).
- LRCLIB often answers 503/429, so there is a circuit breaker (skipped for 15–180 s after a failure; backup sources start immediately), a 6 s timeout per request, and the "server busy" message appears only if **all** sources fail to respond. Songs that are certainly missing are remembered for 20 minutes.
- Tried and rejected: lrc.cx (always returns the same fake lyrics), BetterLyrics (needs an API key), SimpMusic (official clients only), Megalobiz (410), paxsenix musixmatch (closed).

## Karaoke follows the vocals ("flow")

While the Music tab is open the app reads the PC's audio output (WASAPI loopback, local, never recorded) and detects vocals from the 300–3400 Hz band energy relative to the song's level range. The lyric fill only advances while vocals are heard: instrumental gaps hold it, a line whose vocals stopped completes immediately, and how long each line takes is learned from earlier lines. With no audio or total silence it falls back to timing. Capture is off outside the Music tab.

The vocal/instrument pattern is learned per song: lyric timing provides labels (inside a line = vocal; intro, blank lines and long gaps = instrumental), and a small logistic model on five spectral features is retrained every 3 s on that song's own audio and used once its accuracy reaches 0.72. A tolerance (Settings > Music, default ±1.5 s) keeps the audio result close to the timing estimate; if 88% or more (or 12% or fewer) of frames read as voiced, the song is treated as unreadable and uses timing.

## Romaji

Japanese lyrics get romaji in the Music tab. Kana converts directly (Hepburn, with は/へ particle handling); kanji need the optional add-on (MeCab + IPAdic, about 14 MB from nuget.org, SHA-256 pinned, loaded in a collectible `AssemblyLoadContext` so the dictionary frees its RAM). "Romaji only" hides the original lyrics; another option uses romaji in the idle pill. Non-Japanese songs are shown as normal.

## Phone link

- Server: `ShareServer` on fixed ports 47890 (HTTP, files) and 47891 (TLS, control and camera), falling back to random ports on conflict. It accepts only private, loopback and Tailscale (100.64.0.0/10) addresses, protected by a fixed token and a block-able device list. A plain `TcpListener` is used so no admin rights or urlacl are needed.
- Pairing link: `zaxlet://pair?lan=..&ts=..&tp=..&t=..&f=..` carries the LAN address, the Tailscale address (re-read every time it is used, because it changes with the Tailscale account), the TLS port, the token and the SHA-256 fingerprint of the self-signed certificate, which the phone pins.
- The phone service keeps a `/ctl` socket (LAN first, then Tailscale) with a 40 s read timeout and a ping/pong; notifications are sent as JSON including the last 8 messages, and chat replies run the notification's own reply action. Camera frames go through the softcam virtual camera (needs UAC once to register); aspect ratio 1:1, 4:3, 9:16 or 16:9, with landscape detected automatically.
- Firewall: rule "ZaxLet HP" for ports 47890–47891; the Settings button needs UAC once.

## Android app

Sources in `android/` (package `com.zaxlet.hp`), built by `android/build.ps1` with aapt2, javac, d8, zipalign and apksigner (no Gradle). The APK is copied to `dist\` and embedded in the installer so the PC can serve it over HTTP for the first QR code.

## Installer

`installer/build.ps1` zips `dist\` (without `.pdb`) and embeds it in `installer/Setup.cs`, compiled with the .NET Framework `csc.exe` (C# 5, WinForms). It installs per user to `%LOCALAPPDATA%\Programs\ZaxLet`, adds Start Menu and desktop shortcuts, optional autostart, a copy of itself as `Uninstall.exe` and an uninstall registry entry. If the .NET 8 Desktop Runtime is missing it offers to download the official installer from Microsoft.
