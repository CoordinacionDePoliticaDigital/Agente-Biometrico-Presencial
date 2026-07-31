using System.Collections.Generic;

namespace AgenteBiometricoPresencial.Models
{
    // ─── MENSAJES AGENTE → FRONTEND ────────────────────────────────────────────

    /// <summary>Enviado al conectar un cliente WebSocket.</summary>
    public class ConnectedHandshakeMsg
    {
        public string event_type { get; set; } = "CONNECTED_HANDSHAKE";
        public string status { get; set; } = "READY";
        public string agentVersion { get; set; } = "3.0.0";
        public bool simulationMode { get; set; }
        public DeviceStatusPayload devices { get; set; } = new DeviceStatusPayload();
    }

    /// <summary>Estado actual de todos los periféricos.</summary>
    public class DeviceStatusUpdateMsg
    {
        public string event_type { get; set; } = "DEVICE_STATUS_UPDATE";
        public DeviceStatusPayload devices { get; set; } = new DeviceStatusPayload();
    }

    public class DeviceStatusPayload
    {
        public DeviceStatusItem realScanG10 { get; set; } = new DeviceStatusItem();
        public DeviceStatusItem realPassRPNF { get; set; } = new DeviceStatusItem();
    }

    public class DeviceStatusItem
    {
        public bool isConnected { get; set; }
        public bool isSimulated { get; set; }
        public bool isBusy { get; set; }
        public string statusCode { get; set; } = "UNKNOWN";
        public string statusMessage { get; set; } = "";
        public string firmwareVersion { get; set; }
        public string serialNumber { get; set; }
        public bool driverFound { get; set; }
        public string lastCheckedAt { get; set; }
    }

    /// <summary>Huella capturada con éxito.</summary>
    public class FingerprintCapturedMsg
    {
        public string event_type { get; set; } = "FINGERPRINT_CAPTURED";
        public string sessionId { get; set; } = "";
        public string status { get; set; } = "SUCCESS";
        public FingerprintData data { get; set; }
    }

    public class FingerprintData
    {
        public string fingerGroup { get; set; } = "";  // SLAP_4_LEFT | SLAP_4_RIGHT | THUMBS_2
        public int nfiqQuality { get; set; }           // 1 (excelente) a 5 (pobre)
        public string wsqBase64 { get; set; } = "";
        public string isoTemplateBase64 { get; set; } = "";
        public int imageWidth { get; set; }
        public int imageHeight { get; set; }
        public List<int> capturedFingers { get; set; } = new List<int>(); // IDs de dedos capturados
        public List<int> skippedFingers { get; set; } = new List<int>();  // IDs de dedos omitidos (amputados)
    }

    /// <summary>Frame en vivo durante captura dactilar.</summary>
    public class FingerprintProgressMsg
    {
        public string event_type { get; set; } = "FINGERPRINT_PROGRESS";
        public string sessionId { get; set; } = "";
        public int qualityScore { get; set; }  // 0–100
        public bool fingerDetected { get; set; }
        public string previewBase64 { get; set; }
    }

    /// <summary>Documento escaneado por RealPass RPNF.</summary>
    public class DocumentScannedMsg
    {
        public string event_type { get; set; } = "DOCUMENT_SCANNED";
        public string sessionId { get; set; } = "";
        public string status { get; set; } = "SUCCESS";
        public MrzData mrz { get; set; }
        public DocumentImages images { get; set; }
    }

    public class MrzData
    {
        public string documentType { get; set; } = "";
        public string country { get; set; } = "";
        public string surname { get; set; } = "";
        public string givenNames { get; set; } = "";
        public string documentNumber { get; set; } = "";
        public string curp { get; set; }
        public string dateOfBirth { get; set; } = "";
        public string sex { get; set; } = "";
        public string expiryDate { get; set; } = "";
        public bool isExpired { get; set; }
        public bool rfidRead { get; set; }
    }

    public class DocumentImages
    {
        public string whiteLightBase64 { get; set; }
        public string infraredBase64 { get; set; }
        public string ultravioletBase64 { get; set; }
    }

    /// <summary>Error en captura o comando no reconocido.</summary>
    public class CaptureErrorMsg
    {
        public string event_type { get; set; } = "CAPTURE_ERROR";
        public string sessionId { get; set; } = "";
        public string errorCode { get; set; } = "UNKNOWN_ERROR";
        public string message { get; set; } = "";
    }

    /// <summary>Heartbeat periódico para mantener viva la conexión.</summary>
    public class AgentHeartbeatMsg
    {
        public string event_type { get; set; } = "AGENT_HEARTBEAT";
        public string agentVersion { get; set; } = "3.0.0";
        public DeviceStatusPayload devices { get; set; } = new DeviceStatusPayload();
        public string timestamp { get; set; } = "";
        public bool simulationMode { get; set; }
    }

    // ─── MENSAJES FRONTEND → AGENTE ────────────────────────────────────────────

    public class IncomingCommand
    {
        public string command { get; set; } = "";
        public string sessionId { get; set; }
        public string fingerGroup { get; set; }        // Para START_FINGERPRINT_CAPTURE
        public List<int> skipFingers { get; set; }     // Dedos a omitir (amputados)
        public int timeoutSeconds { get; set; } = 30;
        public bool readRfid { get; set; } = true;
        public string spectralMode { get; set; }       // VIS | IR | UV

        // Para START_FULL_BIOMETRIC_CAPTURE
        public string documentType { get; set; } // "PASAPORTE" | "INE"
        public string encryptionKeyBase64 { get; set; }
        public string mobileLivenessUrl { get; set; }
    }

    public class FullBiometricResultMsg
    {
        public string event_type { get; set; } = "FULL_BIOMETRIC_CAPTURED";
        public string sessionId { get; set; } = "";
        public string status { get; set; } = "SUCCESS";
        // JSON cifrado con AES (contiene los datos reales)
        public string encryptedPayloadBase64 { get; set; } = "";
        public string ivBase64 { get; set; } = "";
    }
}
