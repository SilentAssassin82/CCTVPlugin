using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace CCTVTester
{
    class Program
    {
        static void Main(string[] args)
        {
            string host = "localhost";
            int port = 12345;

            if (args.Length >= 1)
                host = args[0];
            if (args.Length >= 2)
                int.TryParse(args[1], out port);

            Console.WriteLine($"Connecting to {host}:{port}...");

            try
            {
                using (var client = new TcpClient(host, port))
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                {
                    Console.WriteLine("Connected! Type commands (or 'quit' to exit):\n");

                    // Background thread to read responses
                    var readTask = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            while (client.Connected)
                            {
                                string line = reader.ReadLine();
                                if (line != null)
                                {
                                    Console.WriteLine($"<< {line}");
                                }
                            }
                        }
                        catch { }
                    });

                    // Main loop for sending commands
                    while (true)
                    {
                        Console.Write(">> ");
                        string command = Console.ReadLine();

                        if (string.IsNullOrWhiteSpace(command))
                            continue;

                        if (command.Equals("quit", StringComparison.OrdinalIgnoreCase))
                            break;

                        writer.WriteLine(command);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nDisconnected. Press any key to exit...");
            Console.ReadKey();
        }
    }
}
