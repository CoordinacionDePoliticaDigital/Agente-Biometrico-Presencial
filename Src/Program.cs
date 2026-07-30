using System;
using AgenteBiometricoPresencial.Server;

namespace AgenteBiometricoPresencial
{
    internal class Program
    {
        private static BiometricWebSocketServer? _server;

        static void Main(string[] args)
        {
            // ─── Banner ────────────────────────────────────────────────────────
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║      AGENTE BIOMÉTRICO PRESENCIAL  v2.0.0                    ║");
            Console.WriteLine("║      Middleware WebSocket — Autoridad Certificadora           ║");
            Console.WriteLine("║      RealScan G10  •  RealPass RPNF  •  Puerto 8443          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            // ─── Parseo de argumentos CLI ──────────────────────────────────────
            int port = 8443;
            bool simulationMode = false;

            foreach (string arg in args)
            {
                if (arg == "--simulate")
                {
                    simulationMode = true;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  ⚠  Flag --simulate detectado. Hardware simulado activo.");
                    Console.WriteLine("     Los dispositivos reales serán IGNORADOS.");
                    Console.ResetColor();
                    Console.WriteLine();
                }
                else if (arg.StartsWith("--port=") && int.TryParse(arg[7..], out int p))
                {
                    port = p;
                }
            }

            // ─── Diagnóstico USB (solo si no estamos en simulación) ────────────
            if (!simulationMode)
                DeviceStatusMonitor.PrintUsbDiagnostics();

            // ─── Manejo de CTRL+C ──────────────────────────────────────────────
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true; // Evitar terminación abrupta
                Console.WriteLine("\n[INFO] CTRL+C recibido. Deteniendo agente...");
                _server?.Stop();
                Environment.Exit(0);
            };

            // ─── Arranque del servidor ─────────────────────────────────────────
            _server = new BiometricWebSocketServer();

            try
            {
                _server.Start(port, simulationMode);
                Console.WriteLine($"\n[INFO] Agente listo. Escuchando en ws://127.0.0.1:{port}");
                Console.WriteLine("[INFO] Presiona CTRL+C para detener.\n");

                // Mantener vivo el proceso principal
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[FATAL] Error al iniciar el agente: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(1);
            }
        }
    }
}
