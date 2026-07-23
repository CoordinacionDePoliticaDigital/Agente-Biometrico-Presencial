using System;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AgenteBiometricoPresencial.Drivers;

namespace AgenteBiometricoPresencial.Server
{
    public class BiometricWebSocketServer
    {
        private WebSocketServer? _server;
        private readonly RealScanDriver _realScanDriver = new();
        private readonly RealPassDriver _realPassDriver = new();

        public void Start(int port = 8443)
        {
            FleckLog.Level = LogLevel.Info;
            _server = new WebSocketServer($"ws://0.0.0.0:{port}");

            Console.WriteLine($"[INFO] Iniciando Agente Biométrico WebSocket en ws://127.0.0.1:{port}...");

            // Inicializar controladores físicos
            if (_realScanDriver.Initialize(out string rsMsg))
                Console.WriteLine($"[RealScan G10] {rsMsg}");
            else
                Console.WriteLine($"[RealScan G10 WARNING] {rsMsg}");

            if (_realPassDriver.Initialize(out string rpMsg))
                Console.WriteLine($"[RealPass RPNF] {rpMsg}");
            else
                Console.WriteLine($"[RealPass RPNF WARNING] {rpMsg}");

            _server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    Console.WriteLine($"[WS OPEN] Cliente conectado desde {socket.ConnectionInfo.ClientIpAddress}");
                    var handshake = new
                    {
                        event_type = "CONNECTED_HANDSHAKE",
                        status = "READY",
                        agentVersion = "1.0.0",
                        devices = new
                        {
                            realScanG10 = true,
                            realPassRPNF = _realPassDriver.IsConnected
                        }
                    };
                    socket.Send(JsonConvert.SerializeObject(handshake));
                };

                socket.OnClose = () => Console.WriteLine("[WS CLOSE] Cliente desconectado.");

                socket.OnMessage = message =>
                {
                    Console.WriteLine($"[WS MSG RECV]: {message}");
                    ProcessMessage(socket, message);
                };
            });
        }

        private void ProcessMessage(IWebSocketConnection socket, string rawJson)
        {
            try
            {
                var request = JObject.Parse(rawJson);
                string command = request["command"]?.ToString() ?? "";
                string sessionId = request["sessionId"]?.ToString() ?? Guid.NewGuid().ToString();

                switch (command)
                {
                    case "START_FINGERPRINT_CAPTURE":
                        socket.Send(JsonConvert.SerializeObject(new
                        {
                            event_type = "FINGERPRINT_CAPTURED",
                            sessionId = sessionId,
                            status = "SUCCESS",
                            data = new
                            {
                                fingerType = request["fingerType"]?.ToString() ?? "SLAP_4_LEFT",
                                nfiqQuality = 1,
                                wsqBase64 = "U29mdHdhcmUgV1NRIChpbWFnZW4gZHVtbXkgZGUgcHJ1ZWJhKQ==",
                                isoTemplateBase64 = "Rk1SMDAyMDIw..."
                            }
                        }));
                        break;

                    case "START_DOCUMENT_SCAN":
                        socket.Send(JsonConvert.SerializeObject(new
                        {
                            event_type = "DOCUMENT_SCANNED",
                            sessionId = sessionId,
                            status = "SUCCESS",
                            mrz = new
                            {
                                documentType = "P",
                                country = "MEX",
                                surname = "CASTILLO MARQUEZ",
                                givenNames = "PRUEBA MARIA DEL CARMEN",
                                documentNumber = "G12345678",
                                curp = "CAMC030110MCHSRRA9",
                                sex = "F"
                            }
                        }));
                        break;

                    default:
                        socket.Send(JsonConvert.SerializeObject(new
                        {
                            event_type = "ERROR",
                            message = $"Comando no reconocido: '{command}'"
                        }));
                        break;
                }
            }
            catch (Exception ex)
            {
                socket.Send(JsonConvert.SerializeObject(new
                {
                    event_type = "ERROR",
                    message = $"Error procesando JSON: {ex.Message}"
                }));
            }
        }
    }
}
