# Diagnóstico de red de Simtrack Bridge
#
# EJECUTAR COMO ADMINISTRADOR (pktmon lo necesita):
#   Click derecho en PowerShell -> "Ejecutar como administrador"
#   cd C:\Users\<usuario>\source\repos\Simtrack.Bridge
#   powershell -ExecutionPolicy Bypass -File .\tools\Diagnose-Network.ps1
#
# Captura 30 segundos de tráfico y dice si llegan paquetes UDP al puerto 4242
# y desde qué IP. Sirve para distinguir:
#   - "llegan y la app no los ve"  -> problema de la app/firewall
#   - "no llega nada"              -> el iPhone envía a otra IP o no envía

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Continue'

Write-Host ''
Write-Host '=== 1. Red del PC ===' -ForegroundColor Cyan
Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notmatch '^127\.|^169\.254' } |
    Select-Object IPAddress, InterfaceAlias, PrefixLength | Format-Table -AutoSize

Get-NetConnectionProfile |
    Select-Object Name, InterfaceAlias, NetworkCategory | Format-Table -AutoSize

Write-Host '=== 2. Reglas de firewall de Simtrack ===' -ForegroundColor Cyan
$rules = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'simtrack' }

if ($rules) {
    $rules | Select-Object DisplayName, Enabled, Direction, Action, Profile | Format-Table -AutoSize
}
else {
    Write-Host 'NO hay reglas de firewall para Simtrack.' -ForegroundColor Yellow
    Write-Host 'Ejecuta tools\Allow-Firewall.ps1 como administrador.' -ForegroundColor Yellow
}

Write-Host '=== 3. Puertos UDP 4242/4243/4244/4245 ===' -ForegroundColor Cyan
Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalPort -in 4242, 4243, 4244, 4245 } |
    Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize

# ---------------------------------------------------------------------------
$temp = Join-Path $env:TEMP "simtrack-diag"
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$etl = Join-Path $temp 'captura.etl'
$txt = Join-Path $temp 'captura.txt'

Remove-Item $etl, $txt -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '=== 4. Capturando 30 segundos de tráfico ===' -ForegroundColor Cyan
Write-Host 'AHORA: abre la app del iPhone en la pestaña Tracking y mueve la cabeza.'
Write-Host ''

try {
    pktmon filter remove | Out-Null
    pktmon filter add -p 4242 -t UDP | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Aviso: no se pudo filtrar solo el 4242; se captura todo el tráfico.' -ForegroundColor Yellow
    }

    pktmon start --capture --comp nics --pkt-size 128 -f $etl | Out-Null

    for ($i = 30; $i -gt 0; $i--) {
        Write-Host -NoNewline ("`rQuedan {0,2} s..." -f $i)
        Start-Sleep -Seconds 1
    }

    Write-Host ''
    pktmon stop | Out-Null
    pktmon etl2txt $etl -o $txt | Out-Null

    Write-Host '=== 5. Resultado ===' -ForegroundColor Cyan

    $lineas = Get-Content $txt -ErrorAction SilentlyContinue
    $udp4242 = $lineas | Select-String -Pattern 'UDP.*4242|4242.*UDP'

    if ($udp4242) {
        Write-Host "Sí llegan paquetes al puerto 4242 (mostrando 10):" -ForegroundColor Green
        $udp4242 | Select-Object -First 10 | ForEach-Object { $_.Line.Trim() }
        Write-Host ''
        Write-Host 'Entonces el problema está en la app Windows o en el firewall.' -ForegroundColor Yellow
    }
    else {
        Write-Host 'NO ha llegado ni un paquete UDP al puerto 4242.' -ForegroundColor Red
        Write-Host ''
        Write-Host 'Conclusiones posibles:' -ForegroundColor Yellow
        Write-Host '  - La app del iPhone es una versión antigua y envía a otra IP'
        Write-Host '    (reinstala el .ipa más reciente de GitHub Actions).'
        Write-Host '  - O el iPhone no está enviando (pestaña Tracking cerrada).'
    }

    Write-Host ''
    Write-Host "Captura completa: $txt"
}
finally {
    pktmon stop 2>$null | Out-Null
    pktmon filter remove 2>$null | Out-Null
}
