using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
        private const string AGENT_VERSION = "3.0.0";
        private const int HEARTBEAT_INTERVAL_SEC = 5;

        private const string REALSCAN_DLL_DIR = @"C:\Program Files\Xperix\RealScanSDK\Bin\x64";
        private const string REALPASS_DLL_DIR = @"C:\Program Files\Xperix\RealPassSDK\Bin\x64";

        private WebSocketServer _server;
        private readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new ConcurrentDictionary<Guid, IWebSocketConnection>();

        private readonly RealScanDriver _realScan = new RealScanDriver();
        private readonly RealPassDriver _realPass = new RealPassDriver();
        private DeviceStatusMonitor _monitor;

        private System.Threading.Timer _heartbeatTimer;

        // ─── Arranque ──────────────────────────────────────────────────────────

        public void Start(int port, bool simulationMode)
        {
            // Propagar el flag de simulación a los drivers
            _realScan.SimulationMode = simulationMode;
            _realPass.SimulationMode = simulationMode;

            if (simulationMode)
                Console.WriteLine("\n  ⚠  MODO SIMULACIÓN ACTIVO — No se usará hardware real\n");

            // Configurar y arrancar el monitor de periféricos
            _monitor = new DeviceStatusMonitor(_realScan, _realPass);
            _monitor.OnStatusChanged += BroadcastDeviceStatusUpdate;
            _monitor.Start();

            // Configurar servidor Fleck
            FleckLog.Level = LogLevel.Warn;
            _server = new WebSocketServer(string.Format("ws://0.0.0.0:{0}", port));

            _server.Start(socket =>
            {
                socket.OnOpen = () => OnClientConnected(socket);
                socket.OnClose = () => OnClientDisconnected(socket);
                socket.OnMessage = message => ProcessMessage(socket, message);
                socket.OnError = ex => Console.WriteLine(string.Format("[WS ERROR] {0}: {1}", socket.ConnectionInfo.Id, ex.Message));
            });

            // Heartbeat periódico
            _heartbeatTimer = new System.Threading.Timer(
                _ => BroadcastHeartbeat(),
                null,
                TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SEC),
                TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_SEC)
            );

            Console.WriteLine(string.Format("[INFO] WebSocket escuchando en ws://127.0.0.1:{0}", port));

            // Inicializar drivers
            InitializeDrivers();
        }

        public void Stop()
        {
            if (_heartbeatTimer != null) _heartbeatTimer.Dispose();
            if (_monitor != null) _monitor.Stop();
            _realScan.Shutdown();
            _realPass.Shutdown();
            if (_server != null) _server.Dispose();
            Console.WriteLine("[INFO] Agente biométrico detenido.");
        }

        // ─── Eventos de conexión ───────────────────────────────────────────────

        private void OnClientConnected(IWebSocketConnection socket)
        {
            _clients[socket.ConnectionInfo.Id] = socket;
            Console.WriteLine(string.Format("[WS ↑] Cliente conectado: {0} (ID: {1})", socket.ConnectionInfo.ClientIpAddress, socket.ConnectionInfo.Id));

            var statusTuple = _monitor.GetCurrentStatus();
            var rs = statusTuple.Item1;
            var rp = statusTuple.Item2;

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
            IWebSocketConnection removed;
            _clients.TryRemove(socket.ConnectionInfo.Id, out removed);
            Console.WriteLine(string.Format("[WS ↓] Cliente desconectado: {0}", socket.ConnectionInfo.Id));
        }

        // ─── Procesamiento de Comandos ─────────────────────────────────────────

        private void ProcessMessage(IWebSocketConnection socket, string rawJson)
        {
            Console.WriteLine(string.Format("[→ CMD] {0}", rawJson));
            try
            {
                var cmd = JsonConvert.DeserializeObject<IncomingCommand>(rawJson);
                if (cmd == null)
                    throw new InvalidOperationException("JSON inválido.");

                string sessionId = cmd.sessionId;
                if (string.IsNullOrEmpty(sessionId))
                    sessionId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();

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

                    case "START_FULL_BIOMETRIC_CAPTURE":
                        HandleFullBiometricCapture(socket, cmd, sessionId);
                        break;

                    case "ABORT_CAPTURE":
                        HandleAbortCapture(socket, sessionId);
                        break;

                    default:
                        SafeSend(socket, Error(sessionId, "UNKNOWN_COMMAND", string.Format("Comando no reconocido: '{0}'", cmd.command)));
                        break;
                }
            }
            catch (Exception ex)
            {
                SafeSend(socket, Error("", "PARSE_ERROR", string.Format("Error procesando mensaje: {0}", ex.Message)));
            }
        }

        // ─── Handlers de Comandos ──────────────────────────────────────────────

        private void HandleGetDeviceStatus(IWebSocketConnection socket, string sessionId)
        {
            var statusTuple = _monitor.GetCurrentStatus();
            var msg = new DeviceStatusUpdateMsg { devices = BuildDevicePayload(statusTuple.Item1, statusTuple.Item2) };
            SafeSend(socket, JsonConvert.SerializeObject(msg));
        }

        private void HandleFingerprintCapture(IWebSocketConnection socket, IncomingCommand cmd, string sessionId)
        {
            string fingerGroup = cmd.fingerGroup != null ? cmd.fingerGroup : "SLAP_4_LEFT";
            var skipFingers = cmd.skipFingers != null ? cmd.skipFingers : new List<int>();

            Console.WriteLine(string.Format("[→ HUELLA] Grupo: {0} | Omitir dedos: [{1}]", fingerGroup, string.Join(",", skipFingers)));

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
                    SafeSend(socket, Error(sessionId, result.ErrorCode != null ? result.ErrorCode : "CAPTURE_FAILED", result.ErrorMessage != null ? result.ErrorMessage : "Captura fallida."));
                }
            });
        }

        private void HandleDocumentScan(IWebSocketConnection socket, IncomingCommand cmd, string sessionId)
        {
            string spectralMode = cmd.spectralMode != null ? cmd.spectralMode : "VIS";
            Console.WriteLine(string.Format("[→ DOC] Modo espectral: {0} | RFID: {1}", spectralMode, cmd.readRfid));

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
                    SafeSend(socket, Error(sessionId, result.ErrorCode != null ? result.ErrorCode : "SCAN_FAILED", result.ErrorMessage != null ? result.ErrorMessage : "Escaneo fallido."));
                }
            });
        }

        private void HandleFullBiometricCapture(IWebSocketConnection socket, IncomingCommand cmd, string sessionId)
        {
            Console.WriteLine("[→ NATIVE] Iniciando Captura Completa nativa en Windows Forms.");

            Thread uiThread = new Thread(() =>
            {
                try
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                    var form = new AgenteBiometricoPresencial.UI.CaptureForm(
                        _realScan, _realPass,
                        cmd.mobileLivenessUrl != null ? cmd.mobileLivenessUrl : "http://192.168.11.53:3001");
                    if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var payload = new
                        {
                            documentFront = form.DocResultFront,
                            documentBack = form.DocResultBack,
                            fingerLeft = form.FingerLeft,
                            fingerRight = form.FingerRight,
                            fingerThumbs = form.FingerThumbs,
                            faceImageBase64 = form.FaceImageBase64
                        };
                        string jsonPayload = JsonConvert.SerializeObject(payload);

                        string encryptedBase64 = "";
                        string ivBase64 = "";

                        if (!string.IsNullOrEmpty(cmd.encryptionKeyBase64))
                        {
                            using (var aes = Aes.Create())
                            {
                                aes.Key = Convert.FromBase64String(cmd.encryptionKeyBase64);
                                aes.GenerateIV();
                                ivBase64 = Convert.ToBase64String(aes.IV);

                                var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                                using (var ms = new MemoryStream())
                                {
                                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                                    using (var sw = new StreamWriter(cs))
                                    {
                                        sw.Write(jsonPayload);
                                    }
                                    encryptedBase64 = Convert.ToBase64String(ms.ToArray());
                                }
                            }
                        }
                        else
                        {
                            encryptedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonPayload));
                        }

                        var msg = new FullBiometricResultMsg
                        {
                            sessionId = sessionId,
                            encryptedPayloadBase64 = encryptedBase64,
                            ivBase64 = ivBase64
                        };
                        SafeSend(socket, JsonConvert.SerializeObject(msg));
                    }
                    else
                    {
                        SafeSend(socket, Error(sessionId, "CAPTURE_CANCELLED", "Captura biométrica cancelada por el usuario."));
                    }
                }
                catch (Exception ex)
                {
                    SafeSend(socket, Error(sessionId, "UI_ERROR", string.Format("Error en UI nativa: {0}", ex.Message)));
                }
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
        }

        private void HandleAbortCapture(IWebSocketConnection socket, string sessionId)
        {
            _realScan.AbortCapture();
            SafeSend(socket, JsonConvert.SerializeObject(new { event_type = "CAPTURE_ABORTED", sessionId = sessionId }));
            Console.WriteLine(string.Format("[ABORT] Sesión {0} — captura abortada por cliente.", sessionId));
        }

        // ─── Broadcast ─────────────────────────────────────────────────────────

        private void BroadcastDeviceStatusUpdate(DevStatus rs, DevStatus rp)
        {
            var msg = new DeviceStatusUpdateMsg { devices = BuildDevicePayload(rs, rp) };
            BroadcastAll(JsonConvert.SerializeObject(msg));
        }

        private void BroadcastHeartbeat()
        {
            var statusTuple = _monitor.GetCurrentStatus();
            var msg = new AgentHeartbeatMsg
            {
                agentVersion   = AGENT_VERSION,
                simulationMode = _realScan.SimulationMode,
                devices        = BuildDevicePayload(statusTuple.Item1, statusTuple.Item2),
                timestamp      = DateTime.UtcNow.ToString("o")
            };
            BroadcastAll(JsonConvert.SerializeObject(msg));
        }

        private void BroadcastAll(string json)
        {
            foreach (var kvp in _clients)
                SafeSend(kvp.Value, json);
        }

        // ─── Inicialización de Drivers ─────────────────────────────────────────

        private void InitializeDrivers()
        {
            Console.WriteLine("\n[Drivers] Inicializando controladores de hardware...");

            try
            {
                string rsMsg;
                if (_realScan.Initialize(out rsMsg))
                    Console.WriteLine(string.Format("  ✓ RealScan G10: {0}", rsMsg));
                else
                    Console.WriteLine(string.Format("  ✗ RealScan G10: {0}", rsMsg));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("  ✗ RealScan G10: Excepción al inicializar — {0}: {1}", ex.GetType().Name, ex.Message));
                Console.WriteLine("     El agente continúa sin acceso al RealScan G10.");
            }

            try
            {
                string rpMsg;
                if (_realPass.Initialize(out rpMsg))
                    Console.WriteLine(string.Format("  ✓ RealPass RPNF: {0}", rpMsg));
                else
                    Console.WriteLine(string.Format("  ✗ RealPass RPNF: {0}", rpMsg));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("  ✗ RealPass RPNF: Excepción al inicializar — {0}: {1}", ex.GetType().Name, ex.Message));
                Console.WriteLine("     El agente continúa sin acceso al RealPass RPNF.");
            }

            Console.WriteLine();
        }

        // ─── Helpers ───────────────────────────────────────────────────────────

        private static DeviceStatusPayload BuildDevicePayload(DevStatus rs, DevStatus rp)
        {
            return new DeviceStatusPayload
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
        }

        private static string Error(string sessionId, string code, string message)
        {
            return JsonConvert.SerializeObject(new CaptureErrorMsg
            {
                sessionId = sessionId,
                errorCode = code,
                message   = message
            });
        }

        private static void SafeSend(IWebSocketConnection socket, string json)
        {
            try
            {
                if (socket != null && socket.IsAvailable)
                    socket.Send(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("[WS SEND ERROR] {0}", ex.Message));
            }
        }
    }
}
