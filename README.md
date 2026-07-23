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

## 🚀 Compilación y Ejecución

```bash
# Compilar proyecto en modo Release x64
dotnet build -c Release -r win-x64

# Ejecutar el servicio interactivo
dotnet run --project Src/AgenteBiometrico.csproj
```
