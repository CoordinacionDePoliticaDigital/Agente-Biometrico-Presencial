using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using AgenteBiometricoPresencial.Models;
using Xperix;

namespace AgenteBiometricoPresencial.Drivers
{
    public class RealPassDriver
    {
        private RealPassSDK _sdk = null;
        private bool _isInitialized = false;
        private bool _isBusy = false;

        public bool SimulationMode { get; set; }

        private TaskCompletionSource<DocumentScanResult> _tcs;
        private string _currentSpectralMode = "VIS";

        public DeviceStatus ProbeDevice()
        {
            var status = new DeviceStatus
            {
                DeviceName = "Xperix RealPass RPNF",
                DeviceId = "REALPASS_RPNF",
                DriverPath = "Xperix.RealPassSDK.dll",
                LastCheckedAt = DateTime.UtcNow
            };

            if (SimulationMode)
            {
                status.IsConnected = true;
                status.IsSimulated = true;
                status.DriverFound = true;
                status.StatusCode = "SIMULATED";
                status.StatusMessage = "Modo simulación activo.";
                status.FirmwareVersion = "SIM-3.2";
                status.SerialNumber = "SIM-RP-0001";
                return status;
            }

            status.DriverFound = true;

            try
            {
                if (_sdk == null)
                {
                    _sdk = new RealPassSDK();
                    _sdk.Create(EventCallback, DataCallback);
                }

                int ret = _sdk.Connect(0);
                if (ret == 0) // RP_SUCCESS
                {
                    string sn = "";
                    _sdk.GetDeviceSN(ref sn);
                    status.SerialNumber = sn;
                    status.FirmwareVersion = "N/A";

                    _isInitialized = true;
                    status.IsConnected = true;
                    status.IsBusy = _isBusy;
                    status.StatusCode = _isBusy ? "BUSY" : "READY";
                    status.StatusMessage = "RealPass RPNF conectado y listo.";
                }
                else
                {
                    _sdk.Disconnect();
                    status.IsConnected = false;
                    status.StatusCode = "DISCONNECTED";
                    status.StatusMessage = string.Format("RealPass RPNF no respondió. Código: {0}.", ret);
                }
            }
            catch (Exception ex)
            {
                status.IsConnected = false;
                status.StatusCode = "ERROR";
                string innerMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                status.StatusMessage = string.Format("Excepción al cargar RealPass SDK: {0}", innerMsg);
            }

            return status;
        }

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
                message = status.StatusMessage;
                return status.IsConnected;
            }
            catch (Exception ex)
            {
                message = string.Format("Error inicializando RealPass: {0}", ex.Message);
                return false;
            }
        }

        public DocumentScanResult ScanDocument(string spectralMode = "VIS", bool readRfid = true, int timeoutSeconds = 30)
        {
            if (SimulationMode)
                return BuildSimulatedDocument();

            if (!_isInitialized || _sdk == null)
                return new DocumentScanResult { Success = false, ErrorCode = "NOT_INITIALIZED", ErrorMessage = "RealPass no inicializado." };

            if (_isBusy)
                return new DocumentScanResult { Success = false, ErrorCode = "DEVICE_BUSY", ErrorMessage = "El dispositivo ya está escaneando." };

            _isBusy = true;
            _currentSpectralMode = spectralMode;
            _tcs = new TaskCompletionSource<DocumentScanResult>();

            try
            {
                // Start document detection which fires DOC_DETECT_ON event
                _sdk.StartDocDetect();

                if (!_tcs.Task.Wait(timeoutSeconds * 1000))
                {
                    _sdk.StopDocDetect();
                    return new DocumentScanResult { Success = false, ErrorCode = "TIMEOUT", ErrorMessage = "Tiempo agotado esperando documento." };
                }

                return _tcs.Task.Result;
            }
            catch (Exception ex)
            {
                return new DocumentScanResult { Success = false, ErrorCode = "EXCEPTION", ErrorMessage = ex.Message };
            }
            finally
            {
                _sdk.StopDocDetect();
                _isBusy = false;
                _tcs = null;
            }
        }

        private void EventCallback(RealPassSDK.EventType eventType)
        {
            if (eventType == RealPassSDK.EventType.DOC_DETECT_ON)
            {
                if (_sdk != null && _isBusy)
                {
                    _sdk.ReadDocument();
                }
            }
            else if (eventType == RealPassSDK.EventType.DOCUMENT_READING_COMPLETE)
            {
                if (_sdk != null && _tcs != null && !_tcs.Task.IsCompleted)
                {
                    try
                    {
                        // ─── MRZ ────────────────────────────────────────────────
                        object mrzData = null;
                        _sdk.GetData(RealPassSDK.DataType.TEXT_MRZ, ref mrzData);
                        string mrzRaw = mrzData as string ?? "";
                        var mrz = ParseMrz(mrzRaw);

                        // ─── QR Code (INE URL) ───────────────────────────────────
                        string qrData = null;
                        /*
                        try
                        {
                            object qrObj = null;
                            _sdk.GetData(RealPassSDK.DataType.TEXT_QR, ref qrObj);
                            qrData = qrObj as string;
                        }
                        catch { }
                        */

                        // ─── Barcode (PDF417) ────────────────────────────────────
                        string barcodeData = null;
                        /*
                        try
                        {
                            object bcObj = null;
                            _sdk.GetData(RealPassSDK.DataType.TEXT_BARCODE, ref bcObj);
                            barcodeData = bcObj as string;
                        }
                        catch { }
                        */

                        // ─── Imágenes espectrales (WH + IR + UV) ────────────────
                        string whBase64 = ExtractImageBase64(RealPassSDK.DataType.IMAGE_WH);
                        string irBase64 = ExtractImageBase64(RealPassSDK.DataType.IMAGE_IR);
                        string uvBase64 = ExtractImageBase64(RealPassSDK.DataType.IMAGE_UV);

                        var result = new DocumentScanResult
                        {
                            Success     = true,
                            Mrz         = mrz,
                            QrData      = qrData,
                            BarcodeData = barcodeData,
                            Images      = new DocumentImages
                            {
                                whiteLightBase64  = whBase64,
                                infraredBase64    = irBase64,
                                ultravioletBase64 = uvBase64
                            }
                        };

                        _tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        _tcs.TrySetResult(new DocumentScanResult { Success = false, ErrorCode = "EXCEPTION", ErrorMessage = ex.Message });
                    }
                }
            }
        }

        private string ExtractImageBase64(RealPassSDK.DataType dataType)
        {
            try
            {
                object imgObj = null;
                _sdk.GetData(dataType, ref imgObj);
                Bitmap bmp = imgObj as Bitmap;
                if (bmp == null) return null;
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Jpeg);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            catch { return null; }
        }

        private void DataCallback(RealPassSDK.DataType dataType, object data)
        {
            // Ignored, we pull data on DOCUMENT_READING_COMPLETE
        }

        public void Shutdown()
        {
            if (_sdk != null && _isInitialized)
            {
                try { _sdk.Disconnect(); } catch { }
            }
            _isInitialized = false;
            _sdk = null;
        }

        public bool IsConnected
        {
            get { return _isInitialized; }
        }

        private static MrzData ParseMrz(string mrzRaw)
        {
            if (string.IsNullOrEmpty(mrzRaw) || string.IsNullOrEmpty(mrzRaw.Trim())) return null;

            var lines = mrzRaw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;

            try
            {
                // CASO 1: TD1 (INE / Identificaciones de 3 líneas, ~30 caracteres/línea)
                if (lines.Length >= 3)
                {
                    string l1 = lines[0].Trim().PadRight(30);
                    string l2 = lines[1].Trim().PadRight(30);
                    string l3 = lines[2].Trim().PadRight(30);

                    string docType = l1.Substring(0, 2).Replace("<", "").Trim();
                    string country = l1.Substring(2, 3).Trim();
                    string docNum  = l1.Substring(5, 9).Replace("<", "").Trim();

                    string dob     = l2.Length >= 6 ? l2.Substring(0, 6) : "";
                    string sex     = l2.Length >= 8 ? l2.Substring(7, 1) : "";
                    string expiry  = l2.Length >= 14 ? l2.Substring(8, 6) : "";

                    // Nombres en línea 3: APELLIDOS<<NOMBRES
                    string nameStr = l3.Trim();
                    var nameParts  = nameStr.Split(new[] { "<<" }, StringSplitOptions.None);
                    string surname    = nameParts.Length > 0 ? nameParts[0].Replace("<", " ").Trim() : "";
                    string givenNames = nameParts.Length > 1 ? nameParts[1].Replace("<", " ").Trim() : "";

                    // Buscar CURP con Expresión Regular en cualquiera de las líneas
                    string curp = "";
                    foreach (var l in lines)
                    {
                        var clean = l.Replace("<", " ");
                        var match = System.Text.RegularExpressions.Regex.Match(clean, @"[A-Z]{4}\d{6}[HM][A-Z]{5}[A-Z0-9]\d");
                        if (match.Success)
                        {
                            curp = match.Value;
                            break;
                        }
                    }

                    bool isExpired = false;
                    int tempVal;
                    if (expiry.Length == 6 && int.TryParse(expiry, out tempVal))
                    {
                        int yy = int.Parse(expiry.Substring(0, 2));
                        int mm = int.Parse(expiry.Substring(2, 2));
                        int dd = int.Parse(expiry.Substring(4, 2));
                        int fullYear = (yy >= 0 && yy <= DateTime.Now.Year % 100 + 10) ? 2000 + yy : 1900 + yy;
                        if (mm >= 1 && mm <= 12 && dd >= 1 && dd <= 31)
                        {
                            var expiryDate = new DateTime(fullYear, mm, dd);
                            isExpired = expiryDate < DateTime.Today;
                        }
                    }

                    return new MrzData
                    {
                        documentType   = string.IsNullOrEmpty(docType) ? "I" : docType,
                        country        = country,
                        surname        = surname,
                        givenNames     = givenNames,
                        documentNumber = docNum,
                        curp           = curp,
                        dateOfBirth    = dob,
                        sex            = sex,
                        expiryDate     = expiry,
                        isExpired      = isExpired
                    };
                }

                // CASO 2: TD3 (Pasaportes de 2 líneas, 44 caracteres/línea)
                if (lines.Length >= 2)
                {
                    string l1 = lines[0].Trim().PadRight(44);
                    string l2 = lines[1].Trim().PadRight(44);

                    string docType = l1.Substring(0, 2).Replace("<", "").Trim();
                    string country = l1.Substring(2, 3).Trim();
                    string names   = l1.Substring(5, 39);
                    var nameParts  = names.Split(new[] { "<<" }, StringSplitOptions.None);
                    string surname     = nameParts.Length > 0 ? nameParts[0].Replace("<", " ").Trim() : "";
                    string givenNames  = nameParts.Length > 1 ? nameParts[1].Replace("<", " ").Trim() : "";

                    string docNumber  = l2.Substring(0, 9).Replace("<", "").Trim();
                    string dob        = l2.Substring(13, 6);
                    string sex        = l2.Substring(20, 1);
                    string expiry     = l2.Substring(21, 6);
                    string personal   = l2.Substring(28, 14).Replace("<", "").Trim();

                    string curp = personal.Length >= 18 ? personal.Substring(0, 18) : personal;

                    bool isExpired = false;
                    int tempVal;
                    if (expiry.Length == 6 && int.TryParse(expiry, out tempVal))
                    {
                        int yy = int.Parse(expiry.Substring(0, 2));
                        int mm = int.Parse(expiry.Substring(2, 2));
                        int dd = int.Parse(expiry.Substring(4, 2));
                        int fullYear = (yy >= 0 && yy <= DateTime.Now.Year % 100 + 10) ? 2000 + yy : 1900 + yy;
                        if (mm >= 1 && mm <= 12 && dd >= 1 && dd <= 31)
                        {
                            var expiryDate = new DateTime(fullYear, mm, dd);
                            isExpired = expiryDate < DateTime.Today;
                        }
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
            }
            catch (Exception ex)
            {
                Console.WriteLine("[RealPass] Error parseando MRZ: " + ex.Message);
            }
            return null;
        }

        private static DocumentScanResult BuildSimulatedDocument()
        {
            return new DocumentScanResult
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
    }

    public class DocumentScanResult
    {
        public bool           Success     { get; set; }
        public bool           IsSimulated { get; set; }
        public MrzData        Mrz         { get; set; }
        public DocumentImages Images      { get; set; }
        /// <summary>URL del código QR del INE (ej: http://qr.ine.mx/...)</summary>
        public string         QrData      { get; set; }
        /// <summary>Datos del código de barras PDF417 si aplica</summary>
        public string         BarcodeData { get; set; }
        public string         ErrorCode   { get; set; }
        public string         ErrorMessage { get; set; }
    }
}
