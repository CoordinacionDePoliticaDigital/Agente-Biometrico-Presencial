using Newtonsoft.Json;

namespace AgenteBiometricoPresencial.Contracts;

public sealed class BiometricCommand
{
    [JsonProperty("command")]
    public string Command { get; init; } = string.Empty;

    [JsonProperty("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonProperty("fingerType")]
    public string? FingerType { get; init; }

    [JsonProperty("missingFingers")]
    public IReadOnlyList<string>? MissingFingers { get; init; }

    [JsonProperty("timeoutSeconds")]
    public int? TimeoutSeconds { get; init; }

    [JsonProperty("readRfid")]
    public bool? ReadRfid { get; init; }

    [JsonProperty("documentSide")]
    public string? DocumentSide { get; init; }

    [JsonProperty("driveName")]
    public string? DriveName { get; init; }

    [JsonProperty("fileName")]
    public string? FileName { get; init; }

    [JsonProperty("privateKeyPem")]
    public string? PrivateKeyPem { get; init; }
}

public sealed record FingerprintSample(
    string Position,
    int IsoFingerPosition,
    int NfiqQuality,
    string Liveness,
    int? LivenessScore,
    string WsqBase64,
    string PreviewPngBase64,
    string? IsoTemplateBase64,
    int ImageWidth,
    int ImageHeight,
    int Rotation);

public sealed record FingerprintCaptureResult(
    string FingerType,
    IReadOnlyList<string> MissingFingers,
    string SlapWsqBase64,
    string SlapPreviewPngBase64,
    int SlapImageWidth,
    int SlapImageHeight,
    IReadOnlyList<FingerprintSample> Samples,
    IReadOnlyList<string> Warnings);

public sealed record DocumentMrzResult(
    string? DocumentType,
    string? IssuingState,
    string? Surname,
    string? GivenNames,
    string? FullName,
    string? DocumentNumber,
    string? Nationality,
    string? DateOfBirth,
    string? Sex,
    string? ExpiryDate,
    string? OptionalData,
    bool DocumentNumberCheckDigitValid,
    bool BirthDateCheckDigitValid,
    bool ExpiryDateCheckDigitValid,
    bool CompositeCheckDigitValid,
    string? Cic = null);

public sealed record DocumentImageResult(
    string Type,
    string MimeType,
    string Base64,
    int Width,
    int Height);

public sealed record DocumentBarcodeResult(
    string Type,
    string Data,
    int Left,
    int Top,
    int Right,
    int Bottom);

public sealed record ElectronicDocumentResult(
    string Bac,
    string Pace,
    string ActiveAuthentication,
    string ChipAuthentication,
    string PassiveAuthentication,
    string TerminalAuthentication,
    IReadOnlyDictionary<string, string> DataGroups);

public sealed record DocumentCaptureResult(
    string DocumentType,
    IReadOnlyList<string> MrzLines,
    DocumentMrzResult? Mrz,
    IReadOnlyList<DocumentImageResult> Images,
    IReadOnlyList<DocumentBarcodeResult> Barcodes,
    ElectronicDocumentResult? ElectronicDocument,
    DocumentOrientationResult? Orientation = null);

public sealed record DocumentOrientationResult(
    int Rotation,
    string Method,
    string Confidence);

public sealed record DocumentSideValidation(
    string ExpectedSide,
    string DetectedSide,
    bool Accepted,
    string Confidence,
    IReadOnlyList<string> Evidence,
    string Message);

public sealed record DocumentEligibilityValidation(
    string Status,
    string Category,
    bool Accepted,
    IReadOnlyList<string> Evidence,
    string Message);

public sealed record DeviceState(
    bool Available,
    bool Connected,
    string? SerialNumber,
    string? LastError,
    string? ProductName = null,
    string? FirmwareVersion = null,
    string? HardwareVersion = null);

public sealed class BiometricDeviceException : Exception
{
    public BiometricDeviceException(string errorCode, string message, int? nativeCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
        NativeCode = nativeCode;
    }

    public string ErrorCode { get; }
    public int? NativeCode { get; }
}
