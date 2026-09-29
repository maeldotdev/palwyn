# Registers the spike as a loose (unsigned) package so it runs with package identity.
# Requires Windows Developer Mode. Remove with: Get-AppxPackage Palwyn.HfpSpike | Remove-AppxPackage
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'bin\pkg'
dotnet publish "$PSScriptRoot\HfpSpike.csproj" -c Release -r win-x64 --self-contained false -o $out | Out-Null
Copy-Item "$PSScriptRoot\AppxManifest.xml" $out -Force

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 150, 150
$bmp.Save((Join-Path $out 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Get-AppxPackage Palwyn.HfpSpike | Remove-AppxPackage
Add-AppxPackage -Register (Join-Path $out 'AppxManifest.xml')
$pfn = (Get-AppxPackage Palwyn.HfpSpike).PackageFamilyName
Start-Process "shell:AppsFolder\$pfn!App"
Start-Sleep -Seconds 12
Get-Content (Join-Path $out 'spike-output.txt')
