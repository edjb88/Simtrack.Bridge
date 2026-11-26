# Simtrack Bridge (Windows)

Puente de tracking 6DOF para simracing. Recibe la pose del iPhone por UDP y la
escribe en la memoria compartida de **FreeTrack 2.0**, que es lo que leen los
simuladores con soporte nativo de TrackIR (Assetto Corsa EVO, Assetto Corsa,
iRacing, etc.).

```
iPhone X (ARKit / TrueDepth)  --UDP 4242 (6 double: cm + grados)-->  Simtrack.App
iPhone X                      --UDP 4243 (JSON control/recentrado)-->  Simtrack.App
iPhone X                      <-UDP 4244 (JSON telemetría)---------  Simtrack.App
Simtrack.App  --250 Hz-->  FT_SharedMem (108 bytes)  -->  Assetto Corsa EVO
```

## Estado

- [x] Receptor UDP 4242 (pose) y 4243 (control).
- [x] Conversión grados → radianes y centímetros → centímetros.
- [x] Escritor FreeTrack 2.0: `FT_SharedMem` de 108 bytes, `DataID = 1`,
      mutex `FT_Mutext`, escritura continua (~250 Hz de objetivo, ~220 Hz
      reales con resolución de temporizador de 1 ms).
- [x] Telemetría de vuelta al iPhone por 4244.
- [x] Recentrado desde el botón de la app y desde el iPhone.
- [ ] Integración del Sony Head Tracker (IMU) y filtro complementario.
- [ ] Malla facial / diagnóstico avanzado en la app iOS.

## Arrancar

**Opción A — Visual Studio 2026**
1. Abrir `Simtrack.Bridge.slnx`.
2. Fijar `Simtrack.App` como proyecto de inicio.
3. F5.

**Opción B — línea de comandos**

```powershell
dotnet run --project .\Simtrack.App
```

La app arranca sola el motor de salida al abrirse (botón `DETENER` para parar).
En el log se ve todo lo que va pasando.

## Firewall (IMPORTANTE)

En este PC la red Wi-Fi está marcada como **Pública**, así que Windows bloquea
la entrada de paquetes UDP por defecto. Hay que abrir los puertos 4242 y 4243.

Ejecutar en una PowerShell **como administrador**:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Allow-Firewall.ps1
```

Alternativa manual (PowerShell administrador):

```powershell
New-NetFirewallRule -DisplayName "Simtrack UDP 4242" -Direction Inbound `
  -Protocol UDP -LocalPort 4242 -Action Allow -Profile Any
New-NetFirewallRule -DisplayName "Simtrack UDP 4243" -Direction Inbound `
  -Protocol UDP -LocalPort 4243 -Action Allow -Profile Any
```

También puede aparecer el aviso de Windows *"Permitir que esta aplicación se
comunique en redes privadas"* al arrancar la app: hay que aceptarlo **y marcar
las redes privadas**.

## Dirección IP del iPhone

La IP del PC está en `SimtrackNetwork.pcIP`, dentro del repo
[edjb88/Simtrack](https://github.com/edjb88/Simtrack) (`Simtrack/ContentView.swift`).
Ahora mismo vale `192.168.1.37`. Si el PC cambia de red:

1. `ipconfig` en el PC → IPv4 del adaptador Wi-Fi.
2. Actualizar `pcIP` en la app iOS y recompilar (GitHub Actions).
3. Reinstalar el `.ipa` con Sideloadly.

El iPhone y el PC tienen que estar en la **misma red** (mismo router).

## Probar sin simulador

1. Arrancar la app.
2. Abrir la pestaña Tracking del iPhone (la app iOS envía mientras tiene la
   cámara activa; iOS no deja usar ARKit en segundo plano).
3. En la app Windows debe cambiar:
   - `ESPERANDO IPHONE` → `IPHONE CONECTADO` (punto verde),
   - el contador de paquetes sube,
   - las cifras de pose se mueven al mover la cabeza,
   - `SALIDA FREETRACK 2.0` muestra `Escribiendo en FT_SharedMem` y ~250 Hz.

Si el contador no sube, es red o firewall, no FreeTrack:
comprobar IP, que los dos estén en la misma red y las reglas del firewall.

## Assetto Corsa EVO

1. Arrancar **Simtrack Bridge** antes que el juego.
2. En AC EVO, activar el head tracking / TrackIR en opciones.
3. Si la cámara se mueve al revés, ajustar las inversiones en
   `Simtrack.Core/Tracking/PoseFusion.cs` (`PoseSettings`).

## Ajustar ejes y suavizado sin recompilar

Junto al `.exe` se genera `simtrack.settings.json`
(`Simtrack.App/bin/Debug/net10.0-windows/simtrack.settings.json`):

```json
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
```

- Cada `Invert...` cambia el signo de ese eje.
- `Smoothing` alto = más suave pero con algo más de retardo; bajo = más
  directo y nervioso. Valores entre 0.05 y 0.4 tienen sentido.
- Guardar el archivo y reiniciar la app para aplicar los cambios.

## Estructura

| Proyecto | Contenido |
| --- | --- |
| `Simtrack.App` | Ventana WPF, `BridgeService` (orquesta todo) |
| `Simtrack.Communication` | `IphoneReceiver` (4242/4243), `TelemetrySender` (4244) |
| `Simtrack.Core` | Modelos y `PoseFusion` (recentrado, suavizado, grados→radianes) |
| `Simtrack.Output` | `FTTypes` y `FreeTrackWriter` (memoria compartida 108 B) |
| `Simtrack.Bluetooth` | Reservado para la integración Sony |

## Formato del paquete de tracking (4242)

48 bytes = 6 `double` little-endian, en este orden:

| Índice | Campo | Unidades |
| --- | --- | --- |
| 0 | X | cm |
| 1 | Y | cm |
| 2 | Z | cm |
| 3 | Yaw | **grados** |
| 4 | Pitch | **grados** |
| 5 | Roll | **grados** |

La app iOS ya manda valores relativos a su referencia de recentrado.
