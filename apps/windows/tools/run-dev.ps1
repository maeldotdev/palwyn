# Build, register the packaged app from the build output (needs Developer Mode) and launch it.
# Debug switches (see DevArgs.cs): -AppArgs "--demo=connected --flyout"
param([string] $AppArgs = '', [switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$proj = Join-Path $root 'src\Palwyn.App\Palwyn.App.csproj'

Get-Process Palwyn.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (-not $NoBuild) {
    dotnet build $proj -p:Platform=x64 -v:q -nologo | Out-Host
    if ($LASTEXITCODE) { throw 'build failed' }
}

$manifest = Get-ChildItem (Join-Path $root 'src\Palwyn.App\bin\x64\Debug') -Recurse -Filter AppxManifest.xml | Select-Object -First 1
Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion
$aumid = (Get-AppxPackage Palwyn.Dev).PackageFamilyName + '!App'

if (-not ('PB.Launcher' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace PB {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ApplicationActivationManager { }
    public static class Launcher {
        public static uint Activate(string aumid, string args) {
            var m = (IApplicationActivationManager)new ApplicationActivationManager();
            uint pid;
            int hr = m.ActivateApplication(aumid, args, 0, out pid);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            return pid;
        }
    }
}
'@
}
"launched $aumid pid=$([PB.Launcher]::Activate($aumid, $AppArgs)) args='$AppArgs'"
