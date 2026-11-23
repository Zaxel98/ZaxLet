# Package the Wishlist browser extension (src\BrowserExtension) into dist\extension\zaxlet-wishlist.xpi.
# .xpi/.zip/.jar are essentially the same: a zip archive with manifest.json at the root. Release Firefox only installs SIGNED .xpi files
# (addons.mozilla.org, "On your own"/unlisted channel, free); without a signature it can only be loaded temporarily via about:debugging.
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
    # entry name = filename only (no folder, no backslash): Firefox requirement
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $f.Name, 'Optimal')
}
$za.Dispose()
"{0}  {1:N1} KB" -f $xpi, ((Get-Item $xpi).Length / 1KB)
