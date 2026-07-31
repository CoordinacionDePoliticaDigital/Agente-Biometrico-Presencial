using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AgenteBiometricoPresencial.Server;

namespace AgenteBiometricoPresencial
{
    internal class Program
    {
        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        private static BiometricWebSocketServer _server;
        private static NotifyIcon _notifyIcon;

        [STAThread]
        static void Main(string[] args)
        {
            // ─── Banner ────────────────────────────────────────────────────────
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║      AGENTE BIOMÉTRICO PRESENCIAL  v3.0.0                    ║");
            Console.WriteLine("║      Middleware WebSocket — Autoridad Certificadora           ║");
            Console.WriteLine("║      RealScan G10  •  RealPass RPNF  •  Puerto 8443          ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            // ─── Configurar PATH para dependencias nativas (C++/CLI) ───────────
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            string realScanDir = @"C:\Program Files\Xperix\RealScanSDK\Bin\x64";
            string realPassDir = @"C:\Program Files\Xperix\RealPassSDK\Bin\x64";

            if (Directory.Exists(realScanDir) && !currentPath.Contains(realScanDir))
                currentPath += ";" + realScanDir;
            if (Directory.Exists(realPassDir) && !currentPath.Contains(realPassDir))
                currentPath += ";" + realPassDir;

            Environment.SetEnvironmentVariable("PATH", currentPath);

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
                else if (arg.StartsWith("--port="))
                {
                    int p;
                    if (int.TryParse(arg.Substring(7), out p))
                        port = p;
                }
            }

            // ─── Diagnóstico USB (solo si no estamos en simulación) ────────────
            if (!simulationMode)
                DeviceStatusMonitor.PrintUsbDiagnostics();

            // ─── Manejo de CTRL+C ──────────────────────────────────────────────
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true; // Evitar terminación abrupta
                Console.WriteLine("\n[INFO] CTRL+C recibido. Deteniendo agente...");
                if (_server != null) _server.Stop();
                Environment.Exit(0);
            };

            // ─── Arranque del servidor ─────────────────────────────────────────
            _server = new BiometricWebSocketServer();

            try
            {
                _server.Start(port, simulationMode);
                Console.WriteLine(string.Format("\n[INFO] Agente listo. Escuchando en ws://127.0.0.1:{0}", port));
                Console.WriteLine("[INFO] Presiona CTRL+C para detener.\n");

                // ─── Configurar el NotifyIcon (System Tray) ────────────────────────
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray_icon.ico");
                if (File.Exists(iconPath))
                {
                    _notifyIcon = new NotifyIcon
                    {
                        Icon = new Icon(iconPath),
                        Text = "Agente Biométrico Presencial v3.0.0",
                        Visible = true
                    };

                    var contextMenu = new ContextMenuStrip();
                    contextMenu.Items.Add("Mostrar Consola", null, (s, e) => ShowWindow(GetConsoleWindow(), SW_SHOW));
                    contextMenu.Items.Add("Ocultar Consola", null, (s, e) => ShowWindow(GetConsoleWindow(), SW_HIDE));
                    contextMenu.Items.Add(new ToolStripSeparator());
                    contextMenu.Items.Add("Salir", null, (s, e) =>
                    {
                        if (_server != null) _server.Stop();
                        _notifyIcon.Visible = false;
                        Application.Exit();
                    });

                    _notifyIcon.ContextMenuStrip = contextMenu;

                    // Mostrar globo de notificación al inicio
                    _notifyIcon.ShowBalloonTip(3000, "Agente Iniciado", string.Format("Escuchando en puerto {0}", port), ToolTipIcon.Info);
                }

                // Ocultar consola por defecto al iniciar exitosamente
                ShowWindow(GetConsoleWindow(), SW_HIDE);

                // Iniciar el bucle de mensajes de UI para mantener vivo el hilo principal y el NotifyIcon
                Application.Run();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(string.Format("\n[FATAL] Error al iniciar el agente: {0}", ex.Message));
                Console.ResetColor();
                Environment.Exit(1);
            }
        }
    }
}
