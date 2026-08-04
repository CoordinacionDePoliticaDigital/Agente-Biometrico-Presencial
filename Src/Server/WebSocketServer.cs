using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgenteBiometricoPresencial.Configuration;
using AgenteBiometricoPresencial.Contracts;
using AgenteBiometricoPresencial.Drivers;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AgenteBiometricoPresencial.Server;

public sealed class BiometricWebSocketServer : IDisposable
{
    private const string AgentVersion = "0.6.1";
    private static long _globalConnectionSequence;
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Include
    };
    private readonly RealScanDriver _realScanDriver = new();
    private readonly RealPassDriver _realPassDriver = new();
    private readonly ConcurrentDictionary<long, IWebSocketConnection> _connections = new();
    private WebSocketServer? _server;
    private System.Threading.Timer? _hardwareMonitor;
    private int _activeConnections;
    private int _healthProbeRunning;
    private bool _lastRealScanConnected;
    private bool _lastRealPassConnected;
    private bool _disposed;

    public event Action<DeviceState, DeviceState>? HardwareStatusChanged;

    public bool AllDevicesConnected =>
        _realScanDriver.State.Connected && _realPassDriver.State.Connected;

    public void Start()
    {
        var options = AgentOptions.FromEnvironment();
        FleckLog.Level = LogLevel.Info;

        var scheme = options.UseTls ? "wss" : "ws";
        _server = new WebSocketServer($"{scheme}://127.0.0.1:{options.Port}");
        if (options.UseTls)
        {
            _server.Certificate = new X509Certificate2(
                options.CertificatePath!,
                options.CertificatePassword);
        }
        else
        {
            Console.WriteLine(
                "[SECURITY WARNING] TLS no configurado. Define BIOMETRIC_AGENT_CERT_PATH " +
                "para habilitar wss://.");
        }

        InitializeDevices();
        _lastRealScanConnected = _realScanDriver.State.Connected;
        _lastRealPassConnected = _realPassDriver.State.Connected;
        HardwareStatusChanged?.Invoke(_realScanDriver.State, _realPassDriver.State);
        _hardwareMonitor = new System.Threading.Timer(
            _ => MonitorHardware(),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
        _server.Start(ConfigureConnection);
        Console.WriteLine($"[INFO] Agente escuchando exclusivamente en {scheme}://127.0.0.1:{options.Port}.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.WriteLine($"[INFO] Deteniendo canal WebSocket; conexiones activas: {Volatile.Read(ref _activeConnections)}.");
        _hardwareMonitor?.Dispose();
        _hardwareMonitor = null;
        foreach (var connection in _connections.Values)
        {
            try
            {
                connection.Close();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[WS WARNING] No se pudo cerrar limpiamente un canal: {exception.Message}");
            }
        }

        _connections.Clear();
        _server?.Dispose();
        _realPassDriver.Dispose();
        _realScanDriver.Dispose();
        Console.WriteLine("[INFO] Canal WebSocket y controladores Xperix detenidos.");
    }

    private void ConfigureConnection(IWebSocketConnection socket)
    {
        var connectionId = Interlocked.Increment(ref _globalConnectionSequence);
        var closed = 0;
        socket.OnOpen = () =>
        {
            _connections[connectionId] = socket;
            var active = Interlocked.Increment(ref _activeConnections);
            var connectionKind = connectionId == 1 ? "conexión inicial" : "conexión/reconexión";
            Console.WriteLine(
                $"[WS CONNECTED] Canal #{connectionId} abierto ({connectionKind}); " +
                $"origen={socket.ConnectionInfo.ClientIpAddress}; activos={active}.");
            Send(socket, new
            {
                @event = "CONNECTED_HANDSHAKE",
                status = "READY",
                agentVersion = AgentVersion,
                stationName = Environment.MachineName.ToUpperInvariant(),
                devices = GetDeviceStates()
            });
        };

        socket.OnClose = () =>
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
            {
                return;
            }

            var active = Math.Max(0, Interlocked.Decrement(ref _activeConnections));
            _connections.TryRemove(connectionId, out _);
            Console.WriteLine($"[WS DISCONNECTED] Canal #{connectionId} cerrado; activos={active}.");
        };
        socket.OnError = exception =>
            Console.WriteLine($"[WS ERROR] Canal #{connectionId}: {exception.GetType().Name}: {exception.Message}");
        socket.OnMessage = message => _ = ProcessMessageAsync(socket, message);
    }

    private async Task ProcessMessageAsync(IWebSocketConnection socket, string rawJson)
    {
        BiometricCommand? request;
        try
        {
            request = JsonConvert.DeserializeObject<BiometricCommand>(rawJson);
        }
        catch (JsonException exception)
        {
            Console.WriteLine($"[WS WARNING] Mensaje rechazado por JSON inválido: {exception.Message}");
            SendError(socket, null, "INVALID_JSON", $"JSON inválido: {exception.Message}");
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Command))
        {
            Console.WriteLine("[WS WARNING] Mensaje rechazado: falta el campo command.");
            SendError(socket, request?.SessionId, "INVALID_COMMAND", "El campo command es obligatorio.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            Console.WriteLine("[WS WARNING] Mensaje rechazado: falta sessionId.");
            SendError(socket, null, "INVALID_SESSION", "El campo sessionId es obligatorio.");
            return;
        }

        if (request.Command.Equals("PING", StringComparison.OrdinalIgnoreCase))
        {
            Send(socket, new
            {
                @event = "PONG",
                sessionId = request.SessionId,
                status = "SUCCESS"
            });
            return;
        }

        var operationTimer = Stopwatch.StartNew();
        var configuredTimeout = request.TimeoutSeconds is int requestedTimeout
            ? $"{requestedTimeout}s"
            : "predeterminado";
        Console.WriteLine(
            $"[WS COMMAND] {request.Command.ToUpperInvariant()} recibido; " +
            $"timeout={configuredTimeout}.");
        try
        {
            switch (request.Command.ToUpperInvariant())
            {
                case "GET_DEVICE_STATUS":
                    Console.WriteLine("[WS INFO] Entregando estado actual de RealScan y RealPass.");
                    Send(socket, new
                    {
                        @event = "DEVICE_STATUS",
                        sessionId = request.SessionId,
                        status = "SUCCESS",
                        devices = GetDeviceStates()
                    });
                    break;

                case "START_FINGERPRINT_CAPTURE":
                    await CaptureFingerprintAsync(socket, request);
                    break;

                case "START_DOCUMENT_SCAN":
                    await CaptureDocumentAsync(socket, request);
                    break;

                case "LIST_REMOVABLE_DRIVES":
                    ListRemovableDrives(socket, request.SessionId);
                    break;

                case "SAVE_PRIVATE_KEY_TO_USB":
                    SavePrivateKeyToUsb(socket, request);
                    break;

                default:
                    Console.WriteLine($"[WS WARNING] Comando no reconocido: {request.Command}.");
                    SendError(
                        socket,
                        request.SessionId,
                        "UNKNOWN_COMMAND",
                        $"Comando no reconocido: '{request.Command}'.");
                    break;
            }

            Console.WriteLine(
                $"[WS SUCCESS] {request.Command.ToUpperInvariant()} procesado en {operationTimer.ElapsedMilliseconds} ms.");
        }
        catch (BiometricDeviceException exception)
        {
            var level = exception.ErrorCode.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)
                ? "TIMEOUT"
                : "ERROR";
            Console.WriteLine(
                $"[{level}] {request.Command} terminó en {operationTimer.ElapsedMilliseconds} ms: " +
                $"{exception.ErrorCode}; {exception.Message}");
            SendError(
                socket,
                request.SessionId,
                exception.ErrorCode,
                exception.Message,
                exception.NativeCode);
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"[ERROR] {request.Command} falló en {operationTimer.ElapsedMilliseconds} ms: {exception}");
            SendError(
                socket,
                request.SessionId,
                "INTERNAL_ERROR",
                "El agente no pudo completar la operación.");
        }
    }

    private static DriveInfo[] GetReadyRemovableDrives() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady)
        .ToArray();

    private static void ListRemovableDrives(IWebSocketConnection socket, string sessionId)
    {
        var drives = GetReadyRemovableDrives();
        Console.WriteLine($"[SECURITY USB] Unidades removibles listas: {drives.Length}.");
        Send(socket, new
        {
            @event = "REMOVABLE_DRIVES",
            sessionId,
            status = "SUCCESS",
            data = drives.Select(drive => new
            {
                name = drive.Name,
                volumeLabel = drive.VolumeLabel,
                totalSize = drive.TotalSize,
                availableFreeSpace = drive.AvailableFreeSpace,
                driveFormat = drive.DriveFormat
            })
        });
    }

    private static void SavePrivateKeyToUsb(IWebSocketConnection socket, BiometricCommand request)
    {
        var key = request.PrivateKeyPem ?? string.Empty;
        if (!key.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal) ||
            !key.Contains("-----END ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new BiometricDeviceException(
                "INVALID_PRIVATE_KEY",
                "La llave privada debe estar cifrada y en formato PKCS#8 PEM.");
        }

        var requestedDrive = request.DriveName ?? string.Empty;
        var drive = GetReadyRemovableDrives().FirstOrDefault(candidate =>
            candidate.Name.Equals(requestedDrive, StringComparison.OrdinalIgnoreCase));
        if (drive is null)
        {
            throw new BiometricDeviceException(
                "REMOVABLE_DRIVE_NOT_FOUND",
                "La memoria USB seleccionada no está disponible. Reconéctela y vuelva a consultar las unidades.");
        }

        var safeFileName = Path.GetFileName(request.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = "solicitante.key";
        }
        if (!safeFileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
        {
            safeFileName += ".key";
        }

        var target = Path.Combine(drive.RootDirectory.FullName, safeFileName);
        if (File.Exists(target))
        {
            throw new BiometricDeviceException(
                "PRIVATE_KEY_ALREADY_EXISTS",
                $"Ya existe {safeFileName} en la memoria USB. Retírelo mediante el procedimiento autorizado o use el folio correcto; el agente no sobrescribe llaves privadas.");
        }
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(key);
        using (var stream = new FileStream(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        var persisted = File.ReadAllBytes(target);
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(bytes),
            SHA256.HashData(persisted)))
        {
            throw new BiometricDeviceException(
                "PRIVATE_KEY_VERIFICATION_FAILED",
                "La llave fue escrita, pero no pudo verificarse íntegramente en la memoria USB.");
        }

        Console.WriteLine(
            $"[SECURITY USB] Llave privada cifrada guardada y verificada en unidad removible; " +
            $"archivo={safeFileName}; bytes={bytes.Length}. El contenido no se registró en logs.");
        Send(socket, new
        {
            @event = "PRIVATE_KEY_SAVED",
            sessionId = request.SessionId,
            status = "SUCCESS",
            data = new
            {
                driveName = drive.Name,
                volumeLabel = drive.VolumeLabel,
                fileName = safeFileName,
                bytesWritten = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            }
        });
    }

    private async Task CaptureFingerprintAsync(
        IWebSocketConnection socket,
        BiometricCommand request)
    {
        var fingerType = request.FingerType ?? string.Empty;
        var timeoutSeconds = request.TimeoutSeconds ?? 30;
        _ = RealScanDriver.ResolveCaptureProfile(fingerType);
        var missingFingers = request.MissingFingers ?? Array.Empty<string>();
        var captureTimer = Stopwatch.StartNew();
        Console.WriteLine(
            $"[HW REALSCAN] Iniciando captura {fingerType}; timeout={timeoutSeconds}s; " +
            $"dedos ausentes declarados={missingFingers.Count}.");

        Send(socket, new
        {
            @event = "FINGERPRINT_CAPTURE_STARTED",
            sessionId = request.SessionId,
            status = "IN_PROGRESS",
            data = new { fingerType, missingFingers, timeoutSeconds }
        });

        var result = await _realScanDriver.CaptureAsync(
            fingerType,
            missingFingers,
            timeoutSeconds,
            CancellationToken.None);

        Console.WriteLine(
            $"[SUCCESS REALSCAN] Captura {fingerType} completada en {captureTimer.ElapsedMilliseconds} ms; " +
            $"segmentos={result.Samples.Count}; peor NFIQ={result.Samples.Max(sample => sample.NfiqQuality)}; " +
            $"advertencias={result.Warnings.Count}.");

        Send(socket, new
        {
            @event = "FINGERPRINT_CAPTURED",
            sessionId = request.SessionId,
            status = "SUCCESS",
            data = new
            {
                fingerType = result.FingerType,
                missingFingers = result.MissingFingers,
                slap = new
                {
                    wsqBase64 = result.SlapWsqBase64,
                    previewPngBase64 = result.SlapPreviewPngBase64,
                    imageWidth = result.SlapImageWidth,
                    imageHeight = result.SlapImageHeight
                },
                samples = result.Samples,
                warnings = result.Warnings
            }
        });
    }

    private async Task CaptureDocumentAsync(
        IWebSocketConnection socket,
        BiometricCommand request)
    {
        var timeoutSeconds = request.TimeoutSeconds ?? 60;
        var readRfid = request.ReadRfid ?? true;
        var documentSide = request.DocumentSide?.ToUpperInvariant() switch
        {
            "BACK" => "BACK",
            _ => "FRONT"
        };
        var scanTimer = Stopwatch.StartNew();
        Console.WriteLine(
            $"[HW REALPASS] Iniciando lectura {documentSide}; timeout={timeoutSeconds}s; " +
            $"RFID={(readRfid ? "habilitado" : "omitido")}.");
        Send(socket, new
        {
            @event = "DOCUMENT_SCAN_STARTED",
            sessionId = request.SessionId,
            status = "IN_PROGRESS",
            data = new { readRfid, timeoutSeconds, documentSide }
        });

        var rawResult = await _realPassDriver.ReadDocumentAsync(
            readRfid,
            timeoutSeconds,
            CancellationToken.None);
        var result = DocumentImageProcessor.Process(rawResult, documentSide);
        result = DocumentMrzFallbackProcessor.Enrich(result, documentSide);
        var sideValidation = ValidateDocumentSide(result, documentSide);
        var eligibilityValidation = ValidateDocumentEligibility(result, documentSide);
        if (!sideValidation.Accepted)
        {
            Console.WriteLine(
                $"[WARN DOCUMENT] Lado rechazado: esperado={sideValidation.ExpectedSide}; " +
                $"detectado={sideValidation.DetectedSide}; confianza={sideValidation.Confidence}.");
        }

        if (!eligibilityValidation.Accepted)
        {
            Console.WriteLine(
                $"[WARN DOCUMENT] Documento rechazado: {eligibilityValidation.Category}; " +
                $"estado={eligibilityValidation.Status}.");
        }
        Console.WriteLine(
            $"[SUCCESS REALPASS] Lectura {documentSide} completada en {scanTimer.ElapsedMilliseconds} ms; " +
            $"tipo={result.DocumentType}; imágenes={result.Images.Count}; MRZ={(result.Mrz is null ? "no" : "sí")}; " +
            $"códigos={result.Barcodes.Count}; tipos=[{string.Join(",", result.Images.Select(image => image.Type).Distinct())}]; " +
            $"elegibilidad={eligibilityValidation.Status}/{eligibilityValidation.Category}.");
        Send(socket, new
        {
            @event = "DOCUMENT_SCANNED",
            sessionId = request.SessionId,
            status = "SUCCESS",
            data = new
            {
                documentSide,
                sideValidation,
                eligibilityValidation,
                result.DocumentType,
                result.MrzLines,
                result.Mrz,
                result.Images,
                result.Barcodes,
                result.ElectronicDocument,
                result.Orientation
            }
        });
    }

    private static bool HasMeaningfulMrz(DocumentCaptureResult result)
    {
        return result.MrzLines.Any(line => !string.IsNullOrWhiteSpace(line) && line.Length >= 25) ||
               (result.Mrz is not null &&
                !string.IsNullOrWhiteSpace(result.Mrz.IssuingState) &&
                !string.IsNullOrWhiteSpace(result.Mrz.DocumentNumber));
    }

    private static DocumentEligibilityValidation ValidateDocumentEligibility(
        DocumentCaptureResult result,
        string expectedSide)
    {
        var evidence = new List<string>();
        var documentType = result.DocumentType ?? string.Empty;
        var normalizedType = documentType.ToUpperInvariant();
        var hasMrz = HasMeaningfulMrz(result);
        var isPassport = normalizedType.Contains("PASSPORT", StringComparison.Ordinal);
        var isDriverLicense = normalizedType.Contains("ISO18013", StringComparison.Ordinal) ||
                              normalizedType.Contains("DRIVER", StringComparison.Ordinal) ||
                              normalizedType.Contains("LICENSE", StringComparison.Ordinal) ||
                              normalizedType.Contains("LICENCE", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            evidence.Add($"Clasificación RealPass: {documentType}");
        }

        if (isDriverLicense)
        {
            evidence.Add("Documento clasificado como licencia de conducir");
            return new DocumentEligibilityValidation(
                "REJECTED",
                "DRIVER_LICENSE",
                false,
                evidence,
                "Documento no admitido: las licencias de conducir no son válidas para este enrolamiento. Presente INE/IFE, pasaporte o documento migratorio INM mexicano.");
        }

        if (isPassport)
        {
            evidence.Add("Pasaporte reconocido por RealPass");
            return new DocumentEligibilityValidation(
                "ACCEPTED",
                "PASSPORT",
                true,
                evidence,
                "Pasaporte admitido; la MRZ y su integridad se validan por separado.");
        }

        if (hasMrz)
        {
            evidence.Add("MRZ ICAO 9303 detectada");
            var issuingState = result.Mrz?.IssuingState;
            if (!string.IsNullOrWhiteSpace(issuingState))
            {
                evidence.Add($"Estado emisor: {issuingState}");
            }

            if (!string.IsNullOrWhiteSpace(issuingState) &&
                !issuingState.Equals("MEX", StringComparison.OrdinalIgnoreCase))
            {
                return new DocumentEligibilityValidation(
                    "REJECTED",
                    "FOREIGN_ID_CARD",
                    false,
                    evidence,
                    "Documento no admitido: para tarjetas de identidad o residencia se requiere un documento mexicano emitido por INE/IFE o INM.");
            }

            return new DocumentEligibilityValidation(
                "ACCEPTED",
                "MEXICAN_ID_OR_INM",
                true,
                evidence,
                "Documento mexicano con MRZ admitido; falta completar la validación de integridad y cotejo de identidad.");
        }

        if (expectedSide == "FRONT")
        {
            evidence.Add("El frente no produjo MRZ; se espera confirmación del reverso");
            return new DocumentEligibilityValidation(
                "PENDING",
                "MEXICAN_CARD_PENDING",
                true,
                evidence,
                "Frente aceptado provisionalmente. El reverso debe confirmar una INE/IFE o identificación migratoria INM mediante MRZ.");
        }

        if (result.Orientation?.Rotation == 180)
        {
            evidence.Add("El reverso fue adquirido a 180° y se orientó automáticamente");
            return new DocumentEligibilityValidation(
                "RECAPTURE_REQUIRED",
                "OCR_ORIENTATION_FAILED",
                false,
                evidence,
                "El reverso fue orientado automáticamente, pero RealPass no produjo MRZ. Recapture únicamente el reverso con el texto erguido para validar la identidad.");
        }

        evidence.Add("La captura completa no produjo MRZ ICAO 9303");
        return new DocumentEligibilityValidation(
            "REJECTED",
            "UNSUPPORTED_DOCUMENT",
            false,
            evidence,
            "Documento no admitido: no se confirmó una INE/IFE, pasaporte o identificación migratoria INM. Las licencias de conducir no son válidas para este enrolamiento.");
    }

    private static DocumentSideValidation ValidateDocumentSide(
        DocumentCaptureResult result,
        string expectedSide)
    {
        var evidence = new List<string>();
        var isPassport = result.DocumentType.Contains("PASSPORT", StringComparison.OrdinalIgnoreCase);
        var hasMrz = HasMeaningfulMrz(result);
        var hasBarcode = result.Barcodes.Count > 0;
        var hasConfirmedPortrait = result.Images.Any(image => image.Type == "PORTRAIT_FACE");
        var hasAuxiliaryPortrait = result.Images.Any(image => image.Type is
            "PORTRAIT_TEMPLATE" or
            "ID_PORTRAIT" or
            "PORTRAIT" or
            "EDOC_PORTRAIT");
        var hasStrongBackEvidence = hasMrz && hasBarcode;

        if (isPassport) evidence.Add("Documento clasificado como pasaporte");
        if (hasMrz) evidence.Add("MRZ detectada");
        if (hasBarcode) evidence.Add($"{result.Barcodes.Count} código(s) detectado(s)");
        if (hasConfirmedPortrait) evidence.Add("Rostro confirmado en la imagen visible");
        if (hasAuxiliaryPortrait) evidence.Add("Retrato auxiliar entregado por el SDK");
        if (hasStrongBackEvidence) evidence.Add("MRZ y código confirman el reverso");

        var detectedSide = isPassport
            ? "FRONT"
            : hasStrongBackEvidence
                ? "BACK"
                : hasConfirmedPortrait
                ? "FRONT"
                : hasMrz || hasBarcode
                    ? "BACK"
                    : hasAuxiliaryPortrait
                        ? "FRONT"
                    : "UNKNOWN";
        var accepted = expectedSide switch
        {
            // UNKNOWN is not an approval: it is allowed through so the
            // eligibility layer can return the precise unsupported/recapture
            // decision. Strong FRONT evidence still rejects a requested back.
            "BACK" => detectedSide is "BACK" or "UNKNOWN",
            "FRONT" => detectedSide != "BACK",
            _ => false
        };
        var confidence = detectedSide == "UNKNOWN" ? "LOW" : "HIGH";
        var message = accepted
            ? detectedSide == "UNKNOWN"
                ? expectedSide == "FRONT"
                    ? "Frente aceptado provisionalmente; el OCR México debe confirmar el modelo y los campos."
                    : "Reverso sin señal concluyente; la elegibilidad determinará si requiere recaptura o rechazo."
                : $"Lado {detectedSide.ToLowerInvariant()} confirmado."
            : $"Se esperaba {expectedSide.ToLowerInvariant()}, pero las señales corresponden a {detectedSide.ToLowerInvariant()}.";

        return new DocumentSideValidation(
            expectedSide,
            detectedSide,
            accepted,
            confidence,
            evidence,
            message);
    }

    private object GetDeviceStates() => new
    {
        realScanG10 = _realScanDriver.State,
        realPassRPNF = _realPassDriver.State
    };

    private void InitializeDevices()
    {
        Console.WriteLine(
            _realScanDriver.Initialize(out var realScanMessage)
                ? $"[RealScan G10] {realScanMessage}"
                : $"[RealScan G10 WARNING] {realScanMessage}");

        Console.WriteLine(
            _realPassDriver.Initialize(out var realPassMessage)
                ? $"[RealPass RPNF] {realPassMessage}"
                : $"[RealPass RPNF WARNING] {realPassMessage}");
    }

    private void MonitorHardware()
    {
        if (_disposed || Interlocked.Exchange(ref _healthProbeRunning, 1) != 0)
        {
            return;
        }

        try
        {
            var stateChanged = false;
            var realScanConnected = _realScanDriver.RefreshConnection(out var realScanMessage);
            if (realScanConnected != _lastRealScanConnected)
            {
                _lastRealScanConnected = realScanConnected;
                stateChanged = true;
                Console.WriteLine(realScanConnected
                    ? $"[HW CONNECTED REALSCAN] Dispositivo recuperado automáticamente. {realScanMessage}"
                    : $"[HW DISCONNECTED REALSCAN] El sondeo periódico perdió el dispositivo. {realScanMessage}");
            }

            var realPassConnected = _realPassDriver.RefreshConnection(out var realPassMessage);
            if (realPassConnected != _lastRealPassConnected)
            {
                _lastRealPassConnected = realPassConnected;
                stateChanged = true;
                Console.WriteLine(realPassConnected
                    ? $"[HW CONNECTED REALPASS] Dispositivo recuperado automáticamente. {realPassMessage}"
                    : $"[HW DISCONNECTED REALPASS] El sondeo USB perdió el dispositivo. {realPassMessage}");
            }

            if (stateChanged)
            {
                HardwareStatusChanged?.Invoke(_realScanDriver.State, _realPassDriver.State);
                Broadcast(new
                {
                    @event = "DEVICE_STATUS_CHANGED",
                    status = "SUCCESS",
                    devices = GetDeviceStates()
                });
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[HW ERROR REALSCAN] Falló el sondeo periódico: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _healthProbeRunning, 0);
        }
    }

    private void Broadcast(object payload)
    {
        var json = JsonConvert.SerializeObject(payload, JsonSettings);
        foreach (var connection in _connections.Values)
        {
            if (connection.IsAvailable)
            {
                connection.Send(json);
            }
        }
    }

    private static void Send(IWebSocketConnection socket, object payload) =>
        socket.Send(JsonConvert.SerializeObject(payload, JsonSettings));

    private static void SendError(
        IWebSocketConnection socket,
        string? sessionId,
        string code,
        string message,
        int? nativeCode = null) =>
        Send(socket, new
        {
            @event = "ERROR",
            sessionId,
            status = "ERROR",
            error = new { code, message, nativeCode }
        });
}
