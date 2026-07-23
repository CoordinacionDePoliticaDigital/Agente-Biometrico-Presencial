using System;
using System.Runtime.InteropServices;

namespace AgenteBiometricoPresencial.Drivers
{
    /// <summary>
    /// Wrapper P/Invoke para el SDK nativo de Xperix / Suprema RealScan G10 (RS_SDK.dll)
    /// </summary>
    public class RealScanDriver
    {
        private const string DLL_PATH = @"C:\Program Files\Xperix\RealScanSDK\Bin\x64\RS_SDK.dll";

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern int RS_InitSDK(string configPath, out int numOfDevices);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        public static extern int RS_ExitSDK();

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        public static extern int RS_InitDevice(int deviceIndex, out IntPtr deviceHandle);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        public static extern int RS_FreeDevice(IntPtr deviceHandle);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        public static extern int RS_StartCapture(IntPtr deviceHandle, int captureMode, int captureOption, int timeout);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        public static extern int RS_AbortCapture(IntPtr deviceHandle);

        private IntPtr _deviceHandle = IntPtr.Zero;
        private bool _isInitialized = false;

        public bool Initialize(out string message)
        {
            try
            {
                int nRet = RS_InitSDK("", out int numOfDevices);
                if (nRet != 0)
                {
                    message = $"Error inicializando RealScan SDK. Código: {nRet}";
                    return false;
                }

                if (numOfDevices == 0)
                {
                    message = "No se detectaron dispositivos RealScan G10 conectados.";
                    return false;
                }

                nRet = RS_InitDevice(0, out _deviceHandle);
                if (nRet != 0)
                {
                    message = $"Error conectando con RealScan G10. Código: {nRet}";
                    return false;
                }

                _isInitialized = true;
                message = $"RealScan G10 inicializado con éxito. Dispositivos: {numOfDevices}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Excepción al cargar RS_SDK.dll: {ex.Message}";
                return false;
            }
        }

        public void Shutdown()
        {
            if (_deviceHandle != IntPtr.Zero)
            {
                RS_FreeDevice(_deviceHandle);
                _deviceHandle = IntPtr.Zero;
            }
            if (_isInitialized)
            {
                RS_ExitSDK();
                _isInitialized = false;
            }
        }
    }
}
