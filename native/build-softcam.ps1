# Mengompilasi softcam (kamera virtual DirectShow, MIT) menjadi softcam.dll memakai llvm-mingw di ..\.tools.
# Jalankan dari terminal Anda sendiri:  powershell -ExecutionPolicy Bypass -File native\build-softcam.ps1
$ErrorActionPreference = "Continue"   # stderr clang bukan kegagalan skrip; yang dinilai adalah exit code
$root = Split-Path $PSScriptRoot -Parent
$src  = Join-Path $root ".tools\softcam-src\softcam-1.8.1\src"
$bin  = (Get-ChildItem (Join-Path $root ".tools\llvm\*\bin\clang++.exe")).DirectoryName
$out  = Join-Path $root ".tools\build"
$log  = Join-Path $out "log.txt"
$env:PATH = "$bin;$env:PATH"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

# Satu kebiasaan khas MSVC di wxdebug.cpp: memanggil konstruktor sebagai pernyataan ("CDisp::CDisp(pp);"); clang menolaknya.
$wx = "$src\baseclasses\wxdebug.cpp"
$latin1 = [Text.Encoding]::GetEncoding(28591)   # baca/tulis per-byte agar isi file tidak berubah
$t = [IO.File]::ReadAllText($wx, $latin1)
if ($t.Contains("CDisp::CDisp(pp);")) { [IO.File]::WriteAllText($wx, $t.Replace("CDisp::CDisp(pp);", "{ CDisp cd_tmp(pp); }"), $latin1) }

$files = @(Get-ChildItem "$src\baseclasses\*.cpp", "$src\softcamcore\*.cpp", "$src\softcam\softcam.cpp", "$PSScriptRoot\mingw_stubs.cpp" | ForEach-Object FullName)
$objs = @(); $failed = 0
foreach ($f in $files) {
    $name = [IO.Path]::GetFileNameWithoutExtension($f)
    $o = Join-Path $out ($name + ".o")
    $txt = & cmd /c "clang++ -target x86_64-w64-mingw32 -std=c++17 -O2 -w -DWIN32 -D_CRT_SECURE_NO_WARNINGS -DUNICODE -D_UNICODE -D_WIN32_WINNT=0x0601 -I`"$src`" -I`"$src\baseclasses`" -fms-extensions -include `"$PSScriptRoot\sal_compat.h`" -c `"$f`" -o `"$o`" 2>&1" | Out-String
    if ($LASTEXITCODE -ne 0) {
        $failed++
        Add-Content $log "=== GAGAL: $name ===`r`n$txt"
        Write-Host "GAGAL: $name" -ForegroundColor Red
        ($txt -split "`r?`n" | Where-Object { $_ -match "error" } | Select-Object -First 6) | Write-Host
    } else { $objs += $o; Write-Host "ok: $name" }
}
if ($failed) { Write-Host "`n$failed file gagal. Log lengkap: $log  (salin yang di atas ke Claude)" -ForegroundColor Yellow; exit 1 }

$dll = Join-Path $root "dist\softcam.dll"
$objList = ($objs | ForEach-Object { "`"$_`"" }) -join " "
$txt = & cmd /c "clang++ -target x86_64-w64-mingw32 -shared -static -o `"$dll`" $objList `"$src\softcam\softcam.def`" -lwinmm -lole32 -loleaut32 -luuid -lstrmiids -lgdi32 -luser32 -ladvapi32 -lshell32 -lcomdlg32 2>&1" | Out-String
if ($LASTEXITCODE -ne 0) { Add-Content $log "=== LINK ===`r`n$txt"; Write-Host "Link gagal:" -ForegroundColor Yellow; Write-Host ($txt -split "`r?`n" | Select-Object -First 15); exit 1 }
Get-Item $dll | Select-Object FullName, Length
Write-Host "Selesai. Jalankan ulang ZaxLet, lalu nyalakan kamera pertama kali (UAC muncul sekali)."
