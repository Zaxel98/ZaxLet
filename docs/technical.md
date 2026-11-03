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

Settings has nine pages: General (language, appearance, autostart, updates and backup, tour, Advanced mode), Island (opening, hiding, gestures, idle pill), Music (lyrics, karaoke, Latin script, YouTube), Phone (connection guide, notifications, camera), Usage & Monitor, Calendar & Prayer, Reminders (water, eye, Pomodoro), Tools (screenshots, clipboard, Discord and Meet) and Add-ons. Each page has section headings; rows labelled with `~` in the source appear only when Advanced mode is on, and a label with `\n` carries a small caption. Old page keys (idle, usage, prayer, apps, ...) map to the new pages in `SettingsView.Normalize`. Changes apply immediately and are stored in `settings.json`.

Guides: `GuideWindow` (Views/Guides.cs) shows numbered steps with live status and QR codes; `PhoneGuide` (install, pair, notifications, troubleshooting) and `DiscordGuide` (quick shortcuts, or the full RPC setup with automatic clipboard detection of the Client ID and Secret) open once on first use (`Settings.GuidesSeen`) and again from info buttons. `ZaxLet.exe --ui-test` renders every settings page and guide step to PNG (`ZAXLET_UI_OUT`) and writes `missing.txt` with strings lacking an English translation.

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

## Latin script (romaji, pinyin and others)

`Translit.Of(line)` returns the Latin form of a lyric line, or null if there is nothing to convert. Japanese goes through `Romaji`: kana converts directly (Hepburn, with は/へ particle handling), kanji need the optional add-on (MeCab + IPAdic, about 14 MB from nuget.org, SHA-256 pinned, loaded in a collectible `AssemblyLoadContext` so the dictionary frees its RAM). The rest: pinyin with tone marks (first reading of each character from a table derived from mozillazg/pinyin-data that the Latin script pack downloads (SHA-256 pinned, converted to a compact `pinyin.gz`; without the pack, Han stays as is), plus a small word list for common polyphones), Hangul in Revised Romanization with basic linking, Cyrillic (Russian, with Ukrainian and Serbian letters), Greek and Arabic. Arabic is written without short vowels, so the result is an approximation.

Han characters belong to both Japanese and Chinese, so the language is decided per song by `Translit.Begin` when the lyrics load: kana in at least 10% of the CJK letters, or kana in the title or artist, means Japanese; Han without kana means Mandarin. "Latin only" hides the original lyrics; another option uses the Latin form in the idle pill.

## Track identification

`TrackId.Parse(title, artist)` cleans browser metadata. It removes bracketed noise such as "(Audio)" or "[Official Music Video]", turns channel names like "artistVEVO" or "Artist - Topic" into artist names, and splits "Artist - Title" (comparing both halves with the channel name to decide which is which; "Title - Remastered 2011" stays one title). It returns the pair to display plus ordered search candidates: the best guess, the swapped pair, the whole string as a free-text query, and finally the original metadata. Lyrics are searched with each candidate until one hits.

## Island extras

`IslandWindow.Features.cs` holds the gestures and system popups; the sources are small services without extra packages.

- **Gestures:** wheel on the pill sets the volume through Core Audio (`AudioEndpoint`), ignoring wheel events that start inside scrollable content; `WM_MOUSEHWHEEL` (two-finger sideways) switches tabs; a double-click on the closed pill toggles play/pause.
- **Popups (OSD):** a 300 ms timer compares volume, mute and Caps Lock with the previous reading; microphone mute, brightness (WMI `WmiMonitorBrightness`, late-bound COM, none on desktops), the apps using the microphone/camera (`CapabilityAccessManager` registry values), Bluetooth connections (WinRT `DeviceWatcher`) and finished downloads (`DownloadWatch`, which ignores browser temp files and waits for a stable size) are read every 2 s. `Alert(text, seconds, onClick, sound)` carries an optional click action, for example showing the downloaded file or starting an update.
- **Network type:** `NetInfo` asks `INetworkListManager` (late-bound COM, readable without admin; the HKLM registry copy is not) and checks whether the "ZaxLet HP" firewall rule covers the Public profile.
- **Clipboard:** `WM_CLIPBOARDUPDATE` feeds `ClipboardHistory` (40 entries, memory only, pinned entries survive Clear).
- **Yield:** `YieldService` looks at the foreground window under the island with UI Automation (buttons, tabs, edits, menus; cached per window and position) and, when something interactive is covered, makes the island behave like it does in fullscreen. Off by default.
- **Calendar:** `Ical` expands RRULE (DAILY, WEEKLY with BYDAY, MONTHLY, YEARLY, INTERVAL, COUNT, UNTIL, EXDATE) for the next 400 days. Times ending in Z become local; TZID times are assumed local.
- **Updates:** `UpdateService` reads `releases/latest`, needs the asset `ZaxLet-update.zip` and its hash (the `digest` field or `ZaxLet-update.zip.sha256`), extracts safely (no entries outside the folder) and lets a small `cmd` script copy the files after the app exits. `installer/build.ps1` writes both files to `installer/out`. Upload them next to `ZaxLetSetup.exe` in every release.
- **Backup:** `SettingsBackup` exports the settings without secrets and, when restoring, keeps the current secrets.

Test hooks: `--feat-test`, `--usage-test`, `--update-test`, `--mon-test` and `--yt-trace=N` write what they found to `*-test.txt` in the data folder; `ZAXLET_DATA_DIR` and `ZAXLET_NO_SINGLE=1` run a second, isolated instance; `ZAXLET_UPDATE_URL` replaces the release API address.

## Claude quota without a terminal login

The exact quota comes from Claude Code's own login (`.credentials.json`, endpoint `api/oauth/usage`). When that login is missing, or the token is rejected, `UsageService.ClaudeFromDesktop` reads `plan-usage-history.json` from the Claude Desktop data folder instead (Store build: `%LOCALAPPDATA%\Claude-Data\<package>\`, classic installer: `%APPDATA%\Claude\`, or the package folder). The app appends a sample about every 15 minutes with `fh` (5-hour percent) and `sd` (weekly percent). The latest sample is shown with its age as a note. No token is read and the app's encrypted storage is never touched. There are no reset times in that file, so the countdown is hidden.

## Monitor: temperature and GPU

No driver and no admin rights are needed. CPU temperature comes from LibreHardwareMonitor's remote web server (port 8085) when it is running, otherwise from the hottest ACPI thermal zone in the Windows counter `Thermal Zone Information` (not a core sensor, but close to the package temperature on laptops). GPU load, temperature and power come from `nvidia-smi` on NVIDIA machines; without it the load is taken from the `GPU Engine` counters (all vendors, busiest engine like Task Manager) and the GPU temperature stays empty. Counters are read through `PdhQuery` (PDH, wildcard instances). `ZaxLet.exe --mon-test` writes what was found to `mon-test.txt`; the environment variable `ZAXLET_NO_NVIDIA=1` forces the counter path.

## YouTube playback position

Browsers often report a stale or empty timeline to Windows (SMTC): position 0 and duration 0 in the middle of a video, or no update after a jump inside the page. `YtProgress` reads the truth from the page itself: YouTube's progress bar is a slider (`ytp-progress-bar`) whose value is the current second and whose maximum is the video length, and Windows UI Automation exposes it for Firefox and Chromium browsers. `MediaService` polls it every 2 s while the window title contains the video title and "YouTube" (so only the active tab of a browser window, and never for other players), prefers it over the SMTC timeline for 6 s after each reading, and uses its maximum as the duration when the browser sends none. Empty SMTC timelines are ignored once a good one has been seen. Everything is local. Dev flag: `--yt-trace=seconds` writes one line per second (track, position, duration, lyric state) to `yt-trace.txt`.

## Compilation videos and song recognition

For a video of 8 minutes or more, `YtChapters.FetchAsync` searches youtube.com (results page, filter "video") for a result whose title matches and whose length is within 3 s of the player's duration, then reads the timestamp lines of its description (at least three, ascending, starting near 0:00). Each segment becomes its own track in `MediaService`: `Position()` and `Duration` are relative to the segment, title and lyrics are re-resolved on every boundary, the progress bar and seeking work per song, and next/previous jump between segments. The page is scraped without an API key, so it may break if YouTube changes its markup. `dotnet run -- --live "title" "channel" seconds` in `tests/ZaxLet.Tests` checks it against the real site.

`SongId` is the "Shazam" button: it taps the WASAPI loopback (`AudioFlow.Tap`) for 12 s, makes a 16 kHz mono WAV and posts it to api.audd.io with the user's own token (`SongIdToken`). The hit's timecode places the song in the stream, reusing the segment mechanism with an open-ended segment, so lyrics line up even in the middle of a mix.

## Hiding the pill

Right-click on the pill (outside text boxes and items that handle right-click themselves) enters the same hidden mode as fullscreen: `SetSnooze(true, peek: true)`. As in fullscreen, moving the cursor to the top edge at the screen centre makes the pill reappear (clock and battery only) until the cursor leaves. It also returns through the hotkey, the tray menu or by itself after the snooze time; phone notifications still show it briefly. Hiding with the hotkey or tray menu has no peek, so it never covers what is under it. Settings > General > "How the pill opens" chooses between hover and left click.

## Language

`L.T(text)` looks the Indonesian source text up in `Strings.En` and returns the English version when English is active; unknown text stays Indonesian. `UI.T` translates automatically, other places call `L.T` or `L.F` (format strings). The language is read once at start (Settings > General > Language: auto, Indonesian, English) and applies after a restart.

## Phone link

- Server: `ShareServer` on fixed ports 47890 (HTTP, files) and 47891 (TLS, control and camera), falling back to random ports on conflict. It accepts only private, loopback and Tailscale (100.64.0.0/10) addresses, protected by a fixed token and a block-able device list. A plain `TcpListener` is used so no admin rights or urlacl are needed.
- Pairing link: `zaxlet://pair?lan=..&ts=..&tp=..&t=..&f=..` carries the LAN address, the Tailscale address (re-read every time it is used, because it changes with the Tailscale account), the TLS port, the token and the SHA-256 fingerprint of the self-signed certificate, which the phone pins.
- The phone service keeps a `/ctl` socket (LAN first, then Tailscale) with a 40 s read timeout and a ping/pong; notifications are sent as JSON including the last 8 messages, and chat replies run the notification's own reply action. Camera frames go through the softcam virtual camera (needs UAC once to register); aspect ratio 1:1, 4:3, 9:16 or 16:9, with landscape detected automatically.
- Firewall: rule "ZaxLet HP" for ports 47890–47891; the Settings button needs UAC once.

## Android app

Sources in `android/` (package `com.zaxlet.hp`), built by `android/build.ps1` with aapt2, javac, d8, zipalign and apksigner (no Gradle). The APK is copied to `dist\` and embedded in the installer so the PC can serve it over HTTP for the first QR code.

## Installer

`installer/build.ps1` zips `dist\` (without `.pdb`) and embeds it in `installer/Setup.cs`, compiled with the .NET Framework `csc.exe` (C# 5, WinForms). It installs per user to `%LOCALAPPDATA%\Programs\ZaxLet`, adds Start Menu and desktop shortcuts, optional autostart, a copy of itself as `Uninstall.exe` and an uninstall registry entry. If the .NET 8 Desktop Runtime is missing it offers to download the official installer from Microsoft.
