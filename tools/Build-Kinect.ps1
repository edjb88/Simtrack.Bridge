# Compila el puente de Kinect v1 (Simtrack.KinectHost.exe)
#
#   powershell -ExecutionPolicy Bypass -File .\tools\Build-Kinect.ps1
#
# Requiere:
#  - Kinect for Windows SDK v1.8 (drivers + Microsoft.Kinect.dll)
#  - Kinect for Windows Developer Toolkit v1.8.0 (Face Tracking)
#  - OpenCvSharp4 (ya descargado en Simtrack.Kinect\deps, o lo descarga este script)

param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'Simtrack.Kinect\bin'
$source = Join-Path $root 'Simtrack.Kinect\KinectHost.cs'
$rgbSource = Join-Path $root 'Simtrack.Kinect\RgbHeadPose.cs'
$deps = Join-Path $root 'Simtrack.Kinect\deps'

$sdkAssemblies = 'C:\Program Files\Microsoft SDKs\Kinect\v1.8\Assemblies'
$toolkitBin = 'C:\Program Files\Microsoft SDKs\Kinect\Developer Toolkit v1.8.0\bin'
$toolkitRedist = 'C:\Program Files\Microsoft SDKs\Kinect\Developer Toolkit v1.8.0\Redist\amd64'

$kinectDll = Join-Path $sdkAssemblies 'Microsoft.Kinect.dll'
$faceDll = Join-Path $toolkitBin 'Microsoft.Kinect.Toolkit.FaceTracking.dll'

$openCvVersion = '4.13.0.20261107'
$openCvDll = Join-Path $deps "extracted\opencvsharp4.$openCvVersion\lib\netstandard2.0\OpenCvSharp.dll"
$openCvNative = Join-Path $deps "extracted\opencvsharp4.runtime.win.$openCvVersion\runtimes\win-x64\native\OpenCvSharpExtern.dll"
$model = Join-Path $deps 'face_detection_yunet_2023mar.onnx'
$haar = Join-Path $deps 'haarcascade_frontalface_default.xml'
$facemark = Join-Path $deps 'lbfmodel.yaml'

if (-not (Test-Path $kinectDll)) {
    throw 'No se encuentra Microsoft.Kinect.dll. Instala Kinect for Windows SDK v1.8.'
}

if (-not (Test-Path $faceDll)) {
    throw "No se encuentra Microsoft.Kinect.Toolkit.FaceTracking.dll. Instala el Developer Toolkit v1.8.0 ($toolkitBin)."
}

if (-not (Test-Path $openCvDll)) {
    throw "No se encuentra OpenCvSharp. Descarga el paquete opencvsharp4 $openCvVersion en $deps."
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$netStandard = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\netstandard.dll'

if (-not (Test-Path $netStandard)) {
    $netStandard = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\netstandard.dll'
}

if (-not (Test-Path $csc)) {
    throw 'No se encuentra csc.exe (.NET Framework 4.x).'
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host 'Compilando KinectHost (Face Tracking + head pose RGB)' -ForegroundColor Cyan

& $csc /nologo /target:exe /platform:x64 `
    "/out:$outDir\Simtrack.KinectHost.exe" `
    "/r:$kinectDll" `
    "/r:$faceDll" `
    "/r:$openCvDll" `
    "/r:$netStandard" `
    /r:System.Drawing.dll `
    $source `
    $rgbSource

if ($LASTEXITCODE -ne 0) {
    throw 'La compilacion ha fallado.'
}

Copy-Item $openCvDll $outDir -Force

# Dependencias de OpenCvSharp para .NET Framework
$extraDlls = @(
    (Join-Path $deps 'extracted\system.memory.4.6.3\lib\netstandard2.0\System.Memory.dll'),
    (Join-Path $deps 'extracted\system.buffers.4.6.0\lib\netstandard2.0\System.Buffers.dll'),
    (Join-Path $deps 'extracted\system.numerics.vectors.4.6.0\lib\netstandard2.0\System.Numerics.Vectors.dll'),
    (Join-Path $deps 'extracted\system.runtime.compilerservices.unsafe.6.1.2\lib\netstandard2.0\System.Runtime.CompilerServices.Unsafe.dll')
)

foreach ($extra in $extraDlls) {
    if (Test-Path $extra) {
        Copy-Item $extra $outDir -Force
    }
    else {
        Write-Host "Aviso: falta $extra" -ForegroundColor Yellow
    }
}

if (Test-Path $openCvNative) {
    Copy-Item $openCvNative $outDir -Force
    Write-Host 'Copiada OpenCvSharpExtern.dll'
}
else {
    Write-Host "Aviso: no se encuentra $openCvNative" -ForegroundColor Yellow
}

foreach ($extraModel in $haar, $facemark) {
    if (Test-Path $extraModel) {
        Copy-Item $extraModel $outDir -Force
        Write-Host ("Copiado " + (Split-Path $extraModel -Leaf))
    }
    else {
        Write-Host "Aviso: falta $extraModel" -ForegroundColor Yellow
    }
}

# DLLs nativas del Face Tracking.
foreach ($native in 'FaceTrackLib.dll', 'FaceTrackData.dll') {
    $origin = Join-Path $toolkitRedist $native

    if (Test-Path $origin) {
        Copy-Item $origin $outDir -Force
    }
}

Write-Host "Listo: $outDir\Simtrack.KinectHost.exe" -ForegroundColor Green
