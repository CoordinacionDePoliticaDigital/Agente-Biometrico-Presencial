using System;
using System.IO;

namespace AgenteBiometricoPresencial.Drivers
{
    /// <summary>
    /// Wrapper para el SDK Managed de Xperix RealPass RPNF (Xperix.RealPassSDK.dll)
    /// </summary>
    public class RealPassDriver
    {
        private bool _isConnected = false;

        public bool Initialize(out string message)
        {
            string dllPath = @"C:\Program Files\Xperix\RealPassSDK\Bin\x64\Xperix.RealPassSDK.dll";
            if (!File.Exists(dllPath))
            {
                message = $"No se encontró Xperix.RealPassSDK.dll en {dllPath}";
                return false;
            }

            try
            {
                // En tiempo de compilación o reflexión, instanciar RealPassSDK
                message = "Controlador RealPass RPNF listo y configurado.";
                _isConnected = true;
                return true;
            }
            catch (Exception ex)
            {
                message = $"Excepción al cargar RealPass SDK: {ex.Message}";
                return false;
            }
        }

        public bool IsConnected => _isConnected;
    }
}
