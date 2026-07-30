using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AgenteBiometricoPresencial.Drivers;
using AgenteBiometricoPresencial.Models;
using DevStatus = AgenteBiometricoPresencial.Models.DeviceStatus;

namespace AgenteBiometricoPresencial.Server
{
    /// <summary>
    /// Servidor WebSocket (Fleck) que expone ws://127.0.0.1:8443.
    /// Acepta múltiples clientes simultáneos, maneja todos los comandos del
    /// protocolo biométrico y emite heartbeat + cambios de estado en tiempo real.
    /// </summary>
    public class BiometricWebSocketServer
    {
        private const string AGENT_VERSION = "2.0.0";
        private const int HEARTBEAT_INTERVAL_SEC = 5;

        // Directorio de DLLs del RealScan SDK (necesario para opencv, tensorflow, etc.)
        private const string REALSCAN_DLL_DIR = @"C:\Program Files\Xperix\RealScanSDK\Bin\x64";

        // P/Invoke para agregar el directorio del SDK al search path de DLLs nativas
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AddDllDirectory(string lpPathName);

        private WebSocketServer? _server;
        private readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new();

        private readonly RealScanDriver _realScan = new();
        private readonly RealPassDriver _realPass = new();
        private DeviceStatusMonitor? _monitor;

        private Timer? _heartbeatTimer;

        // ─── Arranque ──────────────────────────────────────────────────────────

        public void Start(int port, bool simulationMode)
        {
            // Propagar el flag de simulación a los drivers
            _realScan.SimulationMode = simulationMode;
            _realPass.SimulationMode = simulationMode;

            if (simulationMode)
                Console.WriteLine("\n  ⚠  MODO SIMULACIÓN ACTIVO — No se usará hardware real\n");

            // Agregar el directorio de DLLs del SDK al search path ANTES de cargar el driver
            if (System.IO.Directory.Exists(REALSCAN_DLL_DIR))
            {
                SetDllDirectory(REALSCAN_DLL_DIR);
                Console.WriteLine($"[INFO] DLL search path → {REALSCAN_DLL_DIR}");
            }

            // Configurar y arrancar el monitor de periféricos (usa ProbeDevice, no Initialize)
            _monitor = new DeviceStatusMonitor(_realScan, _realPass);
            _monitor.OnStatusChanged += BroadcastDeviceStatusUpdate;
            _monitor.Start();

            // Configurar servidor Fleck
            FleckLog.Level = LogLevel.Warn;
            _server = new WebSocketServer($"ws://0.0.0.0:{port}");

            _server.Start(socket =>
            {
                socket.OnOpen = () => OnClientConnected(socket);
                socket.OnClose = () => OnClientDisconnected(socket);
                socket.OnMessage = message => ProcessMessage(socket, message);
                socket.OnError = ex => Console.WriteLine($"[WS ERROR] {socket.ConnectionInfo.Id}: {ex.Message}");
            });

            // Heartbeat periódico
            _heartbeatTimer = new Timer(
                _ => BroadcastHeartbeat(),
                null,
                TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SEC),
                TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SEC)
            );

            Console.WriteLine($"[INFO] WebSocket escuchando en ws://127.0.0.1:{port}");

            // Inicializar drivers DESPUÉS de que el WebSocket ya esté escuchando
            // Así un fallo del SDK no impide que el agente arranque
            InitializeDrivers();
        }

        public void Stop()
        {
            _heartbeatTimer?.Dispose();
            _monitor?.Stop();
            _realScan.Shutdown();
            _realPass.Shutdown();
            _server?.Dispose();
            Console.WriteLine("[INFO] Agente biométrico detenido.");
        }

        // ─── Eventos de conexión ───────────────────────────────────────────────

        private void OnClientConnected(IWebSocketConnection socket)
        {
            _clients[socket.ConnectionInfo.Id] = socket;
            Console.WriteLine($"[WS ↑] Cliente conectado: {socket.ConnectionInfo.ClientIpAddress} (ID: {socket.ConnectionInfo.Id})");

            var (rs, rp) = _monitor!.GetCurrentStatus();

            var handshake = new ConnectedHandshakeMsg
            {
                agentVersion   = AGENT_VERSION,
                simulationMode = _realScan.SimulationMode,
                devices        = BuildDevicePayload(rs, rp)
            };

            SafeSend(socket, JsonConvert.SerializeObject(handshake));
        }

        private void OnClientDisconnected(IWebSocketConnection socket)
        {
            _clients.TryRemove(socket.ConnectionInfo.Id, out _);
            Console.WriteLine($"[WS ↓] Cliente desconectado: {socket.ConnectionInfo.Id}");
        }

        // ─── Procesamiento de Comandos ─────────────────────────────────────────

        private void ProcessMessage(IWebSocketConnection socket, string rawJson)
        {
            Console.WriteLine($"[→ CMD] {rawJson}");
            try
            {
                var cmd = JsonConvert.DeserializeObject<IncomingCommand>(rawJson)
                    ?? throw new InvalidOperationException("JSON inválido.");

                string sessionId = cmd.sessionId ?? Guid.NewGuid().ToString("N")[..8].ToUpper();

                switch (cmd.command)
                {
                    case "GET_DEVICE_STATUS":
                        HandleGetDeviceStatus(socket, sessionId);
                        break;

                    case "START_FINGERPRINT_CAPTURE":
                        HandleFingerprintCapture(socket, cmd, sessionId);
                        break;

                    case "START_DOCUMENT_SCAN":
                        HandleDocumentScan(socket, cmd, sessionId);
                        break;

                    case "ABORT_CAPTURE":
                        HandleAbortCapture(socket, sessionId);
                        break;

                    default:
                        SafeSend(socket, Error(sessionId, "UNKNOWN_COMMAND", $"Comando no reconocido: '{cmd.command}'"));
                        break;
                }
            }
            catch (Exception ex)
            {
                SafeSend(socket, Error("", "PARSE_ERROR", $"Error procesando mensaje: {ex.Message}"));
            }
        }

        // ─── Handlers de Comandos ──────────────────────────────────────────────

        private void HandleGetDeviceStatus(IWebSocketConnection socket, string sessionId)
        {
            var (rs, rp) = _monitor!.GetCurrentStatus();
            var msg = new DeviceStatusUpdateMsg { devices = BuildDevicePayload(rs, rp) };
            SafeSend(socket, JsonConvert.SerializeObject(msg));
        }

        private void HandleFingerprintCapture(IWebSocketConnection socket, IncomingCommand cmd, string sessionId)
        {
            string fingerGroup = cmd.fingerGroup ?? "SLAP_4_LEFT";
            var skipFingers = cmd.skipFingers ?? new List<int>();

            Console.WriteLine($"[→ HUELLA] Grupo: {fingerGroup} | Omitir dedos: [{string.Join(",", skipFingers)}]");

            // Ejecutar en hilo separado para no bloquear el WebSocket
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = _realScan.CaptureSlap(fingerGroup, skipFingers, cmd.timeoutSeconds);

                if (result.Success)
                {
                    var msg = new FingerprintCapturedMsg
                    {
                        sessionId = sessionId,
                        data = new FingerprintData
                        {
                            fingerGroup       = result.FingerGroup,
                            nfiqQuality       = result.NfiqQuality,
                            wsqBase64         = result.WsqBase64,
                            isoTemplateBase64 = result.IsoTemplateBase64,
                            imageWidth        = result.ImageWidth,
                            imageHeight       = result.ImageHeight,
                            capturedFingers   = result.CapturedFingers,
                            skippedFingers    = result.SkippedFingers
                        }
                    };
                    SafeSend(socket, JsonConvert.SerializeObject(msg));
                }
                else
                {
                    SafeSend(socket, Error(sessionId, result.ErrorCode ?? "CAPTURE_FAILED", result.ErrorMessage ?? "Captura fallida."));
                }
            });
        }

        private void HandleDocumentScan(IWebSocketConnection socket, IncomingCommand cmd, string sessionId)
        {
            string spectralMode = cmd.spectralMode ?? "VIS";
            Console.WriteLine($"[→ DOC] Modo espectral: {spectralMode} | RFID: {cmd.readRfid}");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = _realPass.ScanDocument(spectralMode, cmd.readRfid, cmd.timeoutSeconds);

                if (result.Success)
                {
                    var msg = new DocumentScannedMsg
                    {
                        sessionId = sessionId,
                        mrz       = result.Mrz,
                        images    = result.Images
                    };
                    SafeSend(socket, JsonConvert.SerializeObject(msg));
                }
                else
                {
                    SafeSend(socket, Error(sessionId, result.ErrorCode ?? "SCAN_FAILED", result.ErrorMessage ?? "Escaneo fallido."));
                }
            });
        }

        private void HandleAbortCapture(IWebSocketConnection socket, string sessionId)
        {
            _realScan.AbortCapture();
            SafeSend(socket, JsonConvert.SerializeObject(new { event_type = "CAPTURE_ABORTED", sessionId }));
            Console.WriteLine($"[ABORT] Sesión {sessionId} — captura abortada por cliente.");
        }

        // ─── Broadcast ─────────────────────────────────────────────────────────

        private void BroadcastDeviceStatusUpdate(DevStatus rs, DevStatus rp)
        {
            // DeviceStatus del namespace Drivers, solo re-alias para usar los modelos correctos
            var msg = new DeviceStatusUpdateMsg { devices = BuildDevicePayload(rs, rp) };
            BroadcastAll(JsonConvert.SerializeObject(msg));
        }

        private void BroadcastHeartbeat()
        {
            var (rs, rp) = _monitor!.GetCurrentStatus();
            var msg = new AgentHeartbeatMsg
            {
                agentVersion   = AGENT_VERSION,
                simulationMode = _realScan.SimulationMode,
                devices        = BuildDevicePayload(rs, rp),
                timestamp      = DateTime.UtcNow.ToString("o")
            };
            BroadcastAll(JsonConvert.SerializeObject(msg));
        }

        private void BroadcastAll(string json)
        {
            foreach (var (_, socket) in _clients)
                SafeSend(socket, json);
        }

        // ─── Inicialización de Drivers ─────────────────────────────────────────

        private void InitializeDrivers()
        {
            Console.WriteLine("\n[Drivers] Inicializando controladores de hardware...");

            try
            {
                if (_realScan.Initialize(out string rsMsg))
                    Console.WriteLine($"  ✓ RealScan G10: {rsMsg}");
                else
                    Console.WriteLine($"  ✗ RealScan G10: {rsMsg}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ RealScan G10: Excepción al inicializar — {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("     El agente continúa sin acceso al RealScan G10.");
            }

            try
            {
                if (_realPass.Initialize(out string rpMsg))
                    Console.WriteLine($"  ✓ RealPass RPNF: {rpMsg}");
                else
                    Console.WriteLine($"  ✗ RealPass RPNF: {rpMsg}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ RealPass RPNF: Excepción al inicializar — {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("     El agente continúa sin acceso al RealPass RPNF.");
            }

            Console.WriteLine();
        }

        // ─── Helpers ───────────────────────────────────────────────────────────

        private static DeviceStatusPayload BuildDevicePayload(DevStatus rs, DevStatus rp) =>
            new()
            {
                realScanG10 = new DeviceStatusItem
                {
                    isConnected    = rs.IsConnected,
                    isSimulated    = rs.IsSimulated,
                    isBusy         = rs.IsBusy,
                    statusCode     = rs.StatusCode,
                    statusMessage  = rs.StatusMessage,
                    firmwareVersion = rs.FirmwareVersion,
                    serialNumber   = rs.SerialNumber,
                    driverFound    = rs.DriverFound,
                    lastCheckedAt  = rs.LastCheckedAt.ToString("o")
                },
                realPassRPNF = new DeviceStatusItem
                {
                    isConnected    = rp.IsConnected,
                    isSimulated    = rp.IsSimulated,
                    isBusy         = rp.IsBusy,
                    statusCode     = rp.StatusCode,
                    statusMessage  = rp.StatusMessage,
                    firmwareVersion = rp.FirmwareVersion,
                    serialNumber   = rp.SerialNumber,
                    driverFound    = rp.DriverFound,
                    lastCheckedAt  = rp.LastCheckedAt.ToString("o")
                }
            };

        private static string Error(string sessionId, string code, string message) =>
            JsonConvert.SerializeObject(new CaptureErrorMsg
            {
                sessionId = sessionId,
                errorCode = code,
                message   = message
            });

        private static void SafeSend(IWebSocketConnection socket, string json)
        {
            try
            {
                if (socket.IsAvailable)
                    socket.Send(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS SEND ERROR] {ex.Message}");
            }
        }
    }
}
