# Installs the "AlapCamera" virtual webcam device into HKLM (Windows).
# Required so elevated apps like Streamlabs Desktop (Running as administrator)
# can load the DirectShow COM filter.
param(
    [string]$Name = "AlapCamera",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Name", "`"$Name`"")
    if ($Uninstall) { $argList += "-Uninstall" }
    Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$dir = Join-Path $env:ProgramData "AlapCamera\driver"
$localCache = Join-Path $env:LOCALAPPDATA "AlapCamera\driver"
$base = "https://github.com/schellingb/UnityCapture/raw/master/Install"
$dlls = @("UnityCaptureFilter64.dll", "UnityCaptureFilter32.dll")

$clsid = "{5C2CD55C-92AD-4999-8666-912BD3E70010}"
$cat = "{860BB310-5D01-11D0-BD3B-00A0C911CE86}"

if ($Uninstall) {
    foreach ($dll in $dlls) {
        $path = Join-Path $dir $dll
        if (Test-Path $path) {
            if ($dll -like "*32*") { & "$env:WINDIR\SysWOW64\regsvr32.exe" /s /u $path }
            else { & "$env:WINDIR\System32\regsvr32.exe" /s /u $path }
        }
    }
    exit
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
foreach ($dll in $dlls) {
    $path = Join-Path $dir $dll
    $cached = Join-Path $localCache $dll
    if (-not (Test-Path $path)) {
        if (Test-Path $cached) {
            Copy-Item -Force $cached $path
        } else {
            Invoke-WebRequest -UseBasicParsing -Uri "$base/$dll" -OutFile $path
        }
    }
}

# Register in HKLM for both 64-bit (Streamlabs/OBS) and 32-bit apps
& "$env:WINDIR\System32\regsvr32.exe" /s (Join-Path $dir "UnityCaptureFilter64.dll")
& "$env:WINDIR\SysWOW64\regsvr32.exe" /s (Join-Path $dir "UnityCaptureFilter32.dll")

# Explicitly brand FriendlyName as "AlapCamera" in HKLM (64-bit & 32-bit)
$regPaths = @(
    "HKLM:\SOFTWARE\Classes\CLSID\$cat\Instance\$clsid",
    "HKLM:\SOFTWARE\Classes\CLSID\$clsid",
    "HKLM:\SOFTWARE\WOW6432Node\Classes\CLSID\$cat\Instance\$clsid",
    "HKLM:\SOFTWARE\WOW6432Node\Classes\CLSID\$clsid"
)
foreach ($rp in $regPaths) {
    if (Test-Path $rp) {
        Set-ItemProperty -Path $rp -Name "FriendlyName" -Value $Name -Force
        Set-ItemProperty -Path $rp -Name "(Default)" -Value $Name -Force
    }
}

# Remove any HKCU shadow keys so HKLM is used cleanly everywhere
$cuPaths = @(
    "HKCU:\Software\Classes\CLSID\$cat\Instance\$clsid",
    "HKCU:\Software\Classes\CLSID\$clsid"
)
foreach ($cp in $cuPaths) {
    if (Test-Path $cp) {
        Remove-Item -Path $cp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
