# Reglas de firewall para Simtrack Bridge (UDP 4242 / 4243)
#
# EJECUTAR COMO ADMINISTRADOR:
#   Click derecho en PowerShell -> "Ejecutar como administrador"
#   cd C:\Users\<usuario>\source\repos\Simtrack.Bridge
#   powershell -ExecutionPolicy Bypass -File .\tools\Allow-Firewall.ps1
#
# Sin estas reglas Windows bloquea la entrada de los paquetes que envía el
# iPhone (la red Wi-Fi del PC está marcada como "Pública" y Windows bloquea
# la entrada por defecto).

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$rules = @(
    @{ Name = 'Simtrack Bridge - UDP 4242 (tracking)'; Port = 4242 },
    @{ Name = 'Simtrack Bridge - UDP 4243 (control)';  Port = 4243 },
    @{ Name = 'Simtrack Bridge - UDP 4245 (descubrimiento)'; Port = 4245 }
)

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue

    if ($existing) {
        Write-Host "Actualizando regla existente: $($rule.Name)"
        Remove-NetFirewallRule -DisplayName $rule.Name
    }
    else {
        Write-Host "Creando regla: $($rule.Name)"
    }

    New-NetFirewallRule `
        -DisplayName $rule.Name `
        -Direction Inbound `
        -Action Allow `
        -Protocol UDP `
        -LocalPort $rule.Port `
        -Profile Any `
        -Description 'Permite el tracking UDP de Simtrack desde el iPhone' | Out-Null
}

Write-Host ''
Write-Host 'Listo. Reglas activas:' -ForegroundColor Green
Get-NetFirewallRule -DisplayName 'Simtrack Bridge*' |
    Select-Object DisplayName, Enabled, Direction, Action, Profile |
    Format-Table -AutoSize
