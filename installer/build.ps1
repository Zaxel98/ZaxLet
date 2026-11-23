# Build ZaxLetSetup.exe: zip dist\ contents (excluding .pdb) and embed into the installer compiled with .NET Framework csc.
# Run after build.cmd (and Android\build.ps1 if APK changed) so dist\ is up to date.
param([string]$Dist = '')   # release contents folder; empty = dist\ (for releases, build to a clean folder so developer logs/diagnostics are excluded)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out 'payload.zip'
Remove-Item $zip -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = if ($Dist) { (Resolve-Path $Dist).Path } else { Join-Path $root 'dist' }
$za = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in Get-ChildItem $dist -Recurse -File | Where-Object { $_.Extension -ne '.pdb' }) {
    $rel = $f.FullName.Substring($dist.Length + 1).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $rel, 'Optimal')
}
$za.Dispose()

# auto-update archive: same contents as the installer payload, plus a hash file. Upload both to GitHub Release
# alongside ZaxLetSetup.exe (asset names must be exactly ZaxLet-update.zip and ZaxLet-update.zip.sha256).
$upd = Join-Path $out 'ZaxLet-update.zip'
Copy-Item $zip $upd -Force
$hash = (Get-FileHash $upd -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$upd.sha256" "$hash  ZaxLet-update.zip" -Encoding ASCII
"{0}  sha256 {1}" -f $upd, $hash

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe = Join-Path $out 'ZaxLetSetup.exe'
& $csc /nologo /target:winexe /optimize+ /out:$exe `
    /win32icon:"$root\src\ZaxLet\Assets\zaxlet.ico" `
    /resource:"$zip,payload.zip" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "$PSScriptRoot\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw 'csc gagal' }
"{0}  {1:N1} MB" -f $exe, ((Get-Item $exe).Length / 1MB)
