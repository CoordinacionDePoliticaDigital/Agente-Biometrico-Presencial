using System.Runtime.InteropServices;
using AgenteBiometricoPresencial.Server;
using AgenteBiometricoPresencial.Drivers;
using AgenteBiometricoPresencial.Contracts;
using AgenteBiometricoPresencial.UI;

namespace AgenteBiometricoPresencial;

internal static class Program
{
    private static readonly string[] NativeSdkDirectories =
    {
        @"C:\Program Files\Xperix\RealScanSDK\Bin\x64",
        @"C:\Program Files\Xperix\RealPassSDK\Bin\x64"
    };

    [STAThread]
    private static void Main(string[] args)
    {
        ConfigureNativeSearchPath();

        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
        {
            EnsureDiagnosticConsole();
            RunDiagnostics();
            return;
        }

        if (args.Contains("--scan-document", StringComparer.OrdinalIgnoreCase))
        {
            EnsureDiagnosticConsole();
            RunDocumentDiagnosticAsync().GetAwaiter().GetResult();
            return;
        }

        var captureArgument = Array.FindIndex(
            args,
            argument => argument.Equals("--capture", StringComparison.OrdinalIgnoreCase));
        if (captureArgument >= 0)
        {
            EnsureDiagnosticConsole();
            var fingerType = captureArgument + 1 < args.Length
                ? args[captureArgument + 1]
                : "SLAP_4_LEFT";
            RunCaptureDiagnosticAsync(fingerType).GetAwaiter().GetResult();
            return;
        }

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\GobiernoChihuahua.AgenteBiometricoPresencial",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "El Agente Biométrico Presencial ya está ejecutándose en la bandeja del sistema.",
                "Agente biométrico",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        AgentLog.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
            AgentLog.Append($"[UI FATAL] {eventArgs.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AgentLog.Append($"[PROCESS FATAL] {eventArgs.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AgentLog.Append($"[TASK ERROR] {eventArgs.Exception}");
            eventArgs.SetObserved();
        };
        Console.WriteLine("==========================================================");
        Console.WriteLine("  Agente Biométrico Presencial - Autoridad Certificadora ");
        Console.WriteLine("  Middleware WebSocket para RealScan G10 & RealPass RPNF  ");
        Console.WriteLine("==========================================================");
        Application.Run(new TrayApplicationContext(
            showLogsOnStart: args.Contains("--show-logs", StringComparer.OrdinalIgnoreCase)));
        GC.KeepAlive(singleInstance);
    }

    private static void ConfigureNativeSearchPath()
    {
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var availableDirectories = NativeSdkDirectories.Where(Directory.Exists);
        Environment.SetEnvironmentVariable(
            "PATH",
            string.Join(Path.PathSeparator, availableDirectories.Append(existingPath)));
    }

    private static void RunDiagnostics()
    {
        Console.WriteLine("[DIAGNOSTIC] Inicializando dispositivos sin abrir el WebSocket...");
        using var realScan = new RealScanDriver();
        using var realPass = new RealPassDriver();

        var realScanReady = realScan.Initialize(out var realScanMessage);
        Console.WriteLine($"[RealScan G10] {(realScanReady ? "READY" : "UNAVAILABLE")}: {realScanMessage}");
        var realPassReady = realPass.Initialize(out var realPassMessage);
        Console.WriteLine($"[RealPass RPNF] {(realPassReady ? "READY" : "UNAVAILABLE")}: {realPassMessage}");
        Console.WriteLine("[DIAGNOSTIC] Finalizado.");
    }

    private static async Task RunCaptureDiagnosticAsync(string fingerType)
    {
        Console.WriteLine($"[CAPTURE TEST] Inicializando RealScan para {fingerType}...");
        using var realScan = new RealScanDriver();
        if (!realScan.Initialize(out var message))
        {
            Console.WriteLine($"[CAPTURE TEST] UNAVAILABLE: {message}");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine($"[CAPTURE TEST] {message}");
        Console.WriteLine("[CAPTURE TEST] Coloque los dedos indicados sobre el sensor.");
        try
        {
            var result = await realScan.CaptureAsync(
                fingerType,
                Array.Empty<string>(),
                timeoutSeconds: 45,
                CancellationToken.None);
            Console.WriteLine(
                $"[CAPTURE TEST] SUCCESS: {result.Samples.Count} dedos; " +
                $"plancha {result.SlapImageWidth}x{result.SlapImageHeight}, " +
                $"WSQ {GetDecodedSize(result.SlapWsqBase64)} bytes.");
            foreach (var sample in result.Samples)
            {
                Console.WriteLine(
                    $"[CAPTURE TEST] {sample.Position}: NFIQ={sample.NfiqQuality}, " +
                    $"LFD={sample.Liveness}({sample.LivenessScore}), " +
                    $"WSQ={GetDecodedSize(sample.WsqBase64)} bytes, " +
                    $"ISO={(sample.IsoTemplateBase64 is null ? "no disponible" : $"{GetDecodedSize(sample.IsoTemplateBase64)} bytes")}.");
            }
        }
        catch (BiometricDeviceException exception)
        {
            Console.WriteLine(
                $"[CAPTURE TEST] FAILED {exception.ErrorCode}: {exception.Message}");
            Environment.ExitCode = 3;
        }
    }

    private static async Task RunDocumentDiagnosticAsync()
    {
        Console.WriteLine("[DOCUMENT TEST] Inicializando RealPass...");
        using var realPass = new RealPassDriver();
        if (!realPass.Initialize(out var message))
        {
            Console.WriteLine($"[DOCUMENT TEST] UNAVAILABLE: {message}");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine($"[DOCUMENT TEST] {message}");
        Console.WriteLine("[DOCUMENT TEST] Coloque el documento sobre el lector.");
        try
        {
            var result = await realPass.ReadDocumentAsync(
                readRfid: true,
                timeoutSeconds: 60,
                CancellationToken.None);
            Console.WriteLine(
                $"[DOCUMENT TEST] SUCCESS: tipo={result.DocumentType}, " +
                $"MRZ={result.MrzLines.Count} líneas, imágenes={result.Images.Count}, " +
                $"barcodes={result.Barcodes.Count}, eDocument={result.ElectronicDocument is not null}.");
            foreach (var image in result.Images)
            {
                Console.WriteLine(
                    $"[DOCUMENT TEST] Imagen {image.Type}: {image.Width}x{image.Height}, " +
                    $"PNG={GetDecodedSize(image.Base64)} bytes.");
            }

            if (result.Mrz is not null)
            {
                Console.WriteLine(
                    "[DOCUMENT TEST] MRZ checks: " +
                    $"documento={result.Mrz.DocumentNumberCheckDigitValid}, " +
                    $"nacimiento={result.Mrz.BirthDateCheckDigitValid}, " +
                    $"vigencia={result.Mrz.ExpiryDateCheckDigitValid}, " +
                    $"compuesto={result.Mrz.CompositeCheckDigitValid}.");
            }
        }
        catch (BiometricDeviceException exception)
        {
            Console.WriteLine(
                $"[DOCUMENT TEST] FAILED {exception.ErrorCode}: {exception.Message}");
            Environment.ExitCode = 3;
        }
    }

    private static int GetDecodedSize(string base64) =>
        checked(base64.Length / 4 * 3 - (base64.EndsWith("==", StringComparison.Ordinal) ? 2 :
            base64.EndsWith('=') ? 1 : 0));

    private static void EnsureDiagnosticConsole()
    {
        if (GetConsoleWindow() == IntPtr.Zero)
        {
            AllocConsole();
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
