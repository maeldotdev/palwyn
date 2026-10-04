# Puts the files the Windows app bundles but git doesn't keep into its Assets folder:
# - adb (Android SDK Platform-Tools, Apache-2.0) in Assets\adb, pinned and checksum-verified
# - the emergency-screen phone helper in Assets\Emergency\palwyn-emergency.jar, built from apps/android/emergency
param([switch] $SkipEmergency)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot))
$assets = Join-Path $repo 'apps\windows\src\Palwyn.App\Assets'

$version = 'r37.0.1'
$url = "https://dl.google.com/android/repository/platform-tools_$version-win.zip"
$sha256 = '45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99'
$adbDir = Join-Path $assets 'adb'
$stamp = Join-Path $adbDir 'version.txt'
if (-not ((Test-Path $stamp) -and (Get-Content $stamp) -eq $version)) {
    $zip = Join-Path ([IO.Path]::GetTempPath()) "platform-tools_$version-win.zip"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) { Remove-Item $zip; throw "SHA-256 mismatch for $url" }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "platform-tools_$version"
    Expand-Archive $zip $tmp -Force
    New-Item $adbDir -ItemType Directory -Force | Out-Null
    foreach ($f in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt') { Copy-Item (Join-Path $tmp "platform-tools\$f") $adbDir -Force }
    Set-Content $stamp $version
    Remove-Item $zip, $tmp -Recurse -Force
    Write-Host "adb $version -> $adbDir"
}

$android = Join-Path $repo 'apps\android'
if ($SkipEmergency -or -not (Test-Path (Join-Path $android 'emergency'))) { return }
$jar = Join-Path $assets 'Emergency\palwyn-emergency.jar'
$sources = Get-ChildItem (Join-Path $android 'emergency') -Recurse -File | Where-Object FullName -notmatch '\\build\\'
if ((Test-Path $jar) -and -not ($sources | Where-Object LastWriteTime -gt (Get-Item $jar).LastWriteTime)) { return }
Push-Location $android
try {
    & .\gradlew.bat :emergency:assembleRelease --console=plain -q
    if ($LASTEXITCODE) { throw 'emergency helper build failed' }
} finally { Pop-Location }
New-Item (Split-Path $jar) -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $android 'emergency\build\outputs\apk\release\emergency-release-unsigned.apk') $jar -Force
Write-Host "emergency helper -> $jar"
