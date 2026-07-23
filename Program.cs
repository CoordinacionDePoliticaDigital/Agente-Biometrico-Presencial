using System;
using AgenteBiometricoPresencial.Server;

namespace AgenteBiometricoPresencial
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("  Agente Biométrico Presencial - Autoridad Certificadora ");
            Console.WriteLine("  Middleware WebSocket para RealScan G10 & RealPass RPNF  ");
            Console.WriteLine("==========================================================");

            var server = new BiometricWebSocketServer();
            server.Start(8443);

            Console.WriteLine("\n[INFO] Presiona cualquier tecla o CTRL+C para detener el servicio.");
            Console.ReadLine();
        }
    }
}
