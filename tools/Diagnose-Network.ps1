# Diagnóstico de red de Simtrack Bridge
#
# EJECUTAR COMO ADMINISTRADOR (pktmon lo necesita). Al terminar deja el
# resultado en "diagnostico.txt", en esta misma carpeta.
#
# Captura 30 segundos de tráfico y dice si llegan paquetes UDP a los puertos
# 4242/4243/4245 y desde qué IP. Sirve para distinguir:
#   - "llegan y la app no los ve"   -> firewall
#   - "no llega nada"               -> el iPhone envía a otra IP / otra red

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Continue'

$logPath = Join-Path $PSScriptRoot 'diagnostico.txt'

try {
    Start-Transcript -Path $logPath -Force | Out-Null
}
catch {
    Write-Host "No se pudo crear $logPath" -ForegroundColor Yellow
}

function Write-Section($text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

Write-Section '1. Red del PC'

Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notmatch '^127\.|^169\.254' } |
    Select-Object IPAddress, InterfaceAlias, PrefixLength | Format-Table -AutoSize

Get-NetConnectionProfile |
    Select-Object Name, InterfaceAlias, NetworkCategory | Format-Table -AutoSize

Write-Section '2. Firewall de Simtrack (permitir y bloquear)'

$fw = Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like '*simtrack*' }

if ($fw) {
    $fw | Select-Object DisplayName, Enabled, Direction, Action, Profile | Format-Table -AutoSize

    $blocked = $fw | Where-Object { $_.Action -eq 'Block' -and $_.Enabled -eq 'True' }

    if ($blocked) {
        Write-Host 'ATENCION: hay reglas de BLOQUEO para Simtrack. Ejecuta Allow-Firewall.ps1.' -ForegroundColor Red
    }
}
else {
    Write-Host 'NO hay reglas de firewall para Simtrack.' -ForegroundColor Yellow
    Write-Host 'Ejecuta Allow-Firewall.ps1 como administrador.' -ForegroundColor Yellow
}

Write-Section '3. Puertos UDP escuchando (4242/4243/4244/4245)'

Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalPort -in 4242, 4243, 4244, 4245 } |
    Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize

Write-Section '4. Prueba local del descubrimiento (127.0.0.1:4245)'

try {
    $probe = New-Object System.Net.Sockets.UdpClient
    $probe.Client.ReceiveTimeout = 2000
    $bytes = [System.Text.Encoding]::UTF8.GetBytes('{"protocol":"simtrack.bridge.v1","type":"bridge.discover"}')
    [void]$probe.Send($bytes, $bytes.Length, [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, 4245))
    $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
    $reply = $probe.Receive([ref]$remote)
    Write-Host ('Respuesta: ' + [System.Text.Encoding]::UTF8.GetString($reply)) -ForegroundColor Green
    $probe.Dispose()
}
catch {
    Write-Host "No hubo respuesta del puerto 4245: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'La app de Simtrack parece no estar en marcha (o el puerto esta ocupado).'
}

$temp = Join-Path $env:TEMP 'simtrack-diag'
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$etl = Join-Path $temp 'captura.etl'
$txt = Join-Path $temp 'captura.txt'

Remove-Item $etl, $txt -ErrorAction SilentlyContinue

Write-Section '5. Capturando 30 segundos de tráfico'
Write-Host 'AHORA: abre la app del iPhone en la pestaña Tracking y mueve la cabeza.'

try {
    pktmon filter remove 2>$null | Out-Null

    $filtersOk = $true
    foreach ($port in 4242, 4245) {
        pktmon filter add -p $port -t UDP 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { $filtersOk = $false }
    }

    if (-not $filtersOk) {
        Write-Host 'No se pudo filtrar por puerto; se captura todo el tráfico.' -ForegroundColor Yellow
        pktmon filter remove 2>$null | Out-Null
    }

    pktmon start --capture --comp nics --pkt-size 128 -f $etl | Out-Null

    for ($i = 30; $i -gt 0; $i--) {
        Write-Host -NoNewline ("`rQuedan {0,2} s..." -f $i)
        Start-Sleep -Seconds 1
    }

    Write-Host ''
    pktmon stop | Out-Null
    pktmon etl2txt $etl -o $txt | Out-Null

    Write-Section '6. Resultado de la captura'

    $lineas = Get-Content $txt -ErrorAction SilentlyContinue
    $u4242 = @($lineas | Select-String -Pattern 'UDP.*4242|4242.*UDP')
    $u4245 = @($lineas | Select-String -Pattern 'UDP.*4245|4245.*UDP')

    Write-Host "Paquetes UDP al 4242 (tracking): $($u4242.Count)"
    Write-Host "Paquetes UDP al 4245 (descubrimiento): $($u4245.Count)"

    if ($u4242.Count -gt 0) {
        Write-Host 'SI llegan paquetes de tracking. Mostrando 5:' -ForegroundColor Green
        $u4242 | Select-Object -First 5 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) }
        Write-Host 'El problema estaria en la app Windows (o en el firewall).' -ForegroundColor Yellow
    }
    else {
        Write-Host 'NO ha llegado ni un paquete de tracking al 4242.' -ForegroundColor Red
        Write-Host 'Posibles causas:' -ForegroundColor Yellow
        Write-Host '  - El iPhone esta en otra red (o compartiendo datos por cable).'
        Write-Host '  - La app del iPhone no ha podido descubrir este PC.'
        Write-Host '  - Una regla de BLOQUEO del firewall (mira el apartado 2).'
    }

    if ($u4245.Count -gt 0) {
        Write-Host ''
        Write-Host 'El iPhone SI esta preguntando por el PC (4245). El fallo esta en la respuesta.' -ForegroundColor Yellow
    }
}
finally {
    pktmon stop 2>$null | Out-Null
    pktmon filter remove 2>$null | Out-Null
    try { Stop-Transcript | Out-Null } catch { }
}

Write-Host ''
Write-Host "Diagnostico guardado en: $logPath" -ForegroundColor Green
Write-Host 'Copia ese archivo o pegalo en el chat.'
