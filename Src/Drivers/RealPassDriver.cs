using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using AgenteBiometricoPresencial.Contracts;
using Xperix;

namespace AgenteBiometricoPresencial.Drivers;

/// <summary>
/// Adaptador asíncrono para RealPass. El SDK emite callbacks desde hilos
/// nativos; el driver los agrega en una única operación de lectura serializada.
/// </summary>
public sealed class RealPassDriver : IDisposable
{
    private const string ManagedDllPath =
        @"C:\Program Files\Xperix\RealPassSDK\Bin\x64\Xperix.RealPassSDK.dll";
    private const string RealPassHardwareId = @"VID_16D1&PID_1107";

    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly object _callbackLock = new();
    private RealPassSDK? _sdk;
    private TaskCompletionSource<DocumentCaptureResult>? _activeRead;
    private RealPassSDK.SYNTHESIS_RESULT? _lastSynthesis;
    private bool _connected;
    private string? _serialNumber;
    private string? _lastError;
    private bool _disposed;

    public DeviceState State => new(
        Available: File.Exists(ManagedDllPath),
        Connected: _connected,
        SerialNumber: _serialNumber,
        LastError: _lastError,
        ProductName: "RealPass RPNF");

    public bool Initialize(out string message)
    {
        if (_disposed)
        {
            message = "El controlador RealPass ya fue liberado.";
            return false;
        }

        if (_connected)
        {
            message = "RealPass RPNF ya está conectado.";
            return true;
        }

        if (!File.Exists(ManagedDllPath))
        {
            _lastError = $"No se encontró el ensamblado RealPass en {ManagedDllPath}.";
            message = _lastError;
            return false;
        }

        if (!UsbDevicePresence.IsPresent(RealPassHardwareId))
        {
            _connected = false;
            _lastError = "No se detectó físicamente el RealPass en el bus USB.";
            message = _lastError;
            return false;
        }

        try
        {
            _sdk = new RealPassSDK();
            var createResult = _sdk.Create(OnEvent, OnData);
            if (createResult != RealPassSDK.RP_SUCCESS)
            {
                _lastError = $"RealPass Create devolvió el código {createResult}.";
                message = _lastError;
                _sdk.Destroy();
                _sdk = null;
                return false;
            }

            var result = _sdk.Connect(0);
            if (result != RealPassSDK.RP_SUCCESS)
            {
                _lastError = $"RealPass Connect(0) devolvió el código {result}.";
                message = _lastError;
                _sdk.Destroy();
                _sdk = null;
                return false;
            }

            _connected = true;
            var serial = string.Empty;
            if (_sdk.GetDeviceSN(ref serial) == RealPassSDK.RP_SUCCESS)
            {
                _serialNumber = serial;
            }

            _lastError = null;
            message = string.IsNullOrWhiteSpace(_serialNumber)
                ? "RealPass RPNF conectado."
                : $"RealPass RPNF conectado. Serie: {_serialNumber}.";
            return true;
        }
        catch (Exception exception)
        {
            _connected = false;
            _lastError = $"No se pudo inicializar RealPass: {exception.Message}";
            message = _lastError;
            return false;
        }
    }

    public async Task<DocumentCaptureResult> ReadDocumentAsync(
        bool readRfid,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (!RefreshPhysicalPresence(out var presenceMessage) || _sdk is null || !_connected)
        {
            throw new BiometricDeviceException(
                "REALPASS_NOT_CONNECTED",
                _lastError ?? presenceMessage);
        }

        if (timeoutSeconds is < 1 or > 180)
        {
            throw new BiometricDeviceException(
                "INVALID_TIMEOUT",
                "timeoutSeconds debe estar entre 1 y 180.");
        }

        var operationTimer = Stopwatch.StartNew();
        Console.WriteLine(
            $"[HW REALPASS] Esperando acceso exclusivo al lector; timeout operativo={timeoutSeconds}s.");
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            ConfigureReading(readRfid);
            var completion = new TaskCompletionSource<DocumentCaptureResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_callbackLock)
            {
                _lastSynthesis = null;
                _activeRead = completion;
            }

            var startResult = _sdk.StartDocDetect();
            ThrowOnSdkError(startResult, "No se pudo iniciar la detección del documento.");
            Console.WriteLine("[HW REALPASS] Detector activo; esperando que se coloque un documento.");
            try
            {
                var result = await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(timeoutSeconds),
                    cancellationToken);
                Console.WriteLine(
                    $"[SUCCESS REALPASS] SDK completó la lectura en {operationTimer.ElapsedMilliseconds} ms.");
                return result;
            }
            catch (TimeoutException)
            {
                Console.WriteLine(
                    $"[TIMEOUT REALPASS] No se completó la lectura después de {operationTimer.ElapsedMilliseconds} ms.");
                throw new BiometricDeviceException(
                    "REALPASS_DOCUMENT_TIMEOUT",
                    "No se detectó o leyó un documento dentro del tiempo permitido.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine(
                    $"[WARN REALPASS] Lectura cancelada después de {operationTimer.ElapsedMilliseconds} ms.");
                throw new BiometricDeviceException(
                    "REALPASS_DOCUMENT_CANCELLED",
                    "La lectura del documento fue cancelada.");
            }
            finally
            {
                if (_connected)
                {
                    var stopResult = _sdk.StopDocDetect();
                    Console.WriteLine(
                        stopResult == RealPassSDK.RP_SUCCESS
                            ? "[HW REALPASS] Detector documental detenido."
                            : $"[WARN REALPASS] StopDocDetect devolvió el código {stopResult}.");
                }
                else
                {
                    Console.WriteLine("[HW REALPASS] StopDocDetect omitido porque el USB ya no está presente.");
                }
            }
        }
        finally
        {
            lock (_callbackLock)
            {
                _activeRead = null;
                _lastSynthesis = null;
            }

            _readLock.Release();
        }
    }

    public bool RefreshConnection(out string message)
    {
        if (_disposed)
        {
            message = "El controlador RealPass está detenido.";
            return false;
        }

        if (!RefreshPhysicalPresence(out message))
        {
            return false;
        }

        if (_connected && _sdk is not null)
        {
            message = "RealPass conectado.";
            return true;
        }

        if (HasActiveRead() || !_readLock.Wait(0))
        {
            message = "RealPass volvió al bus USB; esperando que termine la operación anterior para reconectar.";
            return false;
        }

        try
        {
            ReleaseSdkConnection();
            return Initialize(out message);
        }
        finally
        {
            _readLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.WriteLine("[HW REALPASS] Cerrando controlador y conexión física.");
        lock (_callbackLock)
        {
            _activeRead?.TrySetException(new BiometricDeviceException(
                "REALPASS_DISPOSED",
                "El controlador RealPass fue detenido."));
        }

        if (_sdk is not null)
        {
            try
            {
                _sdk.StopDocDetect();
                if (_connected)
                {
                    _sdk.Disconnect();
                }
            }
            finally
            {
                _sdk.Destroy();
                _sdk = null;
                _connected = false;
            }
        }

        _readLock.Dispose();
    }

    private void ConfigureReading(bool readRfid)
    {
        var sdk = _sdk!;
        sdk.m_sConfig.sOcrInfo.nEnable = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.sScanInfo.nEnable = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.sBarcodeInfo.nEnable = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.sCustomDocInfo.nEnable = RealPassSDK.RP_DISABLE;
        sdk.m_sConfig.seDocInfo.nEnable = readRfid
            ? RealPassSDK.RP_ENABLE
            : RealPassSDK.RP_DISABLE;

        sdk.m_sConfig.sScanInfo.nMode = RealPassSDK.RP_SCAN_MODE_NOMAL;
        sdk.m_sConfig.sScanInfo.nIR = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.sScanInfo.nWH = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.sScanInfo.nUV = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.sScanInfo.nEnhanced = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.nRestOnceAtDEnd = RealPassSDK.RP_ENABLE;

        sdk.m_sConfig.sDocDetectInfo.nMode = RealPassSDK.RP_DOC_DETECT_CAM;
        sdk.m_sConfig.sDocDetectInfo.nTimeout = 0;
        sdk.m_sConfig.sDocDetectInfo.nCheckNotRemoved = RealPassSDK.RP_DISABLE;

        sdk.m_sConfig.seDocInfo.sRPeDocSecurity.nBAC = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.seDocInfo.sRPeDocSecurity.nPACE = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.seDocInfo.sRPeDocSecurity.nAA = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.seDocInfo.sRPeDocSecurity.nCA = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.seDocInfo.sRPeDocSecurity.nPA = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.seDocInfo.sRPeDocSecurity.nTA = RealPassSDK.RP_DISABLE;
        sdk.m_sConfig.seDocInfo.nDGx ??= new int[16];
        Array.Fill(sdk.m_sConfig.seDocInfo.nDGx, RealPassSDK.RP_DISABLE);
        sdk.m_sConfig.seDocInfo.nDGx[RealPassSDK.RP_DG1] = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.seDocInfo.nDGx[RealPassSDK.RP_DG2] = RealPassSDK.RP_ENABLE;
        sdk.m_sConfig.seDocInfo.nDGx[RealPassSDK.RP_DG3] = RealPassSDK.RP_DISABLE;
    }

    private void OnEvent(RealPassSDK.EventType eventType)
    {
        try
        {
            switch (eventType)
            {
                case RealPassSDK.EventType.DEVICE_CONNECTED:
                    _connected = true;
                    _lastError = null;
                    Console.WriteLine("[HW CONNECTED REALPASS] Dispositivo físico conectado al SDK.");
                    break;
                case RealPassSDK.EventType.DEVICE_DISCONNECTED:
                    MarkDisconnected("callback del SDK");
                    break;
                case RealPassSDK.EventType.DOC_DETECT_ON:
                    Console.WriteLine("[HW REALPASS] Documento detectado; iniciando OCR, imágenes y lectura configurada.");
                    if (HasActiveRead())
                    {
                        var result = _sdk?.ReadDocument() ?? -1;
                        if (result != RealPassSDK.RP_SUCCESS)
                        {
                            FailActiveRead(
                                "REALPASS_READ_START_FAILED",
                                $"ReadDocument devolvió el código {result}.",
                                result);
                        }
                    }
                    break;
                case RealPassSDK.EventType.DOCUMENT_READING_COMPLETE:
                    Console.WriteLine("[HW REALPASS] Callback DOCUMENT_READING_COMPLETE recibido.");
                    CompleteActiveRead();
                    break;
                case RealPassSDK.EventType.DOC_DETECT_TIMEOUT:
                    Console.WriteLine("[TIMEOUT REALPASS] El SDK agotó la espera de detección documental.");
                    FailActiveRead("REALPASS_DOCUMENT_TIMEOUT", "La detección del documento expiró.");
                    break;
                case RealPassSDK.EventType.DOC_DETECT_ABORT:
                    Console.WriteLine("[WARN REALPASS] El SDK notificó aborto de detección documental.");
                    FailActiveRead("REALPASS_DOCUMENT_ABORTED", "El SDK abortó la detección del documento.");
                    break;
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[ERROR REALPASS] Falló el callback {eventType}: {exception.Message}");
            FailActiveRead("REALPASS_CALLBACK_ERROR", exception.Message);
        }
    }

    private bool RefreshPhysicalPresence(out string message)
    {
        try
        {
            if (UsbDevicePresence.IsPresent(RealPassHardwareId))
            {
                message = "RealPass presente en el bus USB.";
                return true;
            }

            MarkDisconnected("sondeo USB VID_16D1/PID_1107");
            message = _lastError ?? "RealPass no está conectado físicamente.";
            return false;
        }
        catch (Exception exception)
        {
            message = $"No se pudo comprobar la presencia USB del RealPass: {exception.Message}";
            Console.WriteLine($"[HW ERROR REALPASS] {message}");
            return _connected;
        }
    }

    private void MarkDisconnected(string source)
    {
        var wasConnected = _connected;
        _connected = false;
        _lastError = _disposed ? null : "RealPass se desconectó físicamente.";
        if (_disposed)
        {
            Console.WriteLine("[HW INFO REALPASS] Dispositivo cerrado por apagado o reinicio del agente.");
        }
        else if (wasConnected)
        {
            Console.WriteLine($"[HW DISCONNECTED REALPASS] Se perdió la conexión física con el lector; fuente={source}.");
        }

        FailActiveRead(
            "REALPASS_DISCONNECTED",
            "RealPass se desconectó físicamente durante la lectura.");
    }

    private void ReleaseSdkConnection()
    {
        var sdk = _sdk;
        _sdk = null;
        if (sdk is null)
        {
            return;
        }

        try
        {
            sdk.Disconnect();
        }
        catch
        {
            // El dispositivo puede haber desaparecido antes de liberar el SDK.
        }

        try
        {
            sdk.Destroy();
        }
        catch
        {
            // La siguiente inicialización creará una instancia limpia.
        }
    }

    private void OnData(RealPassSDK.DataType dataType, object data)
    {
        if (dataType != RealPassSDK.DataType.SYNTHESIS_RESULT ||
            data is not RealPassSDK.SYNTHESIS_RESULT synthesis)
        {
            return;
        }

        lock (_callbackLock)
        {
            _lastSynthesis = synthesis;
        }
    }

    private void CompleteActiveRead()
    {
        TaskCompletionSource<DocumentCaptureResult>? completion;
        RealPassSDK.SYNTHESIS_RESULT synthesis;
        lock (_callbackLock)
        {
            completion = _activeRead;
            synthesis = _lastSynthesis ?? _sdk?.m_sSynthesisResult ?? default;
        }

        if (completion is null)
        {
            return;
        }

        try
        {
            completion.TrySetResult(BuildResult(synthesis));
        }
        catch (Exception exception)
        {
            completion.TrySetException(new BiometricDeviceException(
                "REALPASS_RESULT_ERROR",
                $"No se pudo construir el resultado documental: {exception.Message}"));
        }
    }

    private DocumentCaptureResult BuildResult(RealPassSDK.SYNTHESIS_RESULT synthesis)
    {
        var mrzLines = new[] { synthesis.strMRZ1, synthesis.strMRZ2, synthesis.strMRZ3 }
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var images = new List<DocumentImageResult>();
        AddSdkImage(images, "ID_IR", RealPassSDK.DataType.IMAGE_ID_IR);
        AddSdkImage(images, "ID_WHITE", RealPassSDK.DataType.IMAGE_ID_WH);
        AddSdkImage(images, "ID_UV", RealPassSDK.DataType.IMAGE_ID_UV);
        AddSdkImage(images, "ID_PORTRAIT", RealPassSDK.DataType.IMAGE_ID_PHOTO);
        AddSdkImage(images, "PASSPORT_IR", RealPassSDK.DataType.IMAGE_IR_TD3);
        AddSdkImage(images, "PASSPORT_WHITE", RealPassSDK.DataType.IMAGE_WH_TD3);
        AddSdkImage(images, "PASSPORT_UV", RealPassSDK.DataType.IMAGE_UV_TD3);
        AddImage(images, "IR", synthesis.bmImgIR);
        AddImage(images, "WHITE", synthesis.bmImgWH);
        AddImage(images, "UV", synthesis.bmImgUV);
        AddImage(images, "OCR", synthesis.bmImgOCR);
        AddImage(images, "PORTRAIT", synthesis.bmImgPHOTO);
        AddImage(images, "EDOC_PORTRAIT", synthesis.bmImgePHOTO);
        AddImage(images, "EDOC_FINGER_1", synthesis.bmImgeFINGER1);
        AddImage(images, "EDOC_FINGER_2", synthesis.bmImgeFINGER2);

        var barcodes = (synthesis.sBarResult.sBarInfo ?? Array.Empty<RealPassSDK.BAR_INFO>())
            .Take(Math.Max(0, synthesis.sBarResult.nBarCnt))
            .Select(barcode => new DocumentBarcodeResult(
                barcode.strBarType ?? barcode.eBarType.ToString(),
                barcode.strBarData ?? string.Empty,
                barcode.nPosLeft,
                barcode.nPosTop,
                barcode.nPosRight,
                barcode.nPosBottom))
            .ToArray();

        var parsedMrz = synthesis.sMRZInfoResult;
        var hasMrz = mrzLines.Length > 0 || !string.IsNullOrWhiteSpace(parsedMrz.strDocNum);
        return new DocumentCaptureResult(
            DocumentType: synthesis.eDocType.ToString(),
            MrzLines: mrzLines,
            Mrz: hasMrz ? new DocumentMrzResult(
                parsedMrz.strDocType,
                parsedMrz.strIssuingState,
                parsedMrz.strSurname,
                parsedMrz.strGivenNames,
                parsedMrz.strName,
                parsedMrz.strDocNum,
                parsedMrz.strNationality,
                parsedMrz.strBirthEx ?? parsedMrz.strBirth,
                parsedMrz.strSex,
                parsedMrz.strExpiryEx ?? parsedMrz.strExpiry,
                parsedMrz.strOptional,
                parsedMrz.bPassNumCDR,
                parsedMrz.bBirthCDR,
                parsedMrz.bExpiryCDR,
                parsedMrz.bCompositeCDR) : null,
            Images: images,
            Barcodes: barcodes,
            ElectronicDocument: BuildElectronicDocument(synthesis));
    }

    private void AddSdkImage(
        ICollection<DocumentImageResult> images,
        string type,
        RealPassSDK.DataType dataType)
    {
        if (_sdk is null)
        {
            return;
        }

        try
        {
            object image = new();
            if (_sdk.GetImage(dataType, ref image) == RealPassSDK.RP_SUCCESS && image is Bitmap bitmap)
            {
                AddImage(images, type, bitmap);
            }
        }
        catch
        {
            // Algunas versiones del SDK lanzan una excepción cuando el tipo de
            // imagen no aplica al documento. La imagen cruda sigue disponible.
        }
    }

    private static ElectronicDocumentResult? BuildElectronicDocument(
        RealPassSDK.SYNTHESIS_RESULT synthesis)
    {
        if (synthesis.eDocType is not (
            RealPassSDK.DocType.E_PASSPORT or
            RealPassSDK.DocType.E_ID_CARD or
            RealPassSDK.DocType.E_DOCUMENT))
        {
            return null;
        }

        var rfid = synthesis.sRFIDResult;
        var dataGroups = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rfid.eDGx is not null)
        {
            for (var index = 0; index < rfid.eDGx.Length; index++)
            {
                dataGroups[$"DG{index + 1}"] = rfid.eDGx[index].ToString();
            }
        }

        return new ElectronicDocumentResult(
            rfid.eBAC.ToString(),
            rfid.ePACE.ToString(),
            rfid.eAA.ToString(),
            rfid.eCA.ToString(),
            rfid.ePA.ToString(),
            rfid.eTA.ToString(),
            dataGroups);
    }

    private static void AddImage(
        ICollection<DocumentImageResult> images,
        string type,
        Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            return;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        images.Add(new DocumentImageResult(
            type,
            "image/png",
            Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length)),
            bitmap.Width,
            bitmap.Height));
    }

    private bool HasActiveRead()
    {
        lock (_callbackLock)
        {
            return _activeRead is not null;
        }
    }

    private void FailActiveRead(string code, string message, int? nativeCode = null)
    {
        lock (_callbackLock)
        {
            _activeRead?.TrySetException(new BiometricDeviceException(code, message, nativeCode));
        }
    }

    private static void ThrowOnSdkError(int result, string context)
    {
        if (result != RealPassSDK.RP_SUCCESS)
        {
            throw new BiometricDeviceException(
                "REALPASS_NATIVE_ERROR",
                $"{context} Código RealPass: {result}.",
                result);
        }
    }
}
