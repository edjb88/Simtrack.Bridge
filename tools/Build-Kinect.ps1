# Compila el puente de Kinect v1 (Simtrack.KinectHost.exe)
#
#   powershell -ExecutionPolicy Bypass -File .\tools\Build-Kinect.ps1
#
# Requiere Kinect for Windows SDK v1.8 (drivers + Microsoft.Kinect.dll) y el
# Developer Toolkit v1.8.0 (Face Tracking: Microsoft.Kinect.Toolkit.FaceTracking.dll).

param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'Simtrack.Kinect\bin'
$source = Join-Path $root 'Simtrack.Kinect\KinectHost.cs'

$sdkAssemblies = 'C:\Program Files\Microsoft SDKs\Kinect\v1.8\Assemblies'
$toolkitBin = 'C:\Program Files\Microsoft SDKs\Kinect\Developer Toolkit v1.8.0\bin'
$toolkitRedist = 'C:\Program Files\Microsoft SDKs\Kinect\Developer Toolkit v1.8.0\Redist\amd64'

$kinectDll = Join-Path $sdkAssemblies 'Microsoft.Kinect.dll'
$faceDll = Join-Path $toolkitBin 'Microsoft.Kinect.Toolkit.FaceTracking.dll'

if (-not (Test-Path $kinectDll)) {
    throw 'No se encuentra Microsoft.Kinect.dll. Instala Kinect for Windows SDK v1.8.'
}

if (-not (Test-Path $faceDll)) {
    throw "No se encuentra Microsoft.Kinect.Toolkit.FaceTracking.dll. Instala el Developer Toolkit v1.8.0 ($toolkitBin)."
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
    throw 'No se encuentra csc.exe (.NET Framework 4.x).'
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host 'Compilando KinectHost con Face Tracking' -ForegroundColor Cyan

& $csc /nologo /target:exe /platform:x64 `
    "/out:$outDir\Simtrack.KinectHost.exe" `
    "/r:$kinectDll" `
    "/r:$faceDll" `
    /r:System.Drawing.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw 'La compilacion ha fallado.'
}

# DLLs nativas del Face Tracking: tienen que estar junto al .exe.
foreach ($native in 'FaceTrackLib.dll', 'FaceTrackData.dll') {
    $origin = Join-Path $toolkitRedist $native

    if (Test-Path $origin) {
        Copy-Item $origin $outDir -Force
        Write-Host "Copiada $native"
    }
    else {
        Write-Host "Aviso: no se encuentra $native en $toolkitRedist" -ForegroundColor Yellow
    }
}

Write-Host "Listo: $outDir\Simtrack.KinectHost.exe" -ForegroundColor Green
Write-Host 'Copia el .exe (y las DLLs nativas) junto a Simtrack.App.exe o a la carpeta Kinect\ y la app lo arranca sola.'
