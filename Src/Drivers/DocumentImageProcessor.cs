using AgenteBiometricoPresencial.Contracts;
using OpenCvSharp;

namespace AgenteBiometricoPresencial.Drivers;

/// <summary>
/// Normaliza la captura óptica de RealPass. El mismo homógrafo se aplica a
/// luz blanca, IR y UV para que las tres vistas sean comparables.
/// </summary>
public static class DocumentImageProcessor
{
    private const int CanonicalWidth = 1016;
    private const int CanonicalHeight = 640;
    private const double Id1AspectRatio = 85.60 / 53.98;
    private static readonly string FaceCascadePath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "haarcascade_frontalface_alt2.xml");

    public static DocumentCaptureResult Process(DocumentCaptureResult result, string expectedSide)
    {
        if (!TrySelectVisibleSource(
                result.Images,
                out var source,
                out var white,
                out var corners,
                out var method))
        {
            var visibleInventory = string.Join(", ", result.Images
                .Where(image => image.Type is "ID_WHITE" or "PASSPORT_WHITE" or "WHITE" or "OCR")
                .Select(image => $"{image.Type}:{image.Width}x{image.Height}"));
            Console.WriteLine(
                $"[IMAGING WARNING] Ningún canal visible produjo límites confiables de credencial; " +
                $"canales=[{visibleInventory}]. Se conserva la captura completa.");
            return result;
        }

        using (white)
        {
            var backgroundLuma = EstimateCornerLuma(white);
            if (backgroundLuma > 145)
            {
                Console.WriteLine(
                    $"[IMAGING WARNING] Fondo claro detectado (luma {backgroundLuma:F0}); " +
                    "puede contaminar visible/IR/UV. Use una base negra mate no reflectiva.");
            }

            if (TryRefineNestedCard(white, corners, out var refinedCorners, out var refinementMethod))
            {
                corners = refinedCorners;
                method = $"{method} + {refinementMethod}";
            }

            var images = result.Images.ToList();
            using var orientationSource = Rectify(white, corners);
            var orientation = DetermineOrientation(
                orientationSource,
                expectedSide,
                result.Barcodes,
                corners,
                white.Size(),
                GetBarcodeCoordinateSize(result.Images, white.Size()));
            AddRectified(images, result.Images, "CROPPED_WHITE", corners, white.Size(), orientation.Rotation, source.Type);
            AddRectified(images, result.Images, "CROPPED_IR", corners, white.Size(), orientation.Rotation, "ID_IR", "PASSPORT_IR", "IR");
            AddRectified(images, result.Images, "CROPPED_UV", corners, white.Size(), orientation.Rotation, "ID_UV", "PASSPORT_UV", "UV");
            AddEnhancedUv(images);
            Console.WriteLine(
                $"[SUCCESS IMAGING] Credencial normalizada a {CanonicalWidth}x{CanonicalHeight} " +
                $"mediante {method}; origen={source.Type} {white.Width}x{white.Height}.");
            Console.WriteLine(
                $"[SUCCESS IMAGING] Orientación documental {orientation.Rotation}° mediante " +
                $"{orientation.Method}; confianza={orientation.Confidence}.");

            if (expectedSide == "FRONT")
            {
                var normalized = images.LastOrDefault(image => image.Type == "CROPPED_WHITE");
                if (normalized is not null && TryDecode(normalized, out var card))
                {
                    using (card)
                    {
                        if (TryExtractPortrait(
                            card,
                            out var portrait,
                            out var portraitType,
                            out var portraitMethod))
                        {
                            using (portrait)
                            {
                                images.Add(Encode(portraitType, portrait));
                            }

                            Console.WriteLine(portraitType == "PORTRAIT_FACE"
                                ? $"[SUCCESS IMAGING] Retrato extraído de la credencial normalizada mediante {portraitMethod}."
                                : $"[IMAGING WARNING] No se confirmó el rostro; se entrega {portraitMethod} para revisión visual.");
                        }
                        else
                        {
                            Console.WriteLine(
                                "[IMAGING WARNING] La tarjeta fue recortada, pero no se detectó un rostro confiable en el frente.");
                        }
                    }
                }
            }

            return result with { Images = images, Orientation = orientation };
        }
    }

    private static DocumentOrientationResult DetermineOrientation(
        Mat card,
        string expectedSide,
        IReadOnlyList<DocumentBarcodeResult> barcodes,
        Point2f[] sourceCorners,
        OpenCvSharp.Size sourceSize,
        OpenCvSharp.Size barcodeCoordinateSize)
    {
        if (expectedSide == "FRONT")
        {
            using var rotated = Rotate180(card);
            var uprightScore = ScoreFrontOrientation(card);
            var rotatedScore = ScoreFrontOrientation(rotated);
            if (rotatedScore > Math.Max(1, uprightScore) * 1.18)
            {
                return new DocumentOrientationResult(
                    180,
                    $"rostro erguido (puntajes 0°={uprightScore:F0}, 180°={rotatedScore:F0})",
                    rotatedScore > uprightScore * 1.8 ? "HIGH" : "MEDIUM");
            }

            return new DocumentOrientationResult(
                0,
                $"rostro erguido (puntajes 0°={uprightScore:F0}, 180°={rotatedScore:F0})",
                uprightScore > rotatedScore * 1.8 ? "HIGH" : "MEDIUM");
        }

        var barcodeDecision = DetermineBackRotationFromBarcodes(
            barcodes,
            sourceCorners,
            sourceSize,
            barcodeCoordinateSize);
        if (barcodeDecision is not null)
        {
            return barcodeDecision;
        }

        using var gray = new Mat();
        Cv2.CvtColor(card, gray, ColorConversionCodes.BGR2GRAY);
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        // Compare symmetric bands so a 180° rotation swaps the measurements
        // instead of changing the sampled area. A small margin around the
        // centre avoids the dense PDF417 boundary from dominating the vote.
        var topHeight = (int)(binary.Height * 0.42);
        var bottomY = (int)(binary.Height * 0.58);
        using var top = new Mat(binary, new Rect(0, 0, binary.Width, topHeight));
        using var bottom = new Mat(binary, new Rect(0, bottomY, binary.Width, binary.Height - bottomY));
        var topDensity = Cv2.CountNonZero(top) / (double)(top.Width * top.Height);
        var bottomDensity = Cv2.CountNonZero(bottom) / (double)(bottom.Width * bottom.Height);
        var rotation = bottomDensity > topDensity * 1.05 ? 180 : 0;
        return new DocumentOrientationResult(
            rotation,
            $"distribución gráfica del reverso (superior={topDensity:P0}, inferior={bottomDensity:P0})",
            Math.Max(topDensity, bottomDensity) > Math.Min(topDensity, bottomDensity) * 1.25 ? "MEDIUM" : "LOW");
    }

    private static DocumentOrientationResult? DetermineBackRotationFromBarcodes(
        IReadOnlyList<DocumentBarcodeResult> barcodes,
        Point2f[] sourceCorners,
        OpenCvSharp.Size sourceSize,
        OpenCvSharp.Size barcodeCoordinateSize)
    {
        var located = barcodes
            .Where(barcode => barcode.Right > barcode.Left && barcode.Bottom > barcode.Top)
            .ToArray();
        if (located.Length == 0)
        {
            Console.WriteLine(
                $"[IMAGING INFO] {barcodes.Count} código(s) sin coordenadas utilizables; " +
                "la orientación del reverso usará distribución gráfica.");
            return null;
        }

        Console.WriteLine(
            $"[IMAGING INFO] Coordenadas de código RealPass: {string.Join(", ", located.Select(barcode =>
                $"{barcode.Type}[{barcode.Left},{barcode.Top},{barcode.Right},{barcode.Bottom}]"))}; " +
            $"referencia={barcodeCoordinateSize.Width}x{barcodeCoordinateSize.Height}.");

        var destination = new[]
        {
            new Point2f(0, 0),
            new Point2f(CanonicalWidth - 1, 0),
            new Point2f(CanonicalWidth - 1, CanonicalHeight - 1),
            new Point2f(0, CanonicalHeight - 1)
        };
        using var transform = Cv2.GetPerspectiveTransform(sourceCorners, destination);
        var centers = located.Select(barcode =>
        {
            var center = new Point2f(
                (barcode.Left + barcode.Right) / 2f * sourceSize.Width / Math.Max(1f, barcodeCoordinateSize.Width),
                (barcode.Top + barcode.Bottom) / 2f * sourceSize.Height / Math.Max(1f, barcodeCoordinateSize.Height));
            return ApplyHomography(center, transform);
        }).Where(point =>
            point.X >= 0 && point.X < CanonicalWidth &&
            point.Y >= 0 && point.Y < CanonicalHeight).ToArray();
        if (centers.Length == 0)
        {
            Console.WriteLine(
                $"[IMAGING INFO] Coordenadas de código fuera de la credencial normalizada; " +
                $"referencia={barcodeCoordinateSize.Width}x{barcodeCoordinateSize.Height}, " +
                $"canal={sourceSize.Width}x{sourceSize.Height}. Se usará distribución gráfica.");
            return null;
        }

        var averageY = centers.Average(point => point.Y);
        if (averageY is > CanonicalHeight * 0.44f and < CanonicalHeight * 0.56f)
        {
            Console.WriteLine(
                $"[IMAGING INFO] Código proyectado cerca del centro (y={averageY:F0}); " +
                "no se usa como voto de orientación.");
            return null;
        }

        var rotation = averageY > CanonicalHeight / 2f ? 180 : 0;
        return new DocumentOrientationResult(
            rotation,
            $"posición de {centers.Length} código(s) en el reverso (y={averageY:F0})",
            "HIGH");
    }

    private static OpenCvSharp.Size GetBarcodeCoordinateSize(
        IReadOnlyList<DocumentImageResult> images,
        OpenCvSharp.Size fallback)
    {
        var reference = images.FirstOrDefault(image =>
            image.Type == "WHITE" && image.Width > 0 && image.Height > 0);
        return reference is null
            ? fallback
            : new OpenCvSharp.Size(reference.Width, reference.Height);
    }

    private static double ScoreFrontOrientation(Mat card)
    {
        var faces = DetectFaces(card, restrictToPortraitRegion: false);
        return faces.Select(face =>
        {
            var centerX = face.X + face.Width / 2d;
            var positionWeight = centerX < card.Width * 0.55 ? 1.65 : 0.35;
            return face.Width * face.Height * positionWeight;
        }).DefaultIfEmpty(0).Max();
    }

    private static Mat Rotate180(Mat source)
    {
        var rotated = new Mat();
        Cv2.Rotate(source, rotated, RotateFlags.Rotate180);
        return rotated;
    }

    private static void AddEnhancedUv(ICollection<DocumentImageResult> images)
    {
        var croppedUv = images.LastOrDefault(image => image.Type == "CROPPED_UV");
        if (croppedUv is null || !TryDecode(croppedUv, out var source))
        {
            return;
        }

        double originalLuma;
        double gamma;
        using (source)
        using (var enhanced = EnhanceUv(source, out originalLuma, out gamma))
        {
            images.Add(Encode("CROPPED_UV_ENHANCED", enhanced));
        }

        Console.WriteLine(
            $"[SUCCESS IMAGING] UV+ generado con reducción bilateral, CLAHE, gamma {gamma:F2}, " +
            $"saturación y enfoque suave; luminancia original={originalLuma:F0}. " +
            "La captura UV original se conserva sin modificaciones.");
    }

    private static Mat EnhanceUv(Mat source, out double originalLuma, out double gamma)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        originalLuma = Cv2.Mean(gray).Val0;
        gamma = originalLuma switch
        {
            < 45 => 0.60,
            < 70 => 0.68,
            < 100 => 0.78,
            _ => 0.90
        };

        using var denoised = new Mat();
        Cv2.BilateralFilter(source, denoised, 7, 42, 42);
        using var lab = new Mat();
        Cv2.CvtColor(denoised, lab, ColorConversionCodes.BGR2Lab);
        var labChannels = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.1, tileGridSize: new OpenCvSharp.Size(8, 8));
            using var localContrast = new Mat();
            clahe.Apply(labChannels[0], localContrast);
            labChannels[0].Dispose();
            labChannels[0] = localContrast.Clone();
            Cv2.Merge(labChannels, lab);
        }
        finally
        {
            foreach (var channel in labChannels)
            {
                channel.Dispose();
            }
        }

        using var contrasted = new Mat();
        Cv2.CvtColor(lab, contrasted, ColorConversionCodes.Lab2BGR);
        using var lookup = new Mat(1, 256, MatType.CV_8UC1);
        for (var value = 0; value < 256; value++)
        {
            lookup.Set(0, value, (byte)Math.Clamp(
                Math.Round(Math.Pow(value / 255d, gamma) * 255d),
                0,
                255));
        }

        using var gammaCorrected = new Mat();
        Cv2.LUT(contrasted, lookup, gammaCorrected);
        using var hsv = new Mat();
        Cv2.CvtColor(gammaCorrected, hsv, ColorConversionCodes.BGR2HSV);
        var hsvChannels = Cv2.Split(hsv);
        try
        {
            Cv2.ConvertScaleAbs(hsvChannels[1], hsvChannels[1], alpha: 1.12, beta: 0);
            Cv2.Merge(hsvChannels, hsv);
        }
        finally
        {
            foreach (var channel in hsvChannels)
            {
                channel.Dispose();
            }
        }

        using var saturated = new Mat();
        Cv2.CvtColor(hsv, saturated, ColorConversionCodes.HSV2BGR);
        using var blurred = new Mat();
        Cv2.GaussianBlur(saturated, blurred, new OpenCvSharp.Size(0, 0), 1.0);
        var result = new Mat();
        Cv2.AddWeighted(saturated, 1.16, blurred, -0.16, 0, result);
        return result;
    }

    private static bool TrySelectVisibleSource(
        IReadOnlyList<DocumentImageResult> images,
        out DocumentImageResult source,
        out Mat visible,
        out Point2f[] corners,
        out string method)
    {
        foreach (var type in new[] { "ID_WHITE", "PASSPORT_WHITE", "WHITE", "OCR" })
        {
            var candidate = images.FirstOrDefault(image => image.Type == type);
            if (candidate is null || !TryDecode(candidate, out var decoded))
            {
                continue;
            }

            if (TryFindCardCorners(decoded, out corners, out var detectionMethod))
            {
                source = candidate;
                visible = decoded;
                method = $"{detectionMethod}; canal={candidate.Type}";
                return true;
            }

            Console.WriteLine(
                $"[IMAGING INFO] Canal {candidate.Type} {decoded.Width}x{decoded.Height} " +
                "sin candidato geométrico; probando el siguiente canal visible.");
            decoded.Dispose();
        }

        source = null!;
        visible = null!;
        corners = Array.Empty<Point2f>();
        method = "sin candidato";
        return false;
    }

    private static void AddRectified(
        ICollection<DocumentImageResult> output,
        IReadOnlyList<DocumentImageResult> sourceImages,
        string outputType,
        Point2f[] corners,
        OpenCvSharp.Size referenceSize,
        int rotation,
        params string[] sourceTypes)
    {
        var source = FindImage(sourceImages, sourceTypes);
        if (source is null || !TryDecode(source, out var image))
        {
            return;
        }

        using (image)
        using (var rectified = Rectify(image, ScaleCorners(corners, referenceSize, image.Size())))
        {
            if (rotation == 180)
            {
                using var oriented = Rotate180(rectified);
                output.Add(Encode(outputType, oriented));
            }
            else
            {
                output.Add(Encode(outputType, rectified));
            }
        }
    }

    private static Mat Rectify(Mat source, Point2f[] corners)
    {
        var destination = new[]
        {
            new Point2f(0, 0),
            new Point2f(CanonicalWidth - 1, 0),
            new Point2f(CanonicalWidth - 1, CanonicalHeight - 1),
            new Point2f(0, CanonicalHeight - 1)
        };
        using var transform = Cv2.GetPerspectiveTransform(corners, destination);
        var result = new Mat();
        Cv2.WarpPerspective(
            source,
            result,
            transform,
            new OpenCvSharp.Size(CanonicalWidth, CanonicalHeight),
            InterpolationFlags.Cubic,
            BorderTypes.Constant,
            Scalar.Black);
        return result;
    }

    private static bool TryRefineNestedCard(
        Mat source,
        Point2f[] outerCorners,
        out Point2f[] refinedCorners,
        out string method)
    {
        using var outerRectified = Rectify(source, outerCorners);
        if (!TryFindCardCorners(outerRectified, out var nestedCorners, out var nestedMethod))
        {
            refinedCorners = Array.Empty<Point2f>();
            method = "sin refinamiento";
            return false;
        }

        var nestedArea = Math.Abs(Cv2.ContourArea(nestedCorners));
        var nestedFraction = nestedArea / (CanonicalWidth * (double)CanonicalHeight);
        var horizontal = (Distance(nestedCorners[0], nestedCorners[1]) +
                          Distance(nestedCorners[2], nestedCorners[3])) / 2d;
        var vertical = (Distance(nestedCorners[0], nestedCorners[3]) +
                        Distance(nestedCorners[1], nestedCorners[2])) / 2d;
        var nestedRatio = horizontal / Math.Max(1, vertical);
        if (nestedFraction is < 0.12 or > 0.58 || nestedRatio is < 1.32 or > 1.86)
        {
            refinedCorners = Array.Empty<Point2f>();
            method = $"candidato interno descartado (proporción {nestedRatio:F2})";
            return false;
        }

        var destination = new[]
        {
            new Point2f(0, 0),
            new Point2f(CanonicalWidth - 1, 0),
            new Point2f(CanonicalWidth - 1, CanonicalHeight - 1),
            new Point2f(0, CanonicalHeight - 1)
        };
        using var forward = Cv2.GetPerspectiveTransform(outerCorners, destination);
        using var inverse = forward.Inv();
        refinedCorners = nestedCorners.Select(point => ApplyHomography(point, inverse)).ToArray();
        method = $"refinamiento interno {nestedMethod}";
        return true;
    }

    private static Point2f ApplyHomography(Point2f point, Mat transform)
    {
        var denominator = transform.At<double>(2, 0) * point.X +
                          transform.At<double>(2, 1) * point.Y +
                          transform.At<double>(2, 2);
        return new Point2f(
            (float)((transform.At<double>(0, 0) * point.X +
                     transform.At<double>(0, 1) * point.Y +
                     transform.At<double>(0, 2)) / denominator),
            (float)((transform.At<double>(1, 0) * point.X +
                     transform.At<double>(1, 1) * point.Y +
                     transform.At<double>(1, 2)) / denominator));
    }

    private static bool TryFindCardCorners(
        Mat source,
        out Point2f[] corners,
        out string method)
    {
        const int analysisWidth = 1000;
        var scale = Math.Min(1d, analysisWidth / (double)source.Width);
        using var reduced = new Mat();
        Cv2.Resize(source, reduced, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(reduced, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(5, 5), 0);

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 45, 140);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(9, 9));
        Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel, iterations: 2);

        var candidates = new List<CardCandidate>();
        AddCandidate(candidates, edges, reduced.Size(), "bordes Canny");

        using var bright = new Mat();
        Cv2.Threshold(gray, bright, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var broadKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(61, 41));
        Cv2.MorphologyEx(bright, bright, MorphTypes.Close, broadKernel, iterations: 2);
        using var cleanupKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
        Cv2.MorphologyEx(bright, bright, MorphTypes.Open, cleanupKernel, iterations: 1);
        AddCandidate(candidates, bright, reduced.Size(), "umbral Otsu");

        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        Cv2.MorphologyEx(dark, dark, MorphTypes.Close, broadKernel, iterations: 2);
        Cv2.MorphologyEx(dark, dark, MorphTypes.Open, cleanupKernel, iterations: 1);
        AddCandidate(candidates, dark, reduced.Size(), "umbral Otsu invertido");

        using var backgroundDifference = BuildBackgroundDifferenceMask(reduced);
        Cv2.MorphologyEx(backgroundDifference, backgroundDifference, MorphTypes.Close, broadKernel, iterations: 2);
        Cv2.MorphologyEx(backgroundDifference, backgroundDifference, MorphTypes.Open, cleanupKernel, iterations: 1);
        AddCandidate(candidates, backgroundDifference, reduced.Size(), "diferencia contra fondo");

        var candidate = candidates.MaxBy(item => item.Score);

        if (candidate is null)
        {
            corners = Array.Empty<Point2f>();
            method = "sin candidato";
            return false;
        }

        corners = EnsureLandscape(OrderCorners(candidate.Points))
            .Select(point => new Point2f((float)(point.X / scale), (float)(point.Y / scale)))
            .ToArray();
        method = candidate.Method;
        return true;
    }

    private static CardCandidate? FindBestQuadrilateral(
        Mat mask,
        OpenCvSharp.Size size,
        string method)
    {
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var imageArea = size.Width * size.Height;
        CardCandidate? best = null;
        var bestScore = 0d;
        foreach (var contour in contours)
        {
            var contourArea = Math.Abs(Cv2.ContourArea(contour));
            var areaFraction = contourArea / imageArea;
            if (areaFraction < 0.035 || areaFraction > 0.62)
            {
                continue;
            }

            var minimumRectangle = Cv2.MinAreaRect(contour);
            var rectangleArea = minimumRectangle.Size.Width * minimumRectangle.Size.Height;
            if (rectangleArea <= 0)
            {
                continue;
            }

            var width = Math.Max(minimumRectangle.Size.Width, minimumRectangle.Size.Height);
            var height = Math.Max(1, Math.Min(minimumRectangle.Size.Width, minimumRectangle.Size.Height));
            var ratio = width / height;
            var rectangularity = contourArea / rectangleArea;
            // ID-1 is 1.586:1. The relaxed band tolerates perspective while
            // excluding the scanner bed (observed 1.38 / 42% rectangularity)
            // and internal PDF417 blocks (observed 2.05 / 99%).
            if (ratio is < 1.30f or > 1.90f || rectangularity < 0.60)
            {
                continue;
            }

            var perimeter = Cv2.ArcLength(contour, true);
            var polygon = Cv2.ApproxPolyDP(contour, perimeter * 0.025, true);
            var candidate = polygon.Length == 4
                ? polygon
                : minimumRectangle.Points()
                    .Select(point => new OpenCvSharp.Point(
                        (int)Math.Round(point.X),
                        (int)Math.Round(point.Y)))
                    .ToArray();
            var aspectScore = Math.Max(0.1, 1 - Math.Abs(ratio - Id1AspectRatio) / Id1AspectRatio);
            var expectedAreaScore = Math.Max(0.15, 1 - Math.Abs(areaFraction - 0.30) / 0.30);
            var score = contourArea * (0.35 + Math.Min(1, rectangularity)) * aspectScore * expectedAreaScore;
            if (score <= bestScore)
            {
                continue;
            }

            best = new CardCandidate(
                candidate,
                score,
                $"{method} (área {areaFraction:P0}, proporción {ratio:F2}, rectangularidad {rectangularity:P0})");
            bestScore = score;
        }

        return best;
    }

    private static void AddCandidate(
        ICollection<CardCandidate> candidates,
        Mat mask,
        OpenCvSharp.Size size,
        string method)
    {
        var candidate = FindBestQuadrilateral(mask, size, method);
        if (candidate is not null)
        {
            candidates.Add(candidate);
        }
    }

    private static Mat BuildBackgroundDifferenceMask(Mat source)
    {
        var patchWidth = Math.Max(8, source.Width / 12);
        var patchHeight = Math.Max(8, source.Height / 12);
        var patches = new[]
        {
            new Rect(0, 0, patchWidth, patchHeight),
            new Rect(source.Width - patchWidth, 0, patchWidth, patchHeight),
            new Rect(0, source.Height - patchHeight, patchWidth, patchHeight),
            new Rect(source.Width - patchWidth, source.Height - patchHeight, patchWidth, patchHeight)
        };
        var means = patches.Select(rect =>
        {
            using var patch = new Mat(source, rect);
            return Cv2.Mean(patch);
        }).ToArray();
        var background = new Scalar(
            means.Average(value => value.Val0),
            means.Average(value => value.Val1),
            means.Average(value => value.Val2));

        using var difference = new Mat();
        Cv2.Absdiff(source, background, difference);
        using var differenceGray = new Mat();
        Cv2.CvtColor(difference, differenceGray, ColorConversionCodes.BGR2GRAY);
        var mask = new Mat();
        Cv2.Threshold(
            differenceGray,
            mask,
            0,
            255,
            ThresholdTypes.Binary | ThresholdTypes.Otsu);
        return mask;
    }

    private static double EstimateCornerLuma(Mat source)
    {
        var patchWidth = Math.Max(8, source.Width / 12);
        var patchHeight = Math.Max(8, source.Height / 12);
        var patches = new[]
        {
            new Rect(0, 0, patchWidth, patchHeight),
            new Rect(source.Width - patchWidth, 0, patchWidth, patchHeight),
            new Rect(0, source.Height - patchHeight, patchWidth, patchHeight),
            new Rect(source.Width - patchWidth, source.Height - patchHeight, patchWidth, patchHeight)
        };
        return patches.Average(rect =>
        {
            using var patch = new Mat(source, rect);
            var mean = Cv2.Mean(patch);
            return mean.Val2 * 0.2126 + mean.Val1 * 0.7152 + mean.Val0 * 0.0722;
        });
    }

    private static Point2f[] ScaleCorners(
        IReadOnlyList<Point2f> corners,
        OpenCvSharp.Size referenceSize,
        OpenCvSharp.Size targetSize)
    {
        if (referenceSize == targetSize)
        {
            return corners.ToArray();
        }

        var scaleX = targetSize.Width / (float)Math.Max(1, referenceSize.Width);
        var scaleY = targetSize.Height / (float)Math.Max(1, referenceSize.Height);
        return corners
            .Select(point => new Point2f(point.X * scaleX, point.Y * scaleY))
            .ToArray();
    }

    private static OpenCvSharp.Point[] EnsureLandscape(OpenCvSharp.Point[] points)
    {
        var horizontal = Distance(points[0], points[1]);
        var vertical = Distance(points[0], points[3]);
        return horizontal >= vertical
            ? points
            : new[] { points[3], points[0], points[1], points[2] };
    }

    private static double Distance(OpenCvSharp.Point first, OpenCvSharp.Point second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static double Distance(Point2f first, Point2f second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static bool TryExtractPortrait(
        Mat card,
        out Mat portrait,
        out string portraitType,
        out string method)
    {
        var faces = DetectFaces(card, restrictToPortraitRegion: true)
            .OrderByDescending(face =>
                face.Width * face.Height * (face.X < card.Width * 0.45 ? 1.35 : 1))
            .ToArray();
        if (faces.Length > 0)
        {
            var face = faces[0];
            var desired = new Rect(
                face.X - (int)(face.Width * 0.35),
                face.Y - (int)(face.Height * 0.12),
                (int)(face.Width * 1.68),
                (int)(face.Height * 1.85));
            var bounded = Intersect(desired, new Rect(0, 0, card.Width, card.Height));
            if (bounded.Width > 0 && bounded.Height > 0)
            {
                portrait = new Mat(card, bounded).Clone();
                portraitType = "PORTRAIT_FACE";
                method = $"Haar frontal ({face.Width}x{face.Height})";
                return true;
            }
        }

        portrait = new Mat();
        portraitType = string.Empty;
        method = "rostro no confirmado";
        return false;
    }

    private static Rect[] DetectFaces(Mat card, bool restrictToPortraitRegion)
    {
        var cascadePath = File.Exists(FaceCascadePath)
            ? FaceCascadePath
            : @"C:\Program Files\Xperix\RealPassSDK\Bin\x64\data\face\haarcascade_frontalface_alt2.xml";
        if (!File.Exists(cascadePath))
        {
            return Array.Empty<Rect>();
        }

        using var gray = new Mat();
        Cv2.CvtColor(card, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);
        using var classifier = new CascadeClassifier(cascadePath);
        return classifier.DetectMultiScale(
                gray,
                scaleFactor: 1.05,
                minNeighbors: 4,
                flags: HaarDetectionTypes.ScaleImage,
                minSize: new OpenCvSharp.Size(32, 32),
                maxSize: new OpenCvSharp.Size(card.Width / 3, card.Height / 2))
            .Where(face => !restrictToPortraitRegion ||
                (face.X + face.Width / 2d < card.Width * 0.78 &&
                 face.Y + face.Height / 2d > card.Height * 0.12 &&
                 face.Y + face.Height / 2d < card.Height * 0.9))
            .ToArray();
    }

    private static Rect Intersect(Rect first, Rect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : new Rect();
    }

    private static OpenCvSharp.Point[] OrderCorners(OpenCvSharp.Point[] points)
    {
        var topLeft = points.MinBy(point => point.X + point.Y);
        var bottomRight = points.MaxBy(point => point.X + point.Y);
        var topRight = points.MinBy(point => point.Y - point.X);
        var bottomLeft = points.MaxBy(point => point.Y - point.X);
        return new[] { topLeft, topRight, bottomRight, bottomLeft };
    }

    private static DocumentImageResult? FindImage(
        IReadOnlyList<DocumentImageResult> images,
        params string[] types) =>
        types.Select(type => images.FirstOrDefault(image => image.Type == type)).FirstOrDefault(image => image is not null);

    private static bool TryDecode(DocumentImageResult source, out Mat image)
    {
        try
        {
            image = Cv2.ImDecode(Convert.FromBase64String(source.Base64), ImreadModes.Color);
            return !image.Empty();
        }
        catch
        {
            image = new Mat();
            return false;
        }
    }

    private static DocumentImageResult Encode(string type, Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return new DocumentImageResult(
            type,
            "image/png",
            Convert.ToBase64String(bytes),
            image.Width,
            image.Height);
    }

    private sealed record CardCandidate(
        OpenCvSharp.Point[] Points,
        double Score,
        string Method);
}
