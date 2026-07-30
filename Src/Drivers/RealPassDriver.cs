using System;
using System.IO;
using System.Collections.Generic;
using AgenteBiometricoPresencial.Models;

namespace AgenteBiometricoPresencial.Drivers
{
    /// <summary>
    /// Wrapper para el SDK Managed de Xperix RealPass RPNF.
    /// Namespace: Xperix.RealPassSDK  (Xperix.RealPassSDK.dll)
    /// Referencia de integración: RealPass_Windows_Webagent_v1.1.pdf
    ///
    /// POLÍTICA DE SIMULACIÓN:
    ///   - Solo se activa si SimulationMode = true (flag --simulate en CLI).
    ///   - Sin ese flag, si el SDK no está o el dispositivo no responde,
    ///     se reporta el estado real: DRIVER_MISSING / DISCONNECTED / ERROR.
    /// </summary>
    public class RealPassDriver
    {
        private const string DLL_MANAGED = @"C:\Program Files\Xperix\RealPassSDK\Bin\x64\Xperix.RealPassSDK.dll";
        private const string DLL_NATIVE  = @"C:\Program Files\Xperix\RealPassSDK\Bin\x64\RealPassSDK.dll";

        // Referencia dinámica al tipo RealPassSDK en tiempo de ejecución
        // (el assembly se carga opcionalmente para no romper compilación sin el SDK instalado)
        private dynamic? _sdk = null;
        private bool _isInitialized = false;
        private bool _isBusy = false;

        /// <summary>Modo simulación: activar ÚNICAMENTE con --simulate en la CLI.</summary>
        public bool SimulationMode { get; set; } = false;

        // ─── API Pública ───────────────────────────────────────────────────────

        /// <summary>
        /// Sondea el estado del RealPass RPNF sin lanzar excepciones.
        /// </summary>
        public DeviceStatus ProbeDevice()
        {
            var status = new DeviceStatus
            {
                DeviceName = "Xperix RealPass RPNF",
                DeviceId = "REALPASS_RPNF",
                DriverPath = DLL_MANAGED,
                LastCheckedAt = DateTime.UtcNow
            };

            // 1) Modo simulación explícito
            if (SimulationMode)
            {
                status.IsConnected = true;
                status.IsSimulated = true;
                status.DriverFound = true;
                status.StatusCode = "SIMULATED";
                status.StatusMessage = "Modo simulación activo (--simulate). No hay hardware real.";
                status.FirmwareVersion = "SIM-3.2";
                status.SerialNumber = "SIM-RP-0001";
                return status;
            }

            // 2) Verificar DLL managed en disco
            if (!File.Exists(DLL_MANAGED))
            {
                status.DriverFound = false;
                status.IsConnected = false;
                status.StatusCode = "DRIVER_MISSING";
                status.StatusMessage = $"SDK no instalado. No se encontró: {DLL_MANAGED}";
                return status;
            }

            // 3) También verificar la DLL nativa de soporte
            if (!File.Exists(DLL_NATIVE))
            {
                status.DriverFound = false;
                status.IsConnected = false;
                status.StatusCode = "DRIVER_MISSING";
                status.StatusMessage = $"Falta DLL nativa de soporte: {DLL_NATIVE}";
                return status;
            }

            status.DriverFound = true;

            // 4) Intentar carga dinámica y sondeo del dispositivo
            try
            {
                var assembly = System.Reflection.Assembly.LoadFrom(DLL_MANAGED);
                var sdkType = assembly.GetType("Xperix.RealPassSDK");
                if (sdkType == null)
                {
                    status.IsConnected = false;
                    status.StatusCode = "ERROR";
                    status.StatusMessage = "No se encontró el tipo Xperix.RealPassSDK en el assembly.";
                    return status;
                }

                _sdk = Activator.CreateInstance(sdkType);

                // Intentar abrir conexión con el dispositivo
                // La API real usa: m_RP.Open() → retorna int (0 = OK)
                int ret = (int)sdkType.GetMethod("Open")!.Invoke(_sdk, null)!;
                if (ret == 0)
                {
                    // Obtener versión de firmware si el método existe
                    var fwMethod = sdkType.GetMethod("GetFirmwareVersion");
                    if (fwMethod != null)
                        status.FirmwareVersion = fwMethod.Invoke(_sdk, null)?.ToString();

                    var snMethod = sdkType.GetMethod("GetSerialNumber");
                    if (snMethod != null)
                        status.SerialNumber = snMethod.Invoke(_sdk, null)?.ToString();

                    _isInitialized = true;
                    status.IsConnected = true;
                    status.IsBusy = _isBusy;
                    status.StatusCode = _isBusy ? "BUSY" : "READY";
                    status.StatusMessage = "RealPass RPNF conectado y listo.";
                }
                else
                {
                    // Cerrar handle si falla
                    sdkType.GetMethod("Close")?.Invoke(_sdk, null);
                    status.IsConnected = false;
                    status.StatusCode = "DISCONNECTED";
                    status.StatusMessage = $"RealPass RPNF no respondió al abrir la conexión. Código: {ret}. Verifique la conexión USB.";
                }
            }
            catch (Exception ex)
            {
                status.IsConnected = false;
                status.StatusCode = "ERROR";
                status.StatusMessage = $"Excepción al cargar RealPass SDK: {ex.InnerException?.Message ?? ex.Message}";
            }

            return status;
        }

        /// <summary>
        /// Inicializa el SDK para uso en capturas sucesivas.
        /// </summary>
        public bool Initialize(out string message)
        {
            if (SimulationMode)
            {
                _isInitialized = true;
                message = "[SIMULACIÓN] RealPass RPNF inicializado en modo simulado.";
                return true;
            }

            try
            {
                var status = ProbeDevice();
                if (!status.IsConnected)
                {
                    message = status.StatusMessage;
                    return false;
                }
                message = status.StatusMessage;
                return true;
            }
            catch (Exception ex)
            {
                message = $"Error inicializando RealPass: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Inicia un escaneo de documento de identidad.
        /// spectralMode: "VIS" (luz blanca) | "IR" (infrarrojo) | "UV" (ultravioleta)
        /// readRfid: leer chip RFID/NFC del pasaporte
        /// </summary>
        public DocumentScanResult ScanDocument(string spectralMode = "VIS", bool readRfid = true, int timeoutSeconds = 30)
        {
            if (SimulationMode)
                return BuildSimulatedDocument();

            if (!_isInitialized || _sdk == null)
                return new DocumentScanResult { Success = false, ErrorCode = "NOT_INITIALIZED", ErrorMessage = "RealPass no inicializado." };

            if (_isBusy)
                return new DocumentScanResult { Success = false, ErrorCode = "DEVICE_BUSY", ErrorMessage = "El dispositivo ya está escaneando." };

            try
            {
                _isBusy = true;
                var sdkType = _sdk!.GetType();

                // Capturar imagen (API: Capture(int mode, int timeout))
                // mode: 0=VIS, 1=IR, 2=UV
                int mode = spectralMode switch { "IR" => 1, "UV" => 2, _ => 0 };
                int ret = (int)sdkType.GetMethod("Capture")!.Invoke(_sdk, new object[] { mode, timeoutSeconds * 1000 })!;

                if (ret != 0)
                    return new DocumentScanResult { Success = false, ErrorCode = $"CAPTURE_ERR_{ret}", ErrorMessage = $"Captura fallida. Código: {ret}" };

                // Obtener imagen como bytes
                byte[] imgBytes = (byte[])sdkType.GetMethod("GetImage")!.Invoke(_sdk, null)!;
                string imgBase64 = Convert.ToBase64String(imgBytes);

                // Obtener texto MRZ
                string mrzRaw = sdkType.GetMethod("GetMRZText")!.Invoke(_sdk, null)?.ToString() ?? "";
                var mrz = ParseMrz(mrzRaw);

                // Leer chip RFID si se solicita y el documento lo tiene
                bool rfidRead = false;
                if (readRfid)
                {
                    var rfidMethod = sdkType.GetMethod("ReadRFID");
                    if (rfidMethod != null)
                    {
                        int rfidRet = (int)rfidMethod.Invoke(_sdk, null)!;
                        rfidRead = rfidRet == 0;
                    }
                }

                if (mrz != null) mrz.rfidRead = rfidRead;

                return new DocumentScanResult
                {
                    Success = true,
                    Mrz = mrz,
                    Images = new DocumentImages
                    {
                        whiteLightBase64 = spectralMode == "VIS" ? imgBase64 : null,
                        infraredBase64   = spectralMode == "IR"  ? imgBase64 : null,
                        ultravioletBase64 = spectralMode == "UV" ? imgBase64 : null
                    }
                };
            }
            catch (Exception ex)
            {
                return new DocumentScanResult { Success = false, ErrorCode = "EXCEPTION", ErrorMessage = ex.Message };
            }
            finally
            {
                _isBusy = false;
            }
        }

        public void Shutdown()
        {
            if (_sdk != null && _isInitialized)
            {
                try { _sdk.GetType().GetMethod("Close")?.Invoke(_sdk, null); } catch { }
            }
            _isInitialized = false;
            _sdk = null;
        }

        public bool IsConnected => _isInitialized;

        // ─── Helpers internos ──────────────────────────────────────────────────

        /// <summary>
        /// Parsea texto MRZ de 2 líneas ICAO 9303 (pasaporte mexicano).
        /// Línea 1 (44 chars): tipo doc + país + apellido + nombres
        /// Línea 2 (44 chars): núm doc + país + fecha nac + sexo + fecha exp + número personal
        /// </summary>
        private static MrzData? ParseMrz(string mrzRaw)
        {
            if (string.IsNullOrWhiteSpace(mrzRaw)) return null;

            var lines = mrzRaw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return null;

            try
            {
                string l1 = lines[0].PadRight(44);
                string l2 = lines[1].PadRight(44);

                string docType = l1[0..2].Trim('<');
                string country = l1[2..5];
                string names   = l1[5..44];
                var nameParts  = names.Split(new[] { "<<" }, StringSplitOptions.None);
                string surname     = nameParts.Length > 0 ? nameParts[0].Replace("<", " ").Trim() : "";
                string givenNames  = nameParts.Length > 1 ? nameParts[1].Replace("<", " ").Trim() : "";

                string docNumber  = l2[0..9].Replace("<", "");
                string dob        = l2[13..19];
                string sex        = l2[20..21];
                string expiry     = l2[21..27];
                string personal   = l2[28..42].Replace("<", "");

                // En México el campo personal (28-42) contiene la CURP
                string curp = personal.Length >= 18 ? personal[..18] : personal;

                bool isExpired = false;
                if (expiry.Length == 6 && int.TryParse(expiry, out _))
                {
                    int yy = int.Parse(expiry[..2]);
                    int mm = int.Parse(expiry[2..4]);
                    int dd = int.Parse(expiry[4..6]);
                    int fullYear = yy >= 0 && yy <= DateTime.Now.Year % 100 + 10
                        ? 2000 + yy : 1900 + yy;
                    var expiryDate = new DateTime(fullYear, mm, dd);
                    isExpired = expiryDate < DateTime.Today;
                }

                return new MrzData
                {
                    documentType   = docType,
                    country        = country,
                    surname        = surname,
                    givenNames     = givenNames,
                    documentNumber = docNumber,
                    curp           = curp,
                    dateOfBirth    = dob,
                    sex            = sex,
                    expiryDate     = expiry,
                    isExpired      = isExpired
                };
            }
            catch
            {
                return null;
            }
        }

        private static DocumentScanResult BuildSimulatedDocument() =>
            new()
            {
                Success = true,
                IsSimulated = true,
                Mrz = new MrzData
                {
                    documentType   = "P",
                    country        = "MEX",
                    surname        = "CASTILLO MARQUEZ",
                    givenNames     = "PRUEBA MARIA DEL CARMEN",
                    documentNumber = "G12345678",
                    curp           = "CAMC030110MCHSRRA9",
                    dateOfBirth    = "030110",
                    sex            = "F",
                    expiryDate     = "300110",
                    isExpired      = false,
                    rfidRead       = true
                },
                Images = new DocumentImages
                {
                    whiteLightBase64  = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
                    infraredBase64    = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=",
                    ultravioletBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwADhQGAWjR9awAAAABJRU5ErkJggg=="
                }
            };
    }

    public class DocumentScanResult
    {
        public bool Success { get; set; }
        public bool IsSimulated { get; set; }
        public MrzData? Mrz { get; set; }
        public DocumentImages? Images { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
