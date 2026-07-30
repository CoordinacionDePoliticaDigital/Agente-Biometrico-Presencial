using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using AgenteBiometricoPresencial.Drivers;
using AgenteBiometricoPresencial.Models;

namespace AgenteBiometricoPresencial
{
    /// <summary>
    /// Monitorea el estado físico de los periféricos biométricos mediante:
    ///   1. Polling de VID/PID USB via WMI (System.Management) cada <PollIntervalSec> seg.
    ///   2. Sondeo del SDK del dispositivo para confirmar respuesta funcional.
    ///
    /// Al detectar un cambio de estado, dispara OnStatusChanged para que el
    /// WebSocketServer haga broadcast inmediato a todos los clientes conectados.
    /// </summary>
    public class DeviceStatusMonitor
    {
        // VID/PID de Xperix para consulta WMI (valores típicos del fabricante)
        // Actualizar si cambian con nuevas revisiones de hardware.
        private const string XPERIX_VID_REALSCAN  = "VID_16D1"; // RealScan G10
        private const string XPERIX_VID_REALPASS   = "VID_0525"; // RealPass RPNF

        public int PollIntervalSec { get; set; } = 5;

        private readonly RealScanDriver _realScan;
        private readonly RealPassDriver _realPass;

        private Thread? _pollThread;
        private bool _running = false;

        private DeviceStatus _lastRealScan = new() { DeviceId = "REALSCAN_G10", StatusCode = "UNKNOWN" };
        private DeviceStatus _lastRealPass = new() { DeviceId = "REALPASS_RPNF", StatusCode = "UNKNOWN" };

        /// <summary>
        /// Evento disparado cuando CUALQUIER periférico cambia de estado.
        /// El servidor WebSocket se suscribe a este evento para hacer broadcast.
        /// </summary>
        public event Action<DeviceStatus, DeviceStatus>? OnStatusChanged;

        public DeviceStatusMonitor(RealScanDriver realScan, RealPassDriver realPass)
        {
            _realScan = realScan;
            _realPass = realPass;
        }

        /// <summary>Inicia el polling en hilo de fondo.</summary>
        public void Start()
        {
            _running = true;
            _pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "DeviceStatusMonitor"
            };
            _pollThread.Start();
            Console.WriteLine($"[Monitor] Monitoreo de periféricos iniciado. Intervalo: {PollIntervalSec}s");
        }

        public void Stop()
        {
            _running = false;
            _pollThread?.Join(2000);
        }

        /// <summary>Obtiene el último estado conocido sin provocar un nuevo sondeo.</summary>
        public (DeviceStatus RealScan, DeviceStatus RealPass) GetCurrentStatus()
            => (_lastRealScan, _lastRealPass);

        // ─── Hilo de Polling ───────────────────────────────────────────────────

        private void PollLoop()
        {
            // Sondeo inicial inmediato al arrancar
            PollAndNotify(force: true);

            while (_running)
            {
                Thread.Sleep(PollIntervalSec * 1000);
                if (_running) PollAndNotify(force: false);
            }
        }

        private void PollAndNotify(bool force)
        {
            try
            {
                var rsStatus = _realScan.ProbeDevice();
                var rpStatus = _realPass.ProbeDevice();

                bool rsChanged = force || StatusChanged(_lastRealScan, rsStatus);
                bool rpChanged = force || StatusChanged(_lastRealPass, rpStatus);

                if (rsChanged || rpChanged)
                {
                    _lastRealScan = rsStatus;
                    _lastRealPass = rpStatus;

                    if (rsChanged)
                        Console.WriteLine($"[Monitor] RealScan G10 → {rsStatus.StatusCode}: {rsStatus.StatusMessage}");
                    if (rpChanged)
                        Console.WriteLine($"[Monitor] RealPass RPNF → {rpStatus.StatusCode}: {rpStatus.StatusMessage}");

                    OnStatusChanged?.Invoke(rsStatus, rpStatus);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Monitor ERROR] {ex.Message}");
            }
        }

        private static bool StatusChanged(DeviceStatus previous, DeviceStatus current)
            => previous.StatusCode != current.StatusCode
            || previous.IsConnected != current.IsConnected
            || previous.IsBusy != current.IsBusy;

        // ─── Detección USB via WMI ─────────────────────────────────────────────

        /// <summary>
        /// Consulta WMI para verificar si el VID del dispositivo está enumerado
        /// en el árbol USB de Windows. Esto es más rápido que cargar el SDK.
        /// </summary>
        public static bool IsDeviceUsbPresent(string vidPrefix)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\%" + vidPrefix + "%'");

                foreach (var obj in searcher.Get())
                {
                    obj.Dispose();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Monitor WMI] No se pudo consultar USB via WMI: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Diagnóstico rápido de USB en la consola al arrancar.
        /// Muestra todos los dispositivos USB Xperix detectados.
        /// </summary>
        public static void PrintUsbDiagnostics()
        {
            Console.WriteLine("\n[USB Diagnóstico] Enumerando dispositivos USB Xperix...");
            string[] vids = { XPERIX_VID_REALSCAN, XPERIX_VID_REALPASS };
            string[] names = { "RealScan G10", "RealPass RPNF" };

            for (int i = 0; i < vids.Length; i++)
            {
                bool found = IsDeviceUsbPresent(vids[i]);
                string icon = found ? "✓" : "✗";
                Console.WriteLine($"  [{icon}] {names[i]} (VID: {vids[i]}): {(found ? "Detectado en USB" : "No detectado")}");
            }

            Console.WriteLine();
        }
    }
}
