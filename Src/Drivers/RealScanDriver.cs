using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AgenteBiometricoPresencial.Models;

namespace AgenteBiometricoPresencial.Drivers
{
    /// <summary>
    /// Wrapper P/Invoke para el SDK nativo de Xperix / Suprema RealScan G10 (RS_SDK.dll).
    /// Basado en el binding oficial: C:\Program Files\Xperix\RealScanSDK\Example\RealScanExample_CSharp\RS_SDK.cs
    ///
    /// POLÍTICA DE SIMULACIÓN:
    ///   - Solo se activa si RealScanDriver.SimulationMode = true (flag --simulate en CLI).
    ///   - Si el dispositivo no está conectado sin ese flag, se retorna DeviceStatus con
    ///     IsConnected=false, StatusCode="DISCONNECTED" y NO se genera data ficticia.
    /// </summary>
    public class RealScanDriver
    {
        // ─── P/Invoke Declarations ─────────────────────────────────────────────
        private const string DLL_PATH = @"C:\Program Files\Xperix\RealScanSDK\Bin\x64\RS_SDK.dll";

        // Códigos de resultado del SDK
        private const int RS_SUCCESS          = 0;
        private const int RS_ERR_NO_DEVICE    = -1001;
        private const int RS_ERR_TIMEOUT      = -1007;
        private const int RS_ERR_ABORTED      = -1008;

        // Modos de captura slap (del RS_API.h)
        public const int CAPTURE_MODE_SLAP_4_LEFT   = 0;  // 4 dedos mano izquierda
        public const int CAPTURE_MODE_SLAP_4_RIGHT  = 1;  // 4 dedos mano derecha
        public const int CAPTURE_MODE_THUMBS_2      = 2;  // 2 pulgares

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int RS_InitSDK(string configPath, out int numOfDevices);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_ExitSDK();

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_InitDevice(int deviceIndex, out IntPtr deviceHandle);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_FreeDevice(IntPtr deviceHandle);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_StartCapture(IntPtr deviceHandle, int captureMode, int captureOption, int timeout);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_AbortCapture(IntPtr deviceHandle);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_GetImageQuality(IntPtr deviceHandle, out int nfiqScore);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_GetSerialNumber(IntPtr deviceHandle, System.Text.StringBuilder serialBuffer, int bufferLen);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_GetFirmwareVersion(IntPtr deviceHandle, System.Text.StringBuilder fwBuffer, int bufferLen);

        // WSQ Image output
        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_GetWSQImageSize(IntPtr deviceHandle, out int size);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int RS_GetWSQImage(IntPtr deviceHandle, byte[] buffer, int bufferLen);

        // ─── Estado interno ────────────────────────────────────────────────────
        private IntPtr _deviceHandle = IntPtr.Zero;
        private bool _isInitialized = false;
        private bool _isBusy = false;

        /// <summary>Modo simulación: activar ÚNICAMENTE con --simulate en la CLI.</summary>
        public bool SimulationMode { get; set; } = false;

        // ─── API Pública ───────────────────────────────────────────────────────

        /// <summary>
        /// Sondea el dispositivo sin lanzar excepciones.
        /// Retorna el DeviceStatus honesto del hardware.
        /// </summary>
        public DeviceStatus ProbeDevice()
        {
            var status = new DeviceStatus
            {
                DeviceName = "Xperix RealScan G10",
                DeviceId = "REALSCAN_G10",
                DriverPath = DLL_PATH,
                LastCheckedAt = DateTime.UtcNow
            };

            // 1) Verificar si el modo simulación está activo explícitamente
            if (SimulationMode)
            {
                status.IsConnected = true;
                status.IsSimulated = true;
                status.DriverFound = true;
                status.StatusCode = "SIMULATED";
                status.StatusMessage = "Modo simulación activo (--simulate). No hay hardware real.";
                status.FirmwareVersion = "SIM-1.0";
                status.SerialNumber = "SIM-G10-0001";
                return status;
            }

            // 2) Verificar que la DLL existe en disco
            if (!System.IO.File.Exists(DLL_PATH))
            {
                status.DriverFound = false;
                status.IsConnected = false;
                status.StatusCode = "DRIVER_MISSING";
                status.StatusMessage = $"SDK no instalado. No se encontró: {DLL_PATH}";
                return status;
            }

            status.DriverFound = true;

            // 3) Intentar inicializar el SDK y detectar el dispositivo
            try
            {
                int ret = RS_InitSDK("", out int numDevices);

                if (ret != RS_SUCCESS)
                {
                    status.IsConnected = false;
                    status.StatusCode = "ERROR";
                    status.StatusMessage = $"RS_InitSDK falló. Código: {ret}";
                    return status;
                }

                if (numDevices == 0)
                {
                    RS_ExitSDK();
                    status.IsConnected = false;
                    status.StatusCode = "DISCONNECTED";
                    status.StatusMessage = "SDK inicializado pero no se detectó ningún RealScan G10 conectado por USB.";
                    return status;
                }

                // Abrir el primer dispositivo para obtener metadatos
                IntPtr handle = IntPtr.Zero;
                ret = RS_InitDevice(0, out handle);
                if (ret == RS_SUCCESS && handle != IntPtr.Zero)
                {
                    var serialBuf = new System.Text.StringBuilder(64);
                    var fwBuf = new System.Text.StringBuilder(64);
                    RS_GetSerialNumber(handle, serialBuf, 64);
                    RS_GetFirmwareVersion(handle, fwBuf, 64);

                    status.SerialNumber = serialBuf.ToString().Trim();
                    status.FirmwareVersion = fwBuf.ToString().Trim();

                    // Solo liberar si NO tenemos ya un handle activo para capturas
                    if (_deviceHandle == IntPtr.Zero)
                        RS_FreeDevice(handle);

                    status.IsConnected = true;
                    status.IsBusy = _isBusy;
                    status.StatusCode = _isBusy ? "BUSY" : "READY";
                    status.StatusMessage = $"RealScan G10 listo. Dispositivos: {numDevices}";
                }
                else
                {
                    RS_ExitSDK();
                    status.IsConnected = false;
                    status.StatusCode = "ERROR";
                    status.StatusMessage = $"No se pudo inicializar el handle del dispositivo. Código: {ret}";
                }
            }
            catch (DllNotFoundException ex)
            {
                status.DriverFound = false;
                status.IsConnected = false;
                status.StatusCode = "DRIVER_MISSING";
                status.StatusMessage = $"DLL no cargable: {ex.Message}";
            }
            catch (Exception ex)
            {
                status.IsConnected = false;
                status.StatusCode = "ERROR";
                status.StatusMessage = $"Excepción al sondear RealScan G10: {ex.Message}";
            }

            return status;
        }

        /// <summary>
        /// Inicializa el SDK y abre el handle del dispositivo para capturas.
        /// Debe llamarse antes de CaptureSlap().
        /// </summary>
        public bool Initialize(out string message)
        {
            if (SimulationMode)
            {
                _isInitialized = true;
                message = "[SIMULACIÓN] RealScan G10 inicializado en modo simulado.";
                return true;
            }

            try
            {
                int ret = RS_InitSDK("", out int numDevices);
                if (ret != RS_SUCCESS) { message = $"RS_InitSDK falló. Código: {ret}"; return false; }
                if (numDevices == 0)   { RS_ExitSDK(); message = "No se detectó ningún dispositivo RealScan G10."; return false; }

                ret = RS_InitDevice(0, out _deviceHandle);
                if (ret != RS_SUCCESS) { RS_ExitSDK(); message = $"RS_InitDevice falló. Código: {ret}"; return false; }

                _isInitialized = true;
                message = $"RealScan G10 inicializado. Dispositivos detectados: {numDevices}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Excepción inicializando RS_SDK.dll: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Ejecuta una captura slap sincrónica.
        /// fingerGroup: "SLAP_4_LEFT" | "SLAP_4_RIGHT" | "THUMBS_2"
        /// skipFingers: IDs de dedos que NO serán capturados (amputados/declarados).
        /// </summary>
        public SlapCaptureResult CaptureSlap(string fingerGroup, List<int> skipFingers, int timeoutSeconds = 30)
        {
            if (SimulationMode)
                return BuildSimulatedSlapResult(fingerGroup, skipFingers);

            if (!_isInitialized || _deviceHandle == IntPtr.Zero)
                return new SlapCaptureResult { Success = false, ErrorCode = "NOT_INITIALIZED", ErrorMessage = "Dispositivo no inicializado." };

            if (_isBusy)
                return new SlapCaptureResult { Success = false, ErrorCode = "DEVICE_BUSY", ErrorMessage = "El dispositivo ya está capturando." };

            int captureMode = fingerGroup switch
            {
                "SLAP_4_LEFT"  => CAPTURE_MODE_SLAP_4_LEFT,
                "SLAP_4_RIGHT" => CAPTURE_MODE_SLAP_4_RIGHT,
                "THUMBS_2"     => CAPTURE_MODE_THUMBS_2,
                _              => CAPTURE_MODE_SLAP_4_LEFT
            };

            try
            {
                _isBusy = true;
                int ret = RS_StartCapture(_deviceHandle, captureMode, 0, timeoutSeconds * 1000);

                if (ret == RS_ERR_TIMEOUT)
                    return new SlapCaptureResult { Success = false, ErrorCode = "TIMEOUT", ErrorMessage = "Tiempo de espera agotado sin detectar huellas." };

                if (ret == RS_ERR_ABORTED)
                    return new SlapCaptureResult { Success = false, ErrorCode = "ABORTED", ErrorMessage = "Captura cancelada por el usuario." };

                if (ret != RS_SUCCESS)
                    return new SlapCaptureResult { Success = false, ErrorCode = $"RS_ERR_{ret}", ErrorMessage = $"Error durante captura. Código: {ret}" };

                // Obtener calidad NFIQ
                RS_GetImageQuality(_deviceHandle, out int nfiqScore);

                // Obtener imagen WSQ
                RS_GetWSQImageSize(_deviceHandle, out int wsqSize);
                var wsqBytes = new byte[wsqSize];
                RS_GetWSQImage(_deviceHandle, wsqBytes, wsqSize);
                string wsqBase64 = Convert.ToBase64String(wsqBytes);

                return new SlapCaptureResult
                {
                    Success = true,
                    FingerGroup = fingerGroup,
                    NfiqQuality = nfiqScore,
                    WsqBase64 = wsqBase64,
                    SkippedFingers = skipFingers,
                    CapturedFingers = GetExpectedFingers(fingerGroup, skipFingers)
                };
            }
            catch (Exception ex)
            {
                return new SlapCaptureResult { Success = false, ErrorCode = "EXCEPTION", ErrorMessage = ex.Message };
            }
            finally
            {
                _isBusy = false;
            }
        }

        public void AbortCapture()
        {
            if (_isInitialized && _deviceHandle != IntPtr.Zero)
                RS_AbortCapture(_deviceHandle);
        }

        public void Shutdown()
        {
            if (_deviceHandle != IntPtr.Zero) { RS_FreeDevice(_deviceHandle); _deviceHandle = IntPtr.Zero; }
            if (_isInitialized) { RS_ExitSDK(); _isInitialized = false; }
        }

        // ─── Helpers internos ──────────────────────────────────────────────────

        private static List<int> GetExpectedFingers(string group, List<int> skip)
        {
            var all = group switch
            {
                "SLAP_4_LEFT"  => new List<int> { 2, 3, 4, 5 },
                "SLAP_4_RIGHT" => new List<int> { 7, 8, 9, 10 },
                "THUMBS_2"     => new List<int> { 1, 6 },
                _              => new List<int>()
            };
            all.RemoveAll(id => skip?.Contains(id) == true);
            return all;
        }

        private static SlapCaptureResult BuildSimulatedSlapResult(string fingerGroup, List<int> skipFingers)
        {
            // Imagen WSQ dummy 1×1 pixel para pruebas de integración
            string dummyWsq = "SUkqAAgAAAA="; // WSQ header mínimo
            return new SlapCaptureResult
            {
                Success = true,
                FingerGroup = fingerGroup,
                NfiqQuality = 1,
                WsqBase64 = dummyWsq,
                IsoTemplateBase64 = "Rk1SMDAyMDIw",
                ImageWidth = 800,
                ImageHeight = 750,
                CapturedFingers = GetExpectedFingers(fingerGroup, skipFingers ?? new()),
                SkippedFingers = skipFingers ?? new(),
                IsSimulated = true
            };
        }
    }

    public class SlapCaptureResult
    {
        public bool Success { get; set; }
        public string FingerGroup { get; set; } = "";
        public int NfiqQuality { get; set; }
        public string WsqBase64 { get; set; } = "";
        public string IsoTemplateBase64 { get; set; } = "";
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public List<int> CapturedFingers { get; set; } = new();
        public List<int> SkippedFingers { get; set; } = new();
        public bool IsSimulated { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
