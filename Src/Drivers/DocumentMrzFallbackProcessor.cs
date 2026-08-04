using System.Drawing;
using System.Text.RegularExpressions;
using AgenteBiometricoPresencial.Contracts;
using Tesseract;

namespace AgenteBiometricoPresencial.Drivers;

/// <summary>
/// Recupera una TD1 ICAO 9303 después del recorte/orientación cuando RealPass
/// no la entregó (caso frecuente si la credencial se colocó a 180 grados).
/// </summary>
public static partial class DocumentMrzFallbackProcessor
{
    private static readonly int[] Weights = [7, 3, 1];

    public static DocumentCaptureResult Enrich(DocumentCaptureResult result, string expectedSide)
    {
        if (!string.Equals(expectedSide, "BACK", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        var image = result.Images.LastOrDefault(item => item.Type == "CROPPED_WHITE") ??
                    result.Images.FirstOrDefault(item => item.Type is "ID_WHITE" or "WHITE" or "OCR");
        if (image is null) return result;

        foreach (var rotation in new[] { 0, 180 })
        {
            try
            {
                var bytes = Convert.FromBase64String(image.Base64);
                if (rotation == 180) bytes = Rotate180(bytes);
                var text = Recognize(bytes);
                if (!TryParseTd1(text, out var lines, out var mrz)) continue;
                var images = rotation == 180 ? RotateCroppedImages(result.Images) : result.Images;
                var orientation = new DocumentOrientationResult(
                    ((result.Orientation?.Rotation ?? 0) + rotation) % 360,
                    $"OCR TD1 legible a {rotation}° adicionales",
                    "HIGH");
                Console.WriteLine($"[SUCCESS OCR MRZ] TD1 confirmada localmente; rotación adicional={rotation}°; CIC={mrz.Cic}.");
                return result with {
                    MrzLines = lines,
                    Mrz = mrz,
                    Images = images,
                    Orientation = orientation
                };
            }
            catch (Exception error)
            {
                Console.WriteLine($"[WARN OCR MRZ] No fue posible evaluar la rotación {rotation}°: {error.Message}");
            }
        }
        return result;
    }

    private static IReadOnlyList<DocumentImageResult> RotateCroppedImages(
        IReadOnlyList<DocumentImageResult> images)
    {
        return images.Select(image =>
        {
            if (!image.Type.StartsWith("CROPPED_", StringComparison.Ordinal)) return image;
            var rotated = Rotate180(Convert.FromBase64String(image.Base64));
            return image with { Base64 = Convert.ToBase64String(rotated) };
        }).ToArray();
    }

    private static string Recognize(byte[] bytes)
    {
        var dataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(dataPath, "OCRB.traineddata"))) return string.Empty;
        using var engine = new TesseractEngine(dataPath, "OCRB", EngineMode.Default);
        engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<");
        using var pix = Pix.LoadFromMemory(bytes);
        using var page = engine.Process(pix, PageSegMode.SparseText);
        return page.GetText() ?? string.Empty;
    }

    private static byte[] Rotate180(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var bitmap = new Bitmap(input);
        bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
        using var output = new MemoryStream();
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }

    internal static bool TryParseTd1(string text, out IReadOnlyList<string> lines, out DocumentMrzResult mrz)
    {
        var candidates = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => NonMrz().Replace(line.ToUpperInvariant(), string.Empty))
            .Where(line => line.Length >= 28)
            .Select(line => line.Length > 30 ? line[..30] : line.PadRight(30, '<'))
            .ToList();
        for (var index = 0; index <= candidates.Count - 3; index++)
        {
            var first = candidates[index];
            var second = candidates[index + 1];
            var third = candidates[index + 2];
            if (!first.StartsWith("I", StringComparison.Ordinal) || first[2..5] != "MEX") continue;
            var documentNumber = first.Substring(5, 9).Trim('<');
            var cic = first.Substring(5, 10).Trim('<');
            var names = third.Split("<<", 2, StringSplitOptions.None);
            var surname = names[0].Replace('<', ' ').Trim();
            var given = (names.Length > 1 ? names[1] : string.Empty).Replace('<', ' ').Trim();
            lines = [first, second, third];
            mrz = new DocumentMrzResult(
                first[..2].Trim('<'), first[2..5], surname, given,
                string.Join(' ', new[] { given, surname }.Where(value => value.Length > 0)),
                documentNumber, second.Substring(15, 3), second[..6], second.Substring(7, 1),
                second.Substring(8, 6), second.Substring(18, 11).Trim('<'),
                Check(first.Substring(5, 9), first[14]),
                Check(second[..6], second[6]),
                Check(second.Substring(8, 6), second[14]),
                Check(first.Substring(5, 25) + second[..7] + second.Substring(8, 7) + second.Substring(18, 11), second[29]),
                cic);
            return true;
        }
        lines = [];
        mrz = null!;
        return false;
    }

    private static bool Check(string value, char digit)
    {
        if (!char.IsDigit(digit)) return false;
        var total = value.Select((character, index) => MrzValue(character) * Weights[index % 3]).Sum();
        return total % 10 == digit - '0';
    }

    private static int MrzValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'Z' => value - 'A' + 10,
        _ => 0
    };

    [GeneratedRegex("[^A-Z0-9<]")]
    private static partial Regex NonMrz();
}
