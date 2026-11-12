# Installs the "AlapCamera" virtual webcam device (Windows).
#
# Uses the open-source Unity Capture DirectShow filter
# (https://github.com/schellingb/UnityCapture, MIT license), registered under
# the name "AlapCamera". After this, "AlapCamera" shows up as a webcam in
# Streamlabs, OBS, Zoom, Google Meet, Discord, etc.
#
# Run:  powershell -ExecutionPolicy Bypass -File scripts\install_virtualcam.ps1
param(
    [string]$Name = "AlapCamera",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

# --- self-elevate (registering a camera device needs admin rights) ---------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = @("-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Name", "`"$Name`"")
    if ($Uninstall) { $argList += "-Uninstall" }
    Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$dir = Join-Path $env:ProgramData "AlapCamera\driver"
$base = "https://github.com/schellingb/UnityCapture/raw/master/Install"
$dlls = @("UnityCaptureFilter64.dll", "UnityCaptureFilter32.dll")

if ($Uninstall) {
    foreach ($dll in $dlls) {
        $path = Join-Path $dir $dll
        if (Test-Path $path) {
            if ($dll -like "*32*") { & "$env:WINDIR\SysWOW64\regsvr32.exe" /s /u $path }
            else { & "$env:WINDIR\System32\regsvr32.exe" /s /u $path }
        }
    }
    Write-Host "AlapCamera virtual camera removed." -ForegroundColor Green
    Start-Sleep 2
    exit
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
foreach ($dll in $dlls) {
    $path = Join-Path $dir $dll
    if (-not (Test-Path $path)) {
        Write-Host "Downloading $dll ..."
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$dll" -OutFile $path
    }
}

# 64-bit apps (OBS, Streamlabs, Zoom...) and 32-bit apps
& "$env:WINDIR\System32\regsvr32.exe" /s "/i:UnityCaptureName=$Name" (Join-Path $dir "UnityCaptureFilter64.dll")
& "$env:WINDIR\SysWOW64\regsvr32.exe" /s "/i:UnityCaptureName=$Name" (Join-Path $dir "UnityCaptureFilter32.dll")

Write-Host "Virtual camera '$Name' installed." -ForegroundColor Green
Write-Host "Restart Streamlabs/OBS/Zoom, then pick '$Name' as the video device."
Start-Sleep 3
