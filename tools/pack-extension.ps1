# Kemas ekstensi browser Wishlist (src\BrowserExtension) jadi dist\extension\zaxlet-wishlist.xpi.
# .xpi/.zip/.jar pada dasarnya sama: arsip zip dengan manifest.json di akar. Firefox rilis hanya memasang .xpi yang sudah DITANDATANGANI
# (addons.mozilla.org, jalur "On your own"/unlisted, gratis); tanpa tanda tangan ia hanya bisa dimuat sementara lewat about:debugging.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'src\BrowserExtension'
$out = Join-Path $root 'dist\extension'
New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$xpi = Join-Path $out 'zaxlet-wishlist.xpi'
Remove-Item $xpi -ErrorAction SilentlyContinue
$za = [System.IO.Compression.ZipFile]::Open($xpi, 'Create')
foreach ($f in Get-ChildItem $src -File) {
    # nama entri = nama berkas saja (tanpa folder, tanpa backslash): syarat Firefox
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $f.Name, 'Optimal')
}
$za.Dispose()
"{0}  {1:N1} KB" -f $xpi, ((Get-Item $xpi).Length / 1KB)
