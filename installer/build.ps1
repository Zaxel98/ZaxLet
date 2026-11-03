# Bangun ZaxLetSetup.exe: zip isi dist\ (tanpa .pdb) lalu tanamkan ke pemasang yang dikompilasi dengan csc .NET Framework.
# Jalankan setelah build.cmd (dan Android\build.ps1 bila APK berubah) supaya dist\ terbaru.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out 'payload.zip'
Remove-Item $zip -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = Join-Path $root 'dist'
$za = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in Get-ChildItem $dist -Recurse -File | Where-Object { $_.Extension -ne '.pdb' }) {
    $rel = $f.FullName.Substring($dist.Length + 1).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $rel, 'Optimal')
}
$za.Dispose()

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe = Join-Path $out 'ZaxLetSetup.exe'
& $csc /nologo /target:winexe /optimize+ /out:$exe `
    /win32icon:"$root\src\ZaxLet\Assets\zaxlet.ico" `
    /resource:"$zip,payload.zip" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "$PSScriptRoot\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw 'csc gagal' }
"{0}  {1:N1} MB" -f $exe, ((Get-Item $exe).Length / 1MB)
