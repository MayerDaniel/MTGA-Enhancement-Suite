using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MTGAEnhancementSuite.UrlScheme
{
    /// <summary>
    /// Cross-platform IPC server using TCP on localhost.
    /// Replaces Windows-only named pipes for inter-process URL forwarding.
    /// </summary>
    internal static class TcpIpcServer
    {
        private const int Port = 49164;

        private static Thread _serverThread;
        private static TcpListener _listener;
        private static volatile bool _running;

        /// <summary>
        /// Callback invoked (on background thread) when a URL is received.
        /// Caller is responsible for dispatching to the main thread.
        /// </summary>
        public static Action<string> OnUrlReceived;

        /// <summary>
        /// Starts the TCP IPC server on a background thread.
        /// </summary>
        public static void Start()
        {
            if (_serverThread != null && _serverThread.IsAlive)
                return;

            _running = true;
            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "MTGAES_TcpIpc"
            };
            _serverThread.Start();
            Plugin.Log.LogInfo("TCP IPC server started on port " + Port);
        }

        /// <summary>
        /// Stops the TCP IPC server.
        /// </summary>
        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); }
            catch { }
        }

        private static void ServerLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
            }
            catch (SocketException ex)
            {
                // Port already in use — another instance has the server
                Plugin.Log.LogInfo($"TCP IPC port {Port} already in use: {ex.Message}");
                return;
            }

            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    if (!_running)
                    {
                        client.Close();
                        break;
                    }

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            using (client)
                            using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8))
                            {
                                var message = reader.ReadLine();
                                if (!string.IsNullOrEmpty(message))
                                {
                                    Plugin.Log.LogInfo($"Received URL via TCP: {message}");
                                    BringGameToFront();
                                    OnUrlReceived?.Invoke(message);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (_running)
                                Plugin.Log.LogWarning($"TCP IPC client error: {ex.Message}");
                        }
                    });
                }
                catch (SocketException)
                {
                    // Listener stopped
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        Plugin.Log.LogWarning($"TCP IPC server error: {ex.Message}");
                }
            }

            try { _listener?.Stop(); }
            catch { }
        }

        /// <summary>
        /// Brings the MTGA game window to the foreground.
        /// Delegates to shared WindowHelper.
        /// </summary>
        public static void BringGameToFront()
        {
            Helpers.WindowHelper.BringToFront();
        }

        /// <summary>
        /// Tries to send a URL to an existing TCP IPC server (another MTGA instance).
        /// Returns true if successful.
        /// </summary>
        public static bool TrySendToExisting(string url)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, Port);

                    using (var writer = new StreamWriter(client.GetStream(), Encoding.UTF8))
                    {
                        writer.WriteLine(url);
                        writer.Flush();
                    }
                }

                Plugin.Log.LogInfo($"Sent URL to existing instance via TCP: {url}");
                return true;
            }
            catch
            {
                // No existing server — we're the primary instance
                return false;
            }
        }
    }
}
