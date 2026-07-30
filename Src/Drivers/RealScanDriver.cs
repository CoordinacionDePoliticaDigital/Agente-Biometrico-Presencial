using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AgenteBiometricoPresencial.Models;

namespace AgenteBiometricoPresencial.Drivers
{
    public class RealScanDriver
    {
        // ─── P/Invoke Declarations ─────────────────────────────────────────────
        private const string DLL_DIR = @"C:\Program Files\Xperix\RealScanSDK\Bin\x64";
        private const string DLL_PATH = DLL_DIR + @"\RS_SDK.dll";

        private const int RS_SUCCESS          = 0;
        private const int RS_ERR_NO_DEVICE    = -1001;
        private const int RS_ERR_TIMEOUT      = -1007;
        private const int RS_ERR_ABORTED      = -1008;

        public const int CAPTURE_MODE_SLAP_4_LEFT   = 0;
        public const int CAPTURE_MODE_SLAP_4_RIGHT  = 1;
        public const int CAPTURE_MODE_THUMBS_2      = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct RSDeviceInfo
        {
            public int deviceType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] productName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] deviceID;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] firmwareVersion;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] hardwareVersion;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public int[] reserved;
        }

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_InitSDK")]
        private static extern int RS_InitSDK(byte[]? configFileName, int option, ref int numOfDevice);

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_ExitAllDevices")]
        private static extern int RS_ExitAllDevices();

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_InitDevice")]
        private static extern int RS_InitDevice(int deviceIndex, ref int deviceHandle);

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_ExitDevice")]
        private static extern int RS_ExitDevice(int deviceHandle);

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_SetCaptureMode")]
        private static extern int RS_SetCaptureMode(int deviceHandle, int captureMode, int captureOption, bool withModeLED);

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_StartCapture")]
        private static extern int RS_StartCapture(int deviceHandle, bool autoCapture, int timeout);

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_AbortCapture")]
        private static extern int RS_AbortCapture(int deviceHandle);

        [DllImport(DLL_PATH, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall, EntryPoint = "RS_GetDeviceInfo")]
        private static extern int RS_GetDeviceInfo(int deviceHandle, ref RSDeviceInfo deviceInfo);

        // ─── Estado interno ────────────────────────────────────────────────────
        private int _deviceHandle = 0;
        private bool _isInitialized = false;
        private bool _isBusy = false;

        public bool SimulationMode { get; set; } = false;

        // ─── API Pública ───────────────────────────────────────────────────────

        public DeviceStatus ProbeDevice()
        {
            var status = new DeviceStatus
            {
                DeviceName = "Xperix RealScan G10",
                DeviceId = "REALSCAN_G10",
                DriverPath = DLL_PATH,
                LastCheckedAt = DateTime.UtcNow
            };

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

            if (!System.IO.File.Exists(DLL_PATH))
            {
                status.DriverFound = false;
                status.IsConnected = false;
                status.StatusCode = "DRIVER_MISSING";
                status.StatusMessage = $"SDK no instalado. No se encontró: {DLL_PATH}";
                return status;
            }

            status.DriverFound = true;

            if (_deviceHandle != 0)
            {
                status.IsConnected = true;
                status.IsBusy = _isBusy;
                status.StatusCode = _isBusy ? "BUSY" : "READY";
                status.StatusMessage = $"RealScan G10 listo.";
                return status;
            }

            string originalDir = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = DLL_DIR;
                int numDevices = 0;
                int ret = RS_InitSDK(null, 0, ref numDevices);

                if (ret != RS_SUCCESS)
                {
                    status.IsConnected = false;
                    status.StatusCode = "ERROR";
                    status.StatusMessage = $"RS_InitSDK falló. Código: {ret}";
                    return status;
                }

                if (numDevices == 0)
                {
                    RS_ExitAllDevices();
                    status.IsConnected = false;
                    status.StatusCode = "DISCONNECTED";
                    status.StatusMessage = "SDK inicializado pero no se detectó RealScan G10.";
                    return status;
                }

                int handle = 0;
                ret = RS_InitDevice(0, ref handle);
                if (ret == RS_SUCCESS && handle != 0)
                {
                    var info = new RSDeviceInfo();
                    RS_GetDeviceInfo(handle, ref info);
                    
                    status.SerialNumber = Encoding.ASCII.GetString(info.deviceID).Replace("\0", "").Trim();
                    status.FirmwareVersion = Encoding.ASCII.GetString(info.firmwareVersion).Replace("\0", "").Trim();

                    if (_deviceHandle == 0)
                        RS_ExitDevice(handle);

                    status.IsConnected = true;
                    status.IsBusy = _isBusy;
                    status.StatusCode = _isBusy ? "BUSY" : "READY";
                    status.StatusMessage = $"RealScan G10 listo. Dispositivos: {numDevices}";
                }
                else
                {
                    RS_ExitAllDevices();
                    status.IsConnected = false;
                    status.StatusCode = "ERROR";
                    status.StatusMessage = $"No se pudo inicializar handle. Código: {ret}";
                }
            }
            catch (Exception ex)
            {
                status.IsConnected = false;
                status.StatusCode = "ERROR";
                status.StatusMessage = $"Excepción al sondear: {ex.Message}";
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }

            return status;
        }

        public bool Initialize(out string message)
        {
            if (SimulationMode)
            {
                _isInitialized = true;
                message = "[SIMULACIÓN] RealScan G10 inicializado.";
                return true;
            }

            string originalDir = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = DLL_DIR;
                int numDevices = 0;
                int ret = RS_InitSDK(null, 0, ref numDevices);
                if (ret != RS_SUCCESS) { message = $"RS_InitSDK falló. Código: {ret}"; return false; }
                if (numDevices == 0)   { RS_ExitAllDevices(); message = "No se detectó RealScan G10."; return false; }

                ret = RS_InitDevice(0, ref _deviceHandle);
                if (ret != RS_SUCCESS) { RS_ExitAllDevices(); message = $"RS_InitDevice falló. Código: {ret}"; return false; }

                _isInitialized = true;
                message = $"RealScan G10 inicializado. Detectados: {numDevices}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Error fatal al inicializar RS_SDK: {ex.Message}";
                return false;
            }
            finally
            {
                Environment.CurrentDirectory = originalDir;
            }
        }

        public SlapCaptureResult CaptureSlap(string fingerGroup, List<int> skipFingers, int timeoutSeconds = 30)
        {
            if (SimulationMode)
                return BuildSimulatedSlapResult(fingerGroup, skipFingers);

            if (!_isInitialized || _deviceHandle == 0)
                return new SlapCaptureResult { Success = false, ErrorCode = "NOT_INITIALIZED", ErrorMessage = "No inicializado." };

            if (_isBusy)
                return new SlapCaptureResult { Success = false, ErrorCode = "DEVICE_BUSY", ErrorMessage = "Dispositivo ocupado." };

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
                
                int ret = RS_SetCaptureMode(_deviceHandle, captureMode, 0, true);
                if (ret != RS_SUCCESS)
                    return new SlapCaptureResult { Success = false, ErrorCode = $"RS_ERR_{ret}", ErrorMessage = $"Error SetCaptureMode. Código: {ret}" };

                ret = RS_StartCapture(_deviceHandle, true, timeoutSeconds * 1000);

                if (ret == RS_ERR_TIMEOUT)
                    return new SlapCaptureResult { Success = false, ErrorCode = "TIMEOUT", ErrorMessage = "Tiempo agotado." };
                if (ret == RS_ERR_ABORTED)
                    return new SlapCaptureResult { Success = false, ErrorCode = "ABORTED", ErrorMessage = "Cancelada." };
                if (ret != RS_SUCCESS)
                    return new SlapCaptureResult { Success = false, ErrorCode = $"RS_ERR_{ret}", ErrorMessage = $"Error de captura. Código: {ret}" };

                // TODO: Implement actual NFIQ and WSQ export when we have the right P/Invoke signatures
                return new SlapCaptureResult
                {
                    Success = true,
                    FingerGroup = fingerGroup,
                    NfiqQuality = 1,
                    WsqBase64 = "SUkqAAgAAAA=", // Dummy Base64 until implemented
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
            if (_isInitialized && _deviceHandle != 0)
                RS_AbortCapture(_deviceHandle);
        }

        public void Shutdown()
        {
            if (_deviceHandle != 0) { RS_ExitDevice(_deviceHandle); _deviceHandle = 0; }
            if (_isInitialized) { RS_ExitAllDevices(); _isInitialized = false; }
        }

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
            return new SlapCaptureResult
            {
                Success = true,
                FingerGroup = fingerGroup,
                NfiqQuality = 1,
                WsqBase64 = "SUkqAAgAAAA=",
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
