# Membangun ZaxLet.apk tanpa Gradle: aapt2 + javac + d8 + zipalign + apksigner dari SDK di ..\.android
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$sdk  = Join-Path $root "..\.android" | Resolve-Path
$bt   = Join-Path $sdk "build-tools\35.0.0"
$jar  = Join-Path $sdk "platforms\android-34\android.jar"
$out  = Join-Path $root "out"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$out\classes","$out\dex" | Out-Null

& "$bt\aapt2.exe" compile --dir "$root\res" -o "$out\res.zip"
& "$bt\aapt2.exe" link -o "$out\base.apk" -I $jar --manifest "$root\AndroidManifest.xml" --min-sdk-version 26 --target-sdk-version 34 --version-code 1 --version-name 1.0 "$out\res.zip"

$src = Get-ChildItem "$root\src" -Recurse -Filter *.java | ForEach-Object FullName
& javac --release 8 -g:none -Xlint:-options -encoding UTF-8 -classpath $jar -d "$out\classes" $src
$cls = Get-ChildItem "$out\classes" -Recurse -Filter *.class | ForEach-Object FullName
& "$bt\d8.bat" --lib $jar --min-api 26 --output "$out\dex" $cls

# classes.dex ke dalam APK
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open("$out\base.apk", "Update")
[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$out\dex\classes.dex", "classes.dex")
$zip.Dispose()

& "$bt\zipalign.exe" -f 4 "$out\base.apk" "$out\aligned.apk"
$ks = Join-Path $root "zaxlet.keystore"
if (-not (Test-Path $ks)) {
    & keytool -genkeypair -keystore $ks -storepass island123 -keypass island123 -alias island -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=ZaxLet HP" | Out-Null
}
& "$bt\apksigner.bat" sign --ks $ks --ks-pass pass:island123 --key-pass pass:island123 --out "$root\ZaxLet.apk" "$out\aligned.apk"
& "$bt\apksigner.bat" verify "$root\ZaxLet.apk"
Get-Item "$root\ZaxLet.apk" | Select-Object FullName, Length



