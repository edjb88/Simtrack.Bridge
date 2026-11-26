# Reglas de firewall para Simtrack Bridge (UDP 4242 / 4243 / 4245)
#
# EJECUTAR COMO ADMINISTRADOR:
#   Doble clic en Permitir-Firewall.cmd, o:
#   Click derecho en PowerShell -> "Ejecutar como administrador"
#   cd C:\ruta\donde\esta\Simtrack.App.exe
#   powershell -ExecutionPolicy Bypass -File .\Allow-Firewall.ps1
#
# Ademas de abrir los puertos, borra las reglas de BLOQUEO que Windows crea
# cuando en el aviso de firewall se pulsa "Cancelar". Una regla de bloqueo
# tiene prioridad sobre cualquier permiso y es la causa tipica de que la app
# escuche pero no le llegue nada.

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

Write-Host '=== 1. Borrando reglas de BLOQUEO de Simtrack.App.exe ===' -ForegroundColor Cyan

$removed = 0

foreach ($rule in (Get-NetFirewallRule -Action Block -ErrorAction SilentlyContinue)) {
    $filter = $rule | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue

    if ($filter -and $filter.Program -and ($filter.Program -like '*\Simtrack.App.exe')) {
        Write-Host "  Eliminando: $($rule.DisplayName) [$($rule.Profile)]"
        Remove-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
        $removed++
    }
}

if ($removed -eq 0) {
    Write-Host '  No habia reglas de bloqueo.'
}

Write-Host '=== 2. Permiso por programa (el .exe de esta carpeta) ===' -ForegroundColor Cyan

$exe = Join-Path $PSScriptRoot 'Simtrack.App.exe'

if (Test-Path $exe) {
    foreach ($protocol in @('UDP', 'TCP')) {
        $name = "Simtrack Bridge - programa $protocol"

        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue

        New-NetFirewallRule `
            -DisplayName $name `
            -Direction Inbound `
            -Action Allow `
            -Program $exe `
            -Protocol $protocol `
            -Profile Any `
            -Description 'Simtrack Bridge (por programa)' | Out-Null

        Write-Host "  Permitido $protocol para $exe"
    }
}
else {
    Write-Host '  Aviso: no se encuentra Simtrack.App.exe en esta carpeta.' -ForegroundColor Yellow
}

Write-Host '=== 3. Permiso por puerto ===' -ForegroundColor Cyan

$rules = @(
    @{ Name = 'Simtrack Bridge - UDP 4242 (tracking)'; Port = 4242 },
    @{ Name = 'Simtrack Bridge - UDP 4243 (control)';  Port = 4243 },
    @{ Name = 'Simtrack Bridge - UDP 4245 (descubrimiento)'; Port = 4245 }
)

foreach ($rule in $rules) {
    Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue

    New-NetFirewallRule `
        -DisplayName $rule.Name `
        -Direction Inbound `
        -Action Allow `
        -Protocol UDP `
        -LocalPort $rule.Port `
        -Profile Any `
        -Description 'Permite el tracking UDP de Simtrack desde el iPhone' | Out-Null

    Write-Host "  Permitido UDP $($rule.Port)"
}

Write-Host ''
Write-Host 'Listo. Reglas de Simtrack:' -ForegroundColor Green
Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'Simtrack Bridge*' } |
    Select-Object DisplayName, Enabled, Direction, Action, Profile |
    Format-Table -AutoSize

Write-Host 'Perfiles de red del PC:' -ForegroundColor Green
Get-NetConnectionProfile | Select-Object Name, InterfaceAlias, NetworkCategory | Format-Table -AutoSize

Write-Host 'Si el iPhone sigue sin conectar, ejecuta Diagnostico-Red.ps1 y revisa el .txt.' -ForegroundColor Yellow
