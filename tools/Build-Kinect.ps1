# Compila el puente de Kinect v1 (Simtrack.KinectHost.exe)
#
#   powershell -ExecutionPolicy Bypass -File .\tools\Build-Kinect.ps1
#
# Requiere Kinect for Windows Runtime/SDK v1.8 (el runtime ya instala los
# drivers y Microsoft.Kinect.dll queda en la GAC).

param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'Simtrack.Kinect\bin'
$source = Join-Path $root 'Simtrack.Kinect\KinectHost.cs'

$gacRoot = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Kinect'

if (-not (Test-Path $gacRoot)) {
    throw 'No se encuentra Microsoft.Kinect.dll en la GAC. Instala Kinect for Windows SDK v1.8.'
}

$versionDir = Get-ChildItem $gacRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
$kinectDll = Join-Path $versionDir.FullName 'Microsoft.Kinect.dll'

if (-not (Test-Path $kinectDll)) {
    throw "No se encuentra Microsoft.Kinect.dll en $($versionDir.FullName)"
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $csc)) {
    throw "No se encuentra csc.exe (.NET Framework 4.x)."
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "Compilando con $kinectDll" -ForegroundColor Cyan

& $csc /nologo /target:exe /platform:x64 `
    "/out:$outDir\Simtrack.KinectHost.exe" `
    "/r:$kinectDll" `
    /r:System.Drawing.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "La compilacion ha fallado."
}

Write-Host "Listo: $outDir\Simtrack.KinectHost.exe" -ForegroundColor Green
Write-Host 'Copia ese .exe junto a Simtrack.App.exe (o a una subcarpeta Kinect) y la app lo arranca sola.'
