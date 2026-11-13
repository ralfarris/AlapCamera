$ErrorActionPreference = 'Stop'
# Build AlapCamera.exe pakai compiler C# bawaan Windows (.NET Framework 4.x) — tanpa install Python/dependency apa pun.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4) tidak ditemukan.' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$ico = Join-Path $root 'assets\alapcamera.ico'
$icoParam = if (Test-Path $ico) { "/win32icon:$ico" } else { "" }

$resParams = @()
foreach ($dll in @('AlapMfCam64.dll', 'UnityCaptureFilter64.dll', 'UnityCaptureFilter32.dll')) {
    $p = Join-Path $root "assets\$dll"
    if (Test-Path $p) {
        $resParams += "/resource:$p,AlapCamera.Drivers.$dll"
    }
}
$onnx = Join-Path $root 'assets\selfie_segmentation.onnx'
if (Test-Path $onnx) {
    $resParams += "/resource:$onnx,AlapCamera.Models.selfie_segmentation.onnx"
}

& $csc /nologo /target:winexe /optimize+ /unsafe /platform:anycpu /codepage:65001 `
    "/out:$dist\AlapCamera.exe" "/win32manifest:$root\src\app.manifest" $icoParam $resParams `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build gagal ($LASTEXITCODE)" }
$size = [math]::Round((Get-Item "$dist\AlapCamera.exe").Length / 1KB)
Write-Host "Build OK -> dist\AlapCamera.exe ($size KB)"
