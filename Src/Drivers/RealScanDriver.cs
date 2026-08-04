using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgenteBiometricoPresencial.Contracts;

namespace AgenteBiometricoPresencial.Drivers;

/// <summary>
/// Acceso serializado al SDK nativo Xperix RealScan. Cada captura devuelve la
/// plancha y las huellas segmentadas, etiquetadas y procesadas individualmente.
/// </summary>
public sealed class RealScanDriver : IDisposable
{
    private const int MaximumSegmentedFingers = 4;
    private const int IsoTemplateBufferSize = 16 * 1024;
    private const float WsqCompressionRatio = 0.75f;

    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private int _deviceHandle;
    private bool _sdkInitialized;
    private bool _disposed;
    private string? _lastError;
    private string? _serialNumber;
    private string? _productName;
    private string? _firmwareVersion;
    private string? _hardwareVersion;

    public DeviceState State => new(
        Available: File.Exists(RealScanNative.DefaultDllPath),
        Connected: _deviceHandle != 0,
        SerialNumber: _serialNumber,
        LastError: _lastError,
        ProductName: _productName,
        FirmwareVersion: _firmwareVersion,
        HardwareVersion: _hardwareVersion);

    public bool Initialize(out string message)
    {
        if (_disposed)
        {
            message = "El controlador RealScan ya fue liberado.";
            return false;
        }

        if (_deviceHandle != 0)
        {
            message = "RealScan G10 ya está inicializado.";
            return true;
        }

        try
        {
            var deviceCount = 1;
            var result = RealScanNative.Success;
            if (!_sdkInitialized)
            {
                deviceCount = 0;
                result = RealScanNative.RS_InitSDK([0], 0, ref deviceCount);
                if (result != RealScanNative.Success)
                {
                    return Fail(result, "No fue posible inicializar el SDK RealScan.", out message);
                }

                _sdkInitialized = true;
            }

            if (deviceCount < 1)
            {
                RealScanNative.RS_ExitAllDevices();
                _sdkInitialized = false;
                _lastError = "No se detectaron dispositivos RealScan conectados.";
                message = _lastError;
                return false;
            }

            result = RealScanNative.RS_InitDevice(0, ref _deviceHandle);
            if (result != RealScanNative.Success)
            {
                return Fail(result, "No fue posible abrir el primer dispositivo RealScan.", out message);
            }

            ReadDeviceInfo();
            result = RealScanNative.RS_SetLFDLevel(_deviceHandle, RealScanNative.LfdOn);
            if (result != RealScanNative.Success)
            {
                CloseDevice();
                return Fail(
                    result,
                    "El dispositivo no pudo habilitar la detección de dedo vivo (LFD).",
                    out message);
            }

            _lastError = null;
            message = $"RealScan inicializado. Modelo: {_productName ?? "desconocido"}; " +
                      $"serie: {_serialNumber ?? "desconocida"}; LFD habilitado.";
            return true;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException)
        {
            CloseDevice();
            _lastError = $"No se pudo cargar el SDK RealScan x64: {exception.Message}";
            message = _lastError;
            return false;
        }
    }

    public async Task<FingerprintCaptureResult> CaptureAsync(
        string fingerType,
        IReadOnlyCollection<string>? missingFingers,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (_deviceHandle == 0)
        {
            throw new BiometricDeviceException(
                "REALSCAN_NOT_CONNECTED",
                _lastError ?? "El dispositivo RealScan no está conectado.");
        }

        if (timeoutSeconds is < 1 or > 120)
        {
            throw new BiometricDeviceException(
                "INVALID_TIMEOUT",
                "timeoutSeconds debe estar entre 1 y 120.");
        }

        var profile = ResolveCaptureProfile(fingerType);
        var normalizedMissing = NormalizeMissingFingers(profile, missingFingers);
        var minimumFingers = profile.ExpectedPositions.Count - normalizedMissing.Count;
        if (minimumFingers < 1)
        {
            throw new BiometricDeviceException(
                "INVALID_MISSING_FINGERS",
                "Una captura no puede declarar ausentes todos los dedos de la plancha.");
        }

        var operationTimer = Stopwatch.StartNew();
        await _captureLock.WaitAsync(cancellationToken);
        try
        {
            ThrowOnNativeError(
                RealScanNative.RS_SetCaptureMode(_deviceHandle, profile.CaptureMode, 0, true),
                $"No se pudo configurar el modo {profile.Name}.");
            ThrowOnNativeError(
                RealScanNative.RS_SetMinimumFinger(_deviceHandle, minimumFingers),
                "No se pudo configurar el número mínimo de dedos.");

            using var cancellationRegistration = cancellationToken.Register(
                () => RealScanNative.RS_AbortCapture(_deviceHandle));

            var result = await Task.Run(
                () => TakeAndProcess(profile, normalizedMissing, timeoutSeconds),
                CancellationToken.None);
            Console.WriteLine(
                $"[SUCCESS REALSCAN] SDK completó {profile.Name} en {operationTimer.ElapsedMilliseconds} ms.");
            return result;
        }
        catch (BiometricDeviceException exception)
        {
            MarkDisconnectedIfHardwareFailure(exception.NativeCode);
            Console.WriteLine(
                exception.ErrorCode.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)
                    ? $"[TIMEOUT REALSCAN] {profile.Name} agotó {operationTimer.ElapsedMilliseconds} ms: {exception.Message}"
                    : $"[ERROR REALSCAN] {profile.Name} falló en {operationTimer.ElapsedMilliseconds} ms: {exception.ErrorCode}; {exception.Message}");
            throw;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public bool RefreshConnection(out string message)
    {
        if (_disposed)
        {
            message = "El controlador RealScan está detenido.";
            return false;
        }

        if (_deviceHandle == 0)
        {
            return Initialize(out message);
        }

        if (!_captureLock.Wait(0))
        {
            message = "RealScan ocupado en una captura.";
            return true;
        }

        try
        {
            var info = CreateDeviceInfo();
            var result = RealScanNative.RS_GetDeviceInfo(_deviceHandle, ref info);
            if (result == RealScanNative.Success)
            {
                message = "RealScan conectado.";
                return true;
            }

            MarkDisconnectedIfHardwareFailure(result);
            message = $"Sondeo RealScan falló: {GetNativeError(result)} (código {result}).";
            return _deviceHandle != 0;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.WriteLine("[HW REALSCAN] Cerrando dispositivo y SDK.");
        CloseDevice();
        if (_sdkInitialized)
        {
            RealScanNative.RS_ExitAllDevices();
            _sdkInitialized = false;
        }

        _captureLock.Dispose();
    }

    internal static CaptureProfile ResolveCaptureProfile(string fingerType) =>
        fingerType.ToUpperInvariant() switch
        {
            "SLAP_4_LEFT" => new(
                "SLAP_4_LEFT",
                RealScanNative.CaptureFlatLeftFourFingers,
                RealScanNative.SlapLeftFour,
                ["LEFT_LITTLE", "LEFT_RING", "LEFT_MIDDLE", "LEFT_INDEX"]),
            "SLAP_4_RIGHT" => new(
                "SLAP_4_RIGHT",
                RealScanNative.CaptureFlatRightFourFingers,
                RealScanNative.SlapRightFour,
                ["RIGHT_INDEX", "RIGHT_MIDDLE", "RIGHT_RING", "RIGHT_LITTLE"]),
            "THUMBS_2" => new(
                "THUMBS_2",
                RealScanNative.CaptureFlatTwoFingers,
                RealScanNative.SlapTwoThumbs,
                ["LEFT_THUMB", "RIGHT_THUMB"]),
            _ => throw new BiometricDeviceException(
                "INVALID_FINGER_TYPE",
                $"fingerType no soportado: '{fingerType}'.")
        };

    private FingerprintCaptureResult TakeAndProcess(
        CaptureProfile profile,
        IReadOnlyList<string> missingFingers,
        int timeoutSeconds)
    {
        var slapImage = IntPtr.Zero;
        var slapWidth = 0;
        var slapHeight = 0;
        var slapInfoPointer = IntPtr.Zero;
        var fingerPointerArray = IntPtr.Zero;
        var fingerWidthsPointer = IntPtr.Zero;
        var fingerHeightsPointer = IntPtr.Zero;
        var fingerPointers = new List<IntPtr>();

        try
        {
            var captureResult = RealScanNative.RS_TakeImageData(
                _deviceHandle,
                checked(timeoutSeconds * 1000),
                ref slapImage,
                ref slapWidth,
                ref slapHeight);
            ThrowCaptureError(captureResult);

            if (slapImage == IntPtr.Zero || slapWidth <= 0 || slapHeight <= 0)
            {
                throw new BiometricDeviceException(
                    "REALSCAN_EMPTY_IMAGE",
                    "RealScan devolvió una imagen vacía.");
            }

            var numberOfFingers = 0;
            var segmentationResult = missingFingers.Count == 0
                ? RealScanNative.RS_Segment(
                    slapImage,
                    slapWidth,
                    slapHeight,
                    profile.SlapType,
                    ref numberOfFingers,
                    ref slapInfoPointer,
                    ref fingerPointerArray,
                    ref fingerWidthsPointer,
                    ref fingerHeightsPointer)
                : SegmentWithMissingFingers(
                    profile,
                    missingFingers,
                    slapImage,
                    slapWidth,
                    slapHeight,
                    ref numberOfFingers,
                    ref slapInfoPointer,
                    ref fingerPointerArray,
                    ref fingerWidthsPointer,
                    ref fingerHeightsPointer);

            ThrowSegmentationError(segmentationResult, missingFingers);
            var expectedCount = profile.ExpectedPositions.Count - missingFingers.Count;
            if (numberOfFingers != expectedCount ||
                numberOfFingers is < 1 or > MaximumSegmentedFingers)
            {
                throw new BiometricDeviceException(
                    "REALSCAN_UNEXPECTED_FINGER_COUNT",
                    $"Se esperaban {expectedCount} dedos y el SDK segmentó {numberOfFingers}.");
            }

            var lfdResult = ReadLiveness(numberOfFingers);
            var samples = new List<FingerprintSample>(numberOfFingers);
            var warnings = new List<string>();
            var observedPositions = new HashSet<string>(StringComparer.Ordinal);
            var expectedPositions = profile.ExpectedPositions
                .Where(position => !missingFingers.Contains(position, StringComparer.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            var slapInfoSize = Marshal.SizeOf<RealScanNative.SlapInfo>();

            for (var index = 0; index < numberOfFingers; index++)
            {
                var fingerImage = Marshal.ReadIntPtr(fingerPointerArray, index * IntPtr.Size);
                fingerPointers.Add(fingerImage);
                var fingerWidth = Marshal.ReadInt32(fingerWidthsPointer, index * sizeof(int));
                var fingerHeight = Marshal.ReadInt32(fingerHeightsPointer, index * sizeof(int));
                var slapInfo = Marshal.PtrToStructure<RealScanNative.SlapInfo>(
                    IntPtr.Add(slapInfoPointer, index * slapInfoSize));

                if (fingerImage == IntPtr.Zero || fingerWidth <= 0 || fingerHeight <= 0)
                {
                    throw new BiometricDeviceException(
                        "REALSCAN_EMPTY_SEGMENT",
                        $"El segmento {index + 1} está vacío.");
                }

                var position = GetPositionName(slapInfo.FingerType);
                observedPositions.Add(position);
                if (!expectedPositions.Contains(position))
                {
                    throw new BiometricDeviceException(
                        "REALSCAN_WRONG_HAND_OR_SEQUENCE",
                        $"Se solicitó {profile.Name}, pero el SDK detectó {position}.");
                }

                var nfiq = 0;
                ThrowOnNativeError(
                    RealScanNative.RS_GetQualityScore(
                        fingerImage,
                        fingerWidth,
                        fingerHeight,
                        ref nfiq),
                    $"No se pudo calcular la calidad de {position}.");

                var liveness = lfdResult.Fingers[index];
                if (liveness.Result == RealScanNative.LfdFake)
                {
                    throw new BiometricDeviceException(
                        "REALSCAN_FAKE_FINGER",
                        $"La detección de dedo vivo rechazó {position} (puntaje {liveness.Score}).");
                }

                var isoTemplate = TryExtractIsoTemplate(
                    fingerImage,
                    fingerWidth,
                    fingerHeight,
                    out var templateWarning);
                if (templateWarning is not null && !warnings.Contains(templateWarning, StringComparer.Ordinal))
                {
                    warnings.Add(templateWarning);
                }

                samples.Add(new FingerprintSample(
                    Position: position,
                    IsoFingerPosition: slapInfo.FingerType,
                    NfiqQuality: nfiq,
                    Liveness: "LIVE",
                    LivenessScore: liveness.Score,
                    WsqBase64: EncodeWsq(fingerImage, fingerWidth, fingerHeight),
                    PreviewPngBase64: EncodePreviewPng(fingerImage, fingerWidth, fingerHeight),
                    IsoTemplateBase64: isoTemplate,
                    ImageWidth: fingerWidth,
                    ImageHeight: fingerHeight,
                    Rotation: slapInfo.Rotation));
            }

            if (!observedPositions.SetEquals(expectedPositions))
            {
                throw new BiometricDeviceException(
                    "REALSCAN_WRONG_HAND_OR_SEQUENCE",
                    $"Posiciones esperadas: {string.Join(", ", expectedPositions)}; " +
                    $"detectadas: {string.Join(", ", observedPositions)}.");
            }

            return new FingerprintCaptureResult(
                FingerType: profile.Name,
                MissingFingers: missingFingers,
                SlapWsqBase64: EncodeWsq(slapImage, slapWidth, slapHeight),
                SlapPreviewPngBase64: EncodePreviewPng(slapImage, slapWidth, slapHeight),
                SlapImageWidth: slapWidth,
                SlapImageHeight: slapHeight,
                Samples: samples.OrderBy(sample => sample.IsoFingerPosition).ToArray(),
                Warnings: warnings);
        }
        finally
        {
            foreach (var pointer in fingerPointers.Where(pointer => pointer != IntPtr.Zero))
            {
                RealScanNative.RS_FreeImageData(pointer);
            }

            FreeNative(slapInfoPointer);
            FreeNative(fingerPointerArray);
            FreeNative(fingerWidthsPointer);
            FreeNative(fingerHeightsPointer);
            FreeNative(slapImage);
        }
    }

    private static string EncodePreviewPng(IntPtr image, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
        var palette = bitmap.Palette;
        for (var index = 0; index < 256; index++)
        {
            palette.Entries[index] = Color.FromArgb(index, index, index);
        }

        bitmap.Palette = palette;
        var bounds = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
        try
        {
            var source = new byte[checked(width * height)];
            Marshal.Copy(image, source, 0, source.Length);
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(source, row * width, IntPtr.Add(data.Scan0, row * data.Stride), width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static int SegmentWithMissingFingers(
        CaptureProfile profile,
        IReadOnlyList<string> missingFingers,
        IntPtr slapImage,
        int slapWidth,
        int slapHeight,
        ref int numberOfFingers,
        ref IntPtr slapInfoPointer,
        ref IntPtr fingerPointerArray,
        ref IntPtr fingerWidthsPointer,
        ref IntPtr fingerHeightsPointer)
    {
        var missingInfo = new RealScanNative.MissingInfo();
        for (var index = 0; index < profile.ExpectedPositions.Count; index++)
        {
            if (!missingFingers.Contains(profile.ExpectedPositions[index], StringComparer.Ordinal))
            {
                continue;
            }

            switch (index)
            {
                case 0: missingInfo.FirstFinger = 1; break;
                case 1: missingInfo.SecondFinger = 1; break;
                case 2: missingInfo.ThirdFinger = 1; break;
                case 3: missingInfo.FourthFinger = 1; break;
            }
        }

        return RealScanNative.RS_SegmentMissingFinger(
            slapImage,
            slapWidth,
            slapHeight,
            profile.SlapType,
            ref numberOfFingers,
            ref slapInfoPointer,
            ref fingerPointerArray,
            ref fingerWidthsPointer,
            ref fingerHeightsPointer,
            ref missingInfo);
    }

    private RealScanNative.LfdResult ReadLiveness(int numberOfFingers)
    {
        var result = new RealScanNative.LfdResult
        {
            Fingers = new RealScanNative.LfdInfo[MaximumSegmentedFingers]
        };
        ThrowOnNativeError(
            RealScanNative.RS_GetLFDResult(_deviceHandle, ref result),
            "No se pudo obtener el resultado de detección de dedo vivo.");
        if (result.NumberOfFingers < numberOfFingers)
        {
            throw new BiometricDeviceException(
                "REALSCAN_INCOMPLETE_LFD",
                $"LFD evaluó {result.NumberOfFingers} de {numberOfFingers} dedos.");
        }

        return result;
    }

    private static string EncodeWsq(IntPtr image, int width, int height)
    {
        var bufferLength = checked(width * height * 2);
        var buffer = new byte[bufferLength];
        ThrowOnNativeError(
            RealScanNative.RS_EncodeWSQ(
                image,
                width,
                height,
                WsqCompressionRatio,
                buffer,
                ref bufferLength),
            "No se pudo codificar la imagen en WSQ.");
        return Convert.ToBase64String(buffer, 0, bufferLength);
    }

    private static string? TryExtractIsoTemplate(
        IntPtr image,
        int width,
        int height,
        out string? warning)
    {
        var buffer = new byte[IsoTemplateBufferSize];
        var templateSize = 0;
        var result = RealScanNative.RS_GetTemplate(
            RealScanNative.TemplateIso19794_2,
            image,
            width,
            height,
            buffer,
            ref templateSize);
        if (result == RealScanNative.ErrorNotSupported)
        {
            warning = "El extractor ISO/IEC 19794-2 no está habilitado para este dispositivo/licencia; " +
                      "la captura conserva WSQ, NFIQ y LFD.";
            return null;
        }

        ThrowOnNativeError(result, "No se pudo generar la plantilla ISO/IEC 19794-2.");
        if (templateSize <= 0 || templateSize > buffer.Length)
        {
            throw new BiometricDeviceException(
                "REALSCAN_EMPTY_TEMPLATE",
                "El SDK no devolvió una plantilla ISO/IEC 19794-2 válida.");
        }

        warning = null;
        return Convert.ToBase64String(buffer, 0, templateSize);
    }

    private void ReadDeviceInfo()
    {
        var info = CreateDeviceInfo();
        ThrowOnNativeError(
            RealScanNative.RS_GetDeviceInfo(_deviceHandle, ref info),
            "No se pudo leer la información del dispositivo.");
        _productName = DecodeCString(info.ProductName);
        _serialNumber = DecodeCString(info.DeviceId);
        _firmwareVersion = DecodeCString(info.FirmwareVersion);
        _hardwareVersion = DecodeCString(info.HardwareVersion);
    }

    private static RealScanNative.DeviceInfo CreateDeviceInfo() => new()
    {
        ProductName = new byte[16],
        DeviceId = new byte[16],
        FirmwareVersion = new byte[16],
        HardwareVersion = new byte[16],
        Reserved = new int[32]
    };

    private void MarkDisconnectedIfHardwareFailure(int? nativeCode)
    {
        if (nativeCode is not (
            RealScanNative.ErrorNoDevice or
            RealScanNative.ErrorInvalidHandle or
            RealScanNative.ErrorCannotGetUsbDevice or
            RealScanNative.ErrorCannotWriteUsb or
            RealScanNative.ErrorCannotReadUsb or
            RealScanNative.ErrorInvalidDeviceConnection or
            RealScanNative.ErrorDeviceNotInitialized))
        {
            return;
        }

        var wasConnected = _deviceHandle != 0;
        _deviceHandle = 0;
        if (_sdkInitialized)
        {
            RealScanNative.RS_ExitAllDevices();
            _sdkInitialized = false;
        }
        _lastError = $"Se perdió la conexión física con RealScan (código {nativeCode}).";
        if (wasConnected)
        {
            Console.WriteLine($"[HW DISCONNECTED REALSCAN] {_lastError}");
        }
    }

    private static IReadOnlyList<string> NormalizeMissingFingers(
        CaptureProfile profile,
        IReadOnlyCollection<string>? missingFingers)
    {
        var normalized = (missingFingers ?? Array.Empty<string>())
            .Select(position => position.Trim().ToUpperInvariant())
            .Where(position => position.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var invalid = normalized
            .Where(position => !profile.ExpectedPositions.Contains(position, StringComparer.Ordinal))
            .ToArray();
        if (invalid.Length > 0)
        {
            throw new BiometricDeviceException(
                "INVALID_MISSING_FINGERS",
                $"Los dedos {string.Join(", ", invalid)} no pertenecen a {profile.Name}.");
        }

        return normalized;
    }

    private static string GetPositionName(int isoPosition) => isoPosition switch
    {
        1 => "RIGHT_THUMB",
        2 => "RIGHT_INDEX",
        3 => "RIGHT_MIDDLE",
        4 => "RIGHT_RING",
        5 => "RIGHT_LITTLE",
        6 => "LEFT_THUMB",
        7 => "LEFT_INDEX",
        8 => "LEFT_MIDDLE",
        9 => "LEFT_RING",
        10 => "LEFT_LITTLE",
        _ => throw new BiometricDeviceException(
            "REALSCAN_UNKNOWN_FINGER_POSITION",
            $"El SDK devolvió la posición de dedo desconocida {isoPosition}.")
    };

    private static void ThrowCaptureError(int nativeCode)
    {
        if (nativeCode == RealScanNative.Success)
        {
            return;
        }

        var errorCode = nativeCode switch
        {
            -202 => "REALSCAN_CAPTURE_TIMEOUT",
            -203 => "REALSCAN_CAPTURE_CANCELLED",
            133 => "REALSCAN_FAKE_FINGER",
            134 or 601 => "REALSCAN_POOR_QUALITY",
            _ => "REALSCAN_CAPTURE_FAILED"
        };
        throw new BiometricDeviceException(
            errorCode,
            $"La captura dactilar no se completó. {GetNativeError(nativeCode)}",
            nativeCode);
    }

    private static void ThrowSegmentationError(
        int nativeCode,
        IReadOnlyCollection<string> missingFingers)
    {
        if (nativeCode == RealScanNative.Success)
        {
            return;
        }

        var errorCode = nativeCode switch
        {
            RealScanNative.ErrorSegmentWrongHand => "REALSCAN_WRONG_HAND",
            RealScanNative.ErrorSegmentFewerFingers => "REALSCAN_FEWER_FINGERS",
            _ => "REALSCAN_SEGMENTATION_FAILED"
        };
        var missingHint = missingFingers.Count == 0
            ? " Si existe un dedo faltante, debe declararse antes de capturar."
            : string.Empty;
        throw new BiometricDeviceException(
            errorCode,
            $"No fue posible segmentar la plancha. {GetNativeError(nativeCode)}{missingHint}",
            nativeCode);
    }

    private bool Fail(int nativeCode, string context, out string message)
    {
        _lastError = $"{context} {GetNativeError(nativeCode)} (código {nativeCode}).";
        message = _lastError;
        return false;
    }

    private static void ThrowOnNativeError(int nativeCode, string context)
    {
        if (nativeCode == RealScanNative.Success)
        {
            return;
        }

        throw new BiometricDeviceException(
            "REALSCAN_NATIVE_ERROR",
            $"{context} {GetNativeError(nativeCode)}",
            nativeCode);
    }

    private static string GetNativeError(int nativeCode)
    {
        var buffer = new byte[1024];
        try
        {
            RealScanNative.RS_GetErrStringChar(nativeCode, buffer);
            return DecodeCString(buffer) ?? "Error nativo sin descripción";
        }
        catch
        {
            return "Error nativo sin descripción";
        }
    }

    private static string? DecodeCString(byte[] value)
    {
        var terminator = Array.IndexOf(value, (byte)0);
        var length = terminator >= 0 ? terminator : value.Length;
        var result = Encoding.ASCII.GetString(value, 0, length).Trim();
        return result.Length == 0 ? null : result;
    }

    private void CloseDevice()
    {
        if (_deviceHandle == 0)
        {
            return;
        }

        RealScanNative.RS_AbortCapture(_deviceHandle);
        RealScanNative.RS_ExitDevice(_deviceHandle);
        _deviceHandle = 0;
    }

    private static void FreeNative(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            RealScanNative.RS_FreeImageData(pointer);
        }
    }

    internal sealed record CaptureProfile(
        string Name,
        int CaptureMode,
        int SlapType,
        IReadOnlyList<string> ExpectedPositions);
}
