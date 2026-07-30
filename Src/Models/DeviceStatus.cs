using System;

namespace AgenteBiometricoPresencial.Models
{
    /// <summary>
    /// Estado en tiempo real de un periférico biométrico.
    /// isSimulated se activa ÚNICAMENTE cuando el flag --simulate está en línea de comandos.
    /// Si el dispositivo no está presente sin ese flag, se reporta el estado real: DISCONNECTED/ERROR.
    /// </summary>
    public class DeviceStatus
    {
        public string DeviceName { get; set; } = "";
        public string DeviceId { get; set; } = "";   // ej. "REALSCAN_G10" | "REALPASS_RPNF"
        public bool IsConnected { get; set; } = false;
        public bool IsSimulated { get; set; } = false;
        public bool IsBusy { get; set; } = false;
        public string StatusCode { get; set; } = "UNKNOWN"; // READY | BUSY | DISCONNECTED | ERROR | SIMULATED
        public string StatusMessage { get; set; } = "";
        public string? FirmwareVersion { get; set; }
        public string? SerialNumber { get; set; }
        public string? DriverPath { get; set; }
        public bool DriverFound { get; set; } = false;
        public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastCaptureAt { get; set; }
    }

    public enum DeviceState
    {
        Unknown,
        Ready,
        Busy,
        Disconnected,
        DriverMissing,
        Error,
        Simulated
    }
}
