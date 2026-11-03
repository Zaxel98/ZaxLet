# Third-party notices

ZaxLet is built on the components below. Each keeps its own license.

| Component | Used for | License | Source |
|---|---|---|---|
| QRCoder 1.6.0 (Raffael Herrmann) | QR codes for phone pairing | MIT | https://github.com/codebude/QRCoder |
| softcam 1.8.1 (tshino) | Virtual camera (DirectShow), compiled from source into `softcam.dll` | MIT, text in [docs/licenses/softcam-MIT.txt](docs/licenses/softcam-MIT.txt) | https://github.com/tshino/softcam |
| Microsoft.Windows.SDK.NET, WinRT.Runtime | Windows media session (SMTC), notifications | MIT (Microsoft) | https://github.com/microsoft/CsWinRT |

## Optional downloads (not redistributed)

These are fetched on the user's request from Settings > Add-ons, checked against a pinned SHA-256, and stored under `%LOCALAPPDATA%\ZaxLet`. They are not part of this repository or of the installer.

| Add-on | Source | Note |
|---|---|---|
| LibNMeCab 0.10.1 and IPAdic dictionary | nuget.org | Kanji to romaji. Each package carries its own license terms. |
| ffmpeg (gyan.dev build) | gyan.dev | Screen recording. Licensed under the LGPL/GPL terms of that build. |

## Online services

Lyrics and other data come from third-party services (LRCLIB, LyricsPlus, NetEase Cloud Music, QQ Music, Kugou, Open-Meteo, ipwho.is, OpenStreetMap Nominatim). ZaxLet is not affiliated with any of them and queries them only as described in the README.
