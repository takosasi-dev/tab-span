# 配る一式を dist\ に作る: PC 側(.NET 同梱のフォルダの zip)と端末側の APK。版は android\app\build.gradle.kts の versionName。
# 署名の鍵は環境変数で渡したときだけ使う(docs\signing.md)。渡さなければ debug の鍵の APK を作る。
# Windows PowerShell 5.1 で動く書き方にする。zip は .NET の ZipFile で作る(Compress-Archive は区切りが「\」になる)。
param(
    [switch]$SkipPc,
    [switch]$SkipAndroid
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gradle = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'android\app\build.gradle.kts')
if ($gradle -notmatch 'versionName\s*=\s*"([^"]+)"') { throw 'versionName が見つかりません' }
$version = $Matches[1]
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$made = @()

if (-not $SkipPc) {
    $name = "TabSpan-v$version-win-x64"
    $folder = Join-Path $dist $name
    if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
    foreach ($proj in 'TabSpan.App', 'TabSpan.Probe') {
        & dotnet publish (Join-Path $root "pc\src\$proj\$proj.csproj") -c Release -r win-x64 --self-contained true -o $folder -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "$proj の publish に失敗しました" }
    }
    foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.en.md') {
        Copy-Item (Join-Path $root $doc) $folder
    }
    $zip = Join-Path $dist "$name.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    # 区切りを「/」にして 1 つずつ入れる(5.1 の CreateFromDirectory は「\」で書く)
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -Recurse -File $folder) {
            $entry = $name + '/' + $file.FullName.Substring($folder.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }
    $made += $zip
}

if (-not $SkipAndroid) {
    $signed = [bool]$env:TABSPAN_KEYSTORE
    $task = if ($signed) { ':app:assembleRelease' } else { ':app:assembleDebug' }
    Push-Location (Join-Path $root 'android')
    try {
        & .\gradlew.bat --no-daemon -q $task
        if ($LASTEXITCODE -ne 0) { throw 'APK のビルドに失敗しました' }
    } finally {
        Pop-Location
    }
    $kind = if ($signed) { 'release' } else { 'debug' }
    $built = Get-ChildItem (Join-Path $root "android\app\build\outputs\apk\$kind") -Filter '*.apk' | Select-Object -First 1
    if (-not $built) { throw "APK($kind)が見つかりません" }
    $apk = Join-Path $dist "TabSpan-v$version-android.apk"
    Copy-Item $built.FullName $apk -Force
    if (-not $signed) { Write-Host 'APK は debug の鍵で署名しています(仮)。正式な鍵は docs\signing.md' }
    $made += $apk
}

foreach ($f in $made) {
    $hash = (Get-FileHash -Algorithm SHA256 $f).Hash.ToLowerInvariant()
    Write-Host ("{0}  {1}" -f $hash, (Split-Path -Leaf $f))
}
