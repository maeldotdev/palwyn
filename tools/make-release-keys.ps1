# Creates Palwyn's release signing keys and gives them to GitHub as repository secrets.
# Run it once, yourself: .\tools\make-release-keys.ps1 -OutDir "D:\Palwyn release keys"
#
# - Android: palwyn-release.jks. Every Android update must be signed with this same key: if it's lost,
#   users can't update and must uninstall and reinstall. Keep the folder backed up somewhere safe.
# - Windows: a self-signed code-signing certificate (CN=Mael), used until SignPath signing is approved.
#
# Needs: the GitHub CLI signed in (gh auth status) and Java's keytool (Android Studio's JBR is found).
param([Parameter(Mandatory)] [string] $OutDir, [string] $Repo = 'maeldotdev/palwyn')
$ErrorActionPreference = 'Stop'

if (Test-Path (Join-Path $OutDir 'palwyn-release.jks')) { throw "Keys already exist in $OutDir. Don't make new ones: updates need the old key." }
New-Item $OutDir -ItemType Directory -Force | Out-Null

function New-Password { $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) -replace '[+/=]', 'x' }
function Set-Secret($name, $value) { gh secret set $name -R $Repo --body $value; if ($LASTEXITCODE) { throw "gh secret set $name failed" } }

$keytool = @("$env:JAVA_HOME\bin\keytool.exe", 'D:\Android Studio\jbr\bin\keytool.exe', "$env:ProgramFiles\Android\Android Studio\jbr\bin\keytool.exe") |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $keytool) { throw 'keytool not found: set JAVA_HOME to a JDK' }

# Android
$jks = Join-Path $OutDir 'palwyn-release.jks'
$androidPassword = New-Password
& $keytool -genkeypair -keystore $jks -storetype PKCS12 -alias palwyn -keyalg RSA -keysize 4096 -validity 10000 `
    -storepass $androidPassword -keypass $androidPassword -dname 'CN=Mael' | Out-Null
if ($LASTEXITCODE) { throw 'keytool failed' }

# Windows
$windowsPassword = New-Password
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Mael' -FriendlyName 'Palwyn release signing' `
    -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 4096 -NotAfter (Get-Date).AddYears(5) `
    -TextExtension @('2.5.29.19={text}')
$pfx = Join-Path $OutDir 'palwyn-windows-signing.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $windowsPassword -AsPlainText -Force) | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'palwyn-windows-signing.cer') | Out-Null
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"

@"
Palwyn release keys, created $(Get-Date -Format 'yyyy-MM-dd').
Keep this folder private and backed up (for example on a USB drive and in a password manager).

Android keystore: palwyn-release.jks   alias: palwyn   password: $androidPassword
Windows certificate: palwyn-windows-signing.pfx   password: $windowsPassword
"@ | Set-Content (Join-Path $OutDir 'passwords.txt') -Encoding utf8

Set-Secret ANDROID_KEYSTORE_BASE64 ([Convert]::ToBase64String([IO.File]::ReadAllBytes($jks)))
Set-Secret ANDROID_KEYSTORE_PASSWORD $androidPassword
Set-Secret WINDOWS_PFX_BASE64 ([Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx)))
Set-Secret WINDOWS_PFX_PASSWORD $windowsPassword

Write-Host "Done. Keys and passwords are in $OutDir; GitHub has the four release secrets."
Write-Host 'Back up that folder now. Never commit it or share it.'
