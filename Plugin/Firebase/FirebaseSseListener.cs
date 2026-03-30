using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using MTGAEnhancementSuite.UI;
using Newtonsoft.Json.Linq;

namespace MTGAEnhancementSuite.Firebase
{
    /// <summary>
    /// Listens to Firebase Realtime Database changes via Server-Sent Events (SSE).
    /// Runs on a background thread; dispatches events to the main thread via MainThreadDispatcher.
    /// </summary>
    internal class FirebaseSseListener : IDisposable
    {
        private static FirebaseSseListener _instance;
        public static FirebaseSseListener Instance => _instance;

        private Thread _thread;
        private volatile bool _running;
        private string _path;
        private string _authToken;
        private int _reconnectDelayMs = 1000;
        private const int MaxReconnectDelayMs = 30000;

        /// <summary>
        /// Fired on the MAIN THREAD when data changes at the listened path.
        /// Args: (string path, JToken data) — path is relative (e.g., "/format").
        /// </summary>
        public event Action<string, JToken> OnDataChanged;

        public FirebaseSseListener()
        {
            _instance = this;
        }

        public void Start(string databasePath, string authToken)
        {
            Stop();

            _path = databasePath;
            _authToken = authToken;
            _running = true;
            _reconnectDelayMs = 1000;

            _thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "FirebaseSSE"
            };
            _thread.Start();

            Plugin.Log.LogInfo($"SSE listener started for /{databasePath}");
        }

        public void Stop()
        {
            _running = false;

            if (_thread != null && _thread.IsAlive)
            {
                _thread.Abort();
                _thread = null;
            }

            Plugin.Log.LogInfo("SSE listener stopped");
        }

        public void UpdateAuthToken(string newToken)
        {
            _authToken = newToken;
        }

        public void Dispose()
        {
            Stop();
            OnDataChanged = null;
            if (_instance == this) _instance = null;
        }

        private void ListenLoop()
        {
            // Enable TLS 1.2 for Mono compatibility
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SSE: Could not set TLS: {ex.Message}");
            }

            while (_running)
            {
                try
                {
                    var config = FirebaseConfig.Instance;
                    var baseUrl = config.DatabaseUrl.TrimEnd('/');
                    var url = $"{baseUrl}/{_path}.json?auth={_authToken}";

                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Accept = "text/event-stream";
                    request.AllowAutoRedirect = true;
                    request.ReadWriteTimeout = 90000; // 90s — Firebase sends keepalive every 30s
                    request.Timeout = 30000;

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream))
                    {
                        // Connection succeeded — reset backoff
                        _reconnectDelayMs = 1000;
                        Plugin.Log.LogInfo($"SSE: connected to /{_path}");

                        string eventType = null;

                        while (_running && !reader.EndOfStream)
                        {
                            var line = reader.ReadLine();
                            if (line == null) break;

                            if (line.StartsWith("event:"))
                            {
                                eventType = line.Substring(6).Trim();
                            }
                            else if (line.StartsWith("data:"))
                            {
                                var dataStr = line.Substring(5).Trim();

                                if (eventType == "put" || eventType == "patch")
                                {
                                    ProcessSseData(dataStr);
                                }
                                // "keep-alive" events have data: null — ignore

                                eventType = null;
                            }
                            // Empty lines separate events — no action needed
                        }
                    }
                }
                catch (WebException wex)
                {
                    if (!_running) break;

                    var httpResp = wex.Response as HttpWebResponse;
                    if (httpResp != null && (httpResp.StatusCode == HttpStatusCode.Unauthorized ||
                                             httpResp.StatusCode == HttpStatusCode.Forbidden))
                    {
                        Plugin.Log.LogWarning("SSE: Auth expired (401/403), requesting re-auth");
                        MainThreadDispatcher.Enqueue(() =>
                        {
                            Toast.Warning("Firebase session expired, reconnecting...");
                        });
                        // Wait longer before retry — auth refresh takes time
                        Thread.Sleep(10000);
                        continue;
                    }

                    Plugin.Log.LogWarning($"SSE: Connection error: {wex.Message}");
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Plugin.Log.LogWarning($"SSE: Error: {ex.Message}");
                }

                if (!_running) break;

                // Exponential backoff
                Plugin.Log.LogInfo($"SSE: Reconnecting in {_reconnectDelayMs}ms...");
                Thread.Sleep(_reconnectDelayMs);
                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, MaxReconnectDelayMs);
            }
        }

        private void ProcessSseData(string dataStr)
        {
            if (string.IsNullOrEmpty(dataStr) || dataStr == "null") return;

            try
            {
                var json = JObject.Parse(dataStr);
                var path = json["path"]?.ToString() ?? "/";
                var data = json["data"];

                MainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        OnDataChanged?.Invoke(path, data);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"SSE: Handler error: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SSE: Failed to parse data: {ex.Message}");
            }
        }

    }
}
