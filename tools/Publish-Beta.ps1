# Crea el paquete "beta" listo para copiar a otro PC.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\Publish-Beta.ps1
#
# Genera en dist\ :
#   Simtrack.Bridge-0.9.0-beta-win-x64\        (carpeta lista para ejecutar)
#   Simtrack.Bridge-0.9.0-beta-win-x64.zip     (lo que se copia al otro PC)

param(
    [string]$Version = '0.9.0-beta',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$name = "Simtrack.Bridge-$Version-$Runtime"
$target = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Limpiando $target" -ForegroundColor Cyan
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $target | Out-Null

Write-Host 'Compilando (self-contained, no hace falta instalar .NET en el otro PC)' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'Simtrack.App\Simtrack.App.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $target

Write-Host 'Copiando ajustes, scripts y documentacion' -ForegroundColor Cyan

Copy-Item (Join-Path $root 'tools\Allow-Firewall.ps1') (Join-Path $target 'Allow-Firewall.ps1') -Force
Copy-Item (Join-Path $root 'tools\Diagnose-Network.ps1') (Join-Path $target 'Diagnostico-Red.ps1') -Force

# Ajustes por defecto junto al .exe
$settings = @'
{
  "InvertX": true,
  "InvertY": false,
  "InvertZ": false,
  "InvertYaw": false,
  "InvertPitch": false,
  "InvertRoll": false,
  "RotationSmoothing": 0.15,
  "PositionSmoothing": 0.25
}
'@
Set-Content -Path (Join-Path $target 'simtrack.settings.json') -Value $settings -Encoding UTF8

# Lanzador del firewall con permisos de administrador (doble clic)
$cmd = @'
@echo off
powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0Allow-Firewall.ps1\"'"
pause
'@
Set-Content -Path (Join-Path $target 'Permitir-Firewall.cmd') -Value $cmd -Encoding ASCII

$leeme = @"
SIMTRACK BRIDGE $Version  ($Runtime)
========================================

Que es
  Puente de head tracking 6DOF: recibe la pose del iPhone por UDP y la
  escribe en la memoria compartida de FreeTrack 2.0, que es lo que leen
  Assetto Corsa EVO y otros simuladores con soporte TrackIR.

Puesta en marcha en este PC (5 minutos)
  1. Copia esta carpeta al PC que quieras (no hace falta instalar .NET).
  2. Doble clic en Permitir-Firewall.cmd  -> aceptar el aviso de administrador.
     (Sin esto Windows bloquea los paquetes del iPhone.)
  3. Ejecuta Simtrack.App.exe. Si Windows SmartScreen avisa de que es una
     aplicacion desconocida: "Mas informacion" -> "Ejecutar de todas formas".
  4. Abre la app del iPhone en la pestaña Tracking.
     La app se anuncia sola por la red (UDP 4245): no hay que escribir IPs.
     Deberia pasar a IPHONE CONECTADO en unos segundos.
  5. Arranca el simulador DESPUES de esta app.

Requisitos de red
  - El iPhone y este PC tienen que estar en la misma red (mismo router) o el
    iPhone compartiendo datos por el cable USB.
  - No hace falta IP fija: el PC dice su IP a la app del iPhone cada vez que
    esta arranca o vuelve a primer plano.

Dashboard de SimHub (opcional)
  - Si SimHub esta instalado y en marcha en este PC, la pestaña Dashboard del
    iPhone carga sola contra la IP correcta (se detecta el puerto).

Si algo no funciona
  1. Ejecuta Diagnostico-Red.ps1 como administrador (captura 30 s y dice si
     llegan paquetes UDP al 4242 y desde que IP).
  2. Revisa el registro de la ventana: dice la IP del iPhone, los paquetes
     recibidos y el estado de FT_SharedMem.
  3. Ajuste de ejes: edita simtrack.settings.json y reinicia la app.
     InvertX/InvertY/InvertZ/InvertYaw/InvertPitch/InvertRoll a true o false.

Que incluye esta beta
  - Receptor UDP 4242 (pose) y 4243 (ordenes de recentrado).
  - Salida FreeTrack 2.0: FT_SharedMem de 108 bytes, DataID 1, ~250 Hz.
  - Descubrimiento automatico del PC por UDP 4245.
  - Telemetria al iPhone por 4244.
  - Aun sin integrar: Sony Head Tracker (IMU) y filtro complementario.
"@
Set-Content -Path (Join-Path $target 'LEEME.txt') -Value $leeme -Encoding UTF8

Write-Host 'Comprimiendo paquete' -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "Listo: $zip  ($sizeMb MB)" -ForegroundColor Green
Get-ChildItem $target | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
