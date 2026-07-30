# Agente Biométrico Presencial v2.0
## Middleware WebSocket C# .NET 8 — Autoridad Certificadora (Gobierno del Estado de Chihuahua)

Servicio de fondo local (Windows, .NET 8) que actúa como puente entre el frontend Svelte y los SDKs/DLLs nativos de Xperix instalados en la PC del Agente Enrolador.

---

## 🏗️ Arquitectura

```
[Browser Svelte Frontend]  ←──── ws://127.0.0.1:8443 ────→  [AgenteBiometricoPresencial.exe]
                                                                   ├─ DeviceStatusMonitor.cs  (WMI/USB polling cada 5s)
                                                                   ├─ Drivers/RealScanDriver.cs  → P/Invoke RS_SDK.dll
                                                                   └─ Drivers/RealPassDriver.cs → Xperix.RealPassSDK.dll
```

---

## 🔌 Protocolo WebSocket JSON

### Comandos (Frontend → Agente)

| `command` | Parámetros | Descripción |
|---|---|---|
| `GET_DEVICE_STATUS` | — | Solicita estado de todos los periféricos |
| `START_FINGERPRINT_CAPTURE` | `fingerGroup`, `skipFingers`, `timeoutSeconds` | Captura slap 4+4+2 |
| `START_DOCUMENT_SCAN` | `spectralMode`, `readRfid`, `timeoutSeconds` | Escaneo de documento |
| `ABORT_CAPTURE` | `sessionId` | Aborta captura activa |

### Eventos (Agente → Frontend)

| `event_type` | Cuándo |
|---|---|
| `CONNECTED_HANDSHAKE` | Al conectar. Incluye versión y estado inicial de periféricos |
| `DEVICE_STATUS_UPDATE` | Cambio de estado en cualquier periférico |
| `AGENT_HEARTBEAT` | Cada 5s — mantiene viva la conexión |
| `FINGERPRINT_CAPTURED` | Huella slap capturada (WSQ Base64 + NFIQ) |
| `DOCUMENT_SCANNED` | MRZ + imágenes multiespectrales |
| `CAPTURE_ERROR` | Error o timeout durante captura |
| `CAPTURE_ABORTED` | Captura abortada por comando del cliente |

---

## ⚠️ Política de Simulación

**NO hay simulación por defecto.** Si los dispositivos no están presentes el agente reporta el estado real:

| Condición | `statusCode` | `isConnected` |
|---|---|---|
| SDK no instalado | `DRIVER_MISSING` | `false` |
| SDK presente, device desconectado | `DISCONNECTED` | `false` |
| Error de SDK | `ERROR` | `false` |
| Device presente y listo | `READY` | `true` |

El modo simulación **solo** se activa con el flag `--simulate`:
```powershell
dotnet run --project Src/AgenteBiometrico.csproj -- --simulate
```

---

## 🛠️ Requisitos del Sistema

- **OS**: Windows 10/11 x64
- **Runtime**: .NET 8.0 (SDK para compilar, Runtime para ejecutar)
- **SDKs Xperix**: 
  - `C:\Program Files\Xperix\RealScanSDK\Bin\x64\RS_SDK.dll`
  - `C:\Program Files\Xperix\RealPassSDK\Bin\x64\Xperix.RealPassSDK.dll`

---

## 🚀 Compilación y Ejecución

```powershell
# Instalar .NET 8 SDK si no está instalado (requiere administrador)
winget install Microsoft.DotNet.SDK.8

# Compilar
dotnet build Src/AgenteBiometrico.csproj -c Release

# Ejecutar (producción — hardware real requerido)
dotnet run --project Src/AgenteBiometrico.csproj

# Ejecutar con simulación (pruebas de integración sin hardware)
dotnet run --project Src/AgenteBiometrico.csproj -- --simulate

# Puerto alternativo
dotnet run --project Src/AgenteBiometrico.csproj -- --port=9443
```

---

## 📋 Diagnóstico al Arranque

Al iniciar, el agente imprime en consola:

```
╔══════════════════════════════════════════════════════════════╗
║      AGENTE BIOMÉTRICO PRESENCIAL  v2.0.0                    ║
║      Middleware WebSocket — Autoridad Certificadora           ║
║      RealScan G10  •  RealPass RPNF  •  Puerto 8443          ║
╚══════════════════════════════════════════════════════════════╝

[USB Diagnóstico] Enumerando dispositivos USB Xperix...
  [✓] RealScan G10 (VID: VID_16D1): Detectado en USB
  [✗] RealPass RPNF (VID: VID_0525): No detectado

[Drivers] Inicializando controladores de hardware...
  ✓ RealScan G10: RealScan G10 inicializado. Dispositivos detectados: 1
  ✗ RealPass RPNF: No se encontró Xperix.RealPassSDK.dll en C:\Program Files\Xperix\...

[INFO] WebSocket escuchando en ws://127.0.0.1:8443
[INFO] Presiona CTRL+C para detener.
```
