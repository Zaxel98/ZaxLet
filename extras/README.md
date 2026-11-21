# Extras (fitur yang dikeluarkan dari build)

Kode fitur di bawah dipindahkan keluar dari build utama (tidak dikompilasi, tidak dihapus permanen):

- `eye/` — Istirahat mata (`EyeView.cs`, timer & pengaturan terkait).
- `pomodoro-focus/` — Pomodoro & Mode Fokus (`FocusService.cs`, timer & pengaturan terkait).
- `quiet-hours/` — Jam tenang (`QuietHours.cs`, pengaturan & hook terkait).
- `wishlist/` — Wishlist, jembatan ekstensi browser, dan ekstensi browser (`Wishlist.cs`, `WishBridge.cs`, `WishBridgeCore.cs`, `WishParse.cs`, `WishlistView.cs`, `BrowserExtension/`, `WishGuide`, perintah cepat `w <link>`).
- `meeting-mode/` — Mode rapat (`MeetingService.cs`, `MeetingDetect.cs`, tombol mikrofon di pill, jeda musik & tahan notifikasi HP selama rapat).

## Urutan memulihkan

Patch hook harus dibalik berurutan: `git apply -R extras/hooks-removed-b.patch` dulu, lalu `git apply -R extras/hooks-removed-a.patch` (a gagal bila b belum dibalik karena hunk-nya tumpang tindih). Setelah itu pindahkan berkas fitur dari `extras/<nama>/` kembali ke `src/ZaxLet/` dengan sub-folder yang sama (ekstensi browser ke `src/BrowserExtension/`), kembalikan baris Compile Include tes, dan salin kode tes dari `tests.cs.txt`. Sudah diuji bahwa b lalu a berlaku bersih pada HEAD ini.
