# Agente Biométrico Presencial (Middleware WebSocket C# / .NET)

Servicio de fondo local (Windows Service / Daemon .NET) para la estación del Agente Enrolador de la **Autoridad Certificadora (Gobierno del Estado de Chihuahua)**.

Este middleware actúa como puente entre el cliente web Frontend (Svelte en Tier 1) y los SDKs/DLLs C++ nativos instalados en la PC bajo `C:\Program Files\Xperix`.

---

## 🏗️ Arquitectura del Servicio

```
+------------------------+                        +----------------------------------+
|  Svelte Web Frontend   |                        |  Agente Biométrico Local (C#)    |
|   (Tier 1 - Browser)   |                        |   WebSocket Server (Port 8443)   |
+------------------------+                        +----------------------------------+
            |                                                      |
            |------------ Connect (wss://127.0.0.1:8443) --------->|
            |<----------- Handshake & Status OK -------------------|
            |                                                      |
            |------------ JSON: START_FINGERPRINT ---------------->|---- P/Invoke RS_SDK.dll
            |<----------- Event: FINGERPRINT_CAPTURED -------------|     (RealScan G10)
            |                                                      |
            |------------ JSON: START_DOCUMENT_SCAN -------------->|---- Xperix.RealPassSDK.dll
            |<----------- Event: DOCUMENT_SCANNED -----------------|     (RealPass RPNF)
```

---

## 🔌 Dispositivos Biométricos Soportados

1. **Xperix / Suprema RealScan G10**:
   - Escáner dactilar de 10 huellas (Slap scanner 4+4+2).
   - Generación de imágenes y plantillas ISO/IEC 19794-2 y compresión WSQ con calidad NFIQ (1-5).
   - DLL: `C:\Program Files\Xperix\RealScanSDK\Bin\x64\RS_SDK.dll`

2. **Xperix RealPass RPNF**:
   - Lector de documentos de identidad y pasaportes electrónicos.
   - Extracción de texto MRZ (ICAO 9303), lectura de chip RFID NFC e imágenes (Luz Blanca, Infrarroja, Ultravioleta).
   - DLL: `C:\Program Files\Xperix\RealPassSDK\Bin\x64\Xperix.RealPassSDK.dll`

---

## 🛠️ Requisitos del Sistema
- **Sistema Operativo**: Windows 10/11 x64
- **Entorno de Ejecución**: .NET 8.0 SDK / Runtime
- **SDKs de Fabricante**: Xperix RealScan SDK v2.0+ y RealPass SDK v3.2+ en `C:\Program Files\Xperix\`

---

## Estado de la integración

- **RealScan G10**: captura 4+4+2, segmentación y etiquetado por dedo,
  dedos faltantes, calidad NFIQ individual, LFD, WSQ de plancha/dedo y
  plantilla ISO/IEC 19794-2 implementados sobre el binding nativo x64.
- **RealPass RPNF**: detección y lectura asíncrona, MRZ parseada, códigos de
  barras, imágenes WH/IR/UV/OCR/retrato y resultados de seguridad ePassport
  implementados sobre el ensamblado oficial.
- **Plantilla ISO 19794-2**: se genera por dedo mediante `RS_GetTemplate`. Si el
  SDK o la licencia instalada no soportan el extractor, la captura falla de
  forma explícita; no se generan datos ficticios.

## Compilación y ejecución

```bash
# Compilar proyecto en modo Release x64
dotnet build Src/AgenteBiometrico.csproj -c Release -r win-x64

# Ejecutar el servicio interactivo
dotnet run --project Src/AgenteBiometrico.csproj

# Diagnosticar SDK y conexión física sin abrir el WebSocket
dotnet run --project Src/AgenteBiometrico.csproj -- --diagnose

# Captura física de prueba; sólo imprime métricas, no guarda biométricos
dotnet run --project Src/AgenteBiometrico.csproj -- --capture SLAP_4_LEFT

# Lectura documental de prueba sin imprimir ni guardar datos personales
dotnet run --project Src/AgenteBiometrico.csproj -- --scan-document
```

Por omisión el agente escucha únicamente en `ws://127.0.0.1:8443`. Para el
modo requerido por el frontend HTTPS, configura un certificado PFX confiable
por el navegador:

```powershell
$env:BIOMETRIC_AGENT_CERT_PATH = 'C:\ruta\agente-local.pfx'
$env:BIOMETRIC_AGENT_CERT_PASSWORD = 'contraseña-del-pfx'
$env:BIOMETRIC_AGENT_PORT = '8443'
dotnet run --project Src/AgenteBiometrico.csproj
```

Con certificado configurado el endpoint cambia automáticamente a
`wss://127.0.0.1:8443`.

## Contrato WebSocket inicial

Comandos habilitados:

- `GET_DEVICE_STATUS`
- `START_FINGERPRINT_CAPTURE`
- `START_DOCUMENT_SCAN`

Ejemplo de captura:

```json
{
  "command": "START_FINGERPRINT_CAPTURE",
  "sessionId": "SESS-987654",
  "fingerType": "SLAP_4_LEFT",
  "missingFingers": ["LEFT_RING"],
  "timeoutSeconds": 30
}
```

`fingerType` acepta `SLAP_4_LEFT`, `SLAP_4_RIGHT` y `THUMBS_2`. El agente
emite primero `FINGERPRINT_CAPTURE_STARTED` y después
`FINGERPRINT_CAPTURED`, o `ERROR` con código estable y, cuando corresponde,
el código nativo del fabricante. `missingFingers` es opcional y sólo puede
contener posiciones de la plancha solicitada. El resultado contiene el WSQ de
la plancha y un arreglo `samples` con posición ISO, NFIQ, LFD, WSQ y plantilla
ISO/IEC 19794-2 de cada dedo.

Ejemplo de lectura documental:

```json
{
  "command": "START_DOCUMENT_SCAN",
  "sessionId": "SESS-987654",
  "readRfid": true,
  "timeoutSeconds": 60
}
```

El evento `DOCUMENT_SCANNED` contiene tipo detectado, líneas y campos MRZ,
imágenes PNG, códigos de barras y resultados BAC/PACE/AA/CA/PA/DG cuando se
trata de un documento electrónico. La clasificación OCR específica de
INE/IFE/INM se incorporará como una capa posterior sobre estas imágenes.
