using System;
using System.Collections;
using System.Text;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MTGAEnhancementSuite.Firebase
{
    internal class FirebaseClient : MonoBehaviour
    {
        private static FirebaseClient _instance;
        private string _idToken;
        private string _refreshToken;
        private bool _isAuthenticated;
        private Coroutine _heartbeatCoroutine;
        private Coroutine _tokenRefreshCoroutine;
        private string _cachedPersonaId;
        private string _cachedDisplayName;

        public bool IsAuthenticated => _isAuthenticated;

        public static FirebaseClient Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("FirebaseClient");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<FirebaseClient>();
                }
                return _instance;
            }
        }

        public void SignInWithMTGAIdentity(string personaId, string displayName, Action<bool> callback)
        {
            StartCoroutine(SignInWithMTGAIdentityCoroutine(personaId, displayName, callback));
        }

        private IEnumerator SignInWithMTGAIdentityCoroutine(string personaId, string displayName,
            Action<bool> callback)
        {
            _cachedPersonaId = personaId;
            _cachedDisplayName = displayName;
            var config = FirebaseConfig.Instance;

            var tokenUrl = $"{config.FunctionUrl}/getAuthToken";
            var tokenPayload = new JObject
            {
                ["personaId"] = personaId,
                ["displayName"] = displayName
            };

            string customToken = null;

            using (var request = new UnityWebRequest(tokenUrl, "POST"))
            {
                var body = Encoding.UTF8.GetBytes(tokenPayload.ToString(Formatting.None));
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    var response = JObject.Parse(request.downloadHandler.text);
                    customToken = response["token"]?.ToString();
                }
                else
                {
                    Plugin.Log.LogError($"Failed to get custom token: {request.error} - {request.downloadHandler.text}");
                    _isAuthenticated = false;
                    callback?.Invoke(false);
                    yield break;
                }
            }

            if (string.IsNullOrEmpty(customToken))
            {
                Plugin.Log.LogError("Received empty custom token");
                _isAuthenticated = false;
                callback?.Invoke(false);
                yield break;
            }

            var signInUrl = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={config.ApiKey}";
            var signInPayload = new JObject
            {
                ["token"] = customToken,
                ["returnSecureToken"] = true
            };

            using (var request = new UnityWebRequest(signInUrl, "POST"))
            {
                var body = Encoding.UTF8.GetBytes(signInPayload.ToString(Formatting.None));
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    var response = JObject.Parse(request.downloadHandler.text);
                    _idToken = response["idToken"]?.ToString();
                    _refreshToken = response["refreshToken"]?.ToString();
                    _isAuthenticated = true;
                    Plugin.Log.LogInfo($"Firebase auth successful for {displayName} (token length={_idToken?.Length}, refresh={!string.IsNullOrEmpty(_refreshToken)})");
                    StartProactiveTokenRefresh();

                    // Fetch available formats from Firebase (once)
                    if (!ChallengeFormatState.FormatsLoaded)
                    {
                        FetchFormatList();
                    }

                    callback?.Invoke(true);
                }
                else
                {
                    Plugin.Log.LogError($"Firebase signIn failed: {request.error} - {request.downloadHandler.text}");
                    _isAuthenticated = false;
                    callback?.Invoke(false);
                }
            }
        }

        /// <summary>
        /// Register a challenge lobby in the Realtime Database.
        /// </summary>
        public void RegisterLobby(string challengeId, string format, string hostDisplayName,
            string hostPlayerId, string matchType, bool isPublic, Action<bool> callback,
            bool isBestOf3 = false)
        {
            StartCoroutine(RegisterLobbyCoroutine(challengeId, format, hostDisplayName,
                hostPlayerId, matchType, isPublic, callback, isBestOf3));
        }

        private IEnumerator RegisterLobbyCoroutine(string challengeId, string format,
            string hostDisplayName, string hostPlayerId, string matchType,
            bool isPublic, Action<bool> callback, bool isBestOf3)
        {
            if (!EnsureAuthenticated())
            {
                Toast.Warning("Not connected \u2014 lobby won't be registered.");
                callback?.Invoke(false);
                yield break;
            }

            var config = FirebaseConfig.Instance;
            var baseUrl = config.DatabaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/lobbies/{challengeId}.json?auth={_idToken}";

            // Strip discriminator from display name for public display
            var publicName = ChallengeFormatState.StripDiscriminator(hostDisplayName);

            var lobbyData = new JObject
            {
                ["format"] = format,
                ["hostDisplayName"] = publicName,
                ["hostFullName"] = hostDisplayName,
                ["hostPlayerId"] = hostPlayerId,
                ["matchType"] = matchType,
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["lastHeartbeat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["status"] = "open",
                ["isPublic"] = isPublic,
                ["isBestOf3"] = isBestOf3
            };

            var json = lobbyData.ToString(Formatting.None);

            using (var request = new UnityWebRequest(url, "PUT"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    Plugin.Log.LogInfo($"Lobby registered: {challengeId} ({format}, public={isPublic})");
                    callback?.Invoke(true);
                }
                else
                {
                    HandleApiError("Register Lobby", request);
                    callback?.Invoke(false);
                }
            }
        }

        /// <summary>
        /// Send a heartbeat for the active lobby.
        /// </summary>
        public void StartHeartbeat(string challengeId)
        {
            if (_heartbeatCoroutine != null)
                StopCoroutine(_heartbeatCoroutine);

            _heartbeatCoroutine = StartCoroutine(HeartbeatLoop(challengeId));
            Plugin.Log.LogInfo($"Heartbeat started for lobby {challengeId}");
        }

        public void StopHeartbeat()
        {
            if (_heartbeatCoroutine != null)
            {
                StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
                Plugin.Log.LogInfo("Heartbeat stopped");
            }
        }

        private IEnumerator HeartbeatLoop(string challengeId)
        {
            while (true)
            {
                yield return new WaitForSeconds(60f);

                if (!EnsureAuthenticated()) continue;

                var config = FirebaseConfig.Instance;
                var baseUrl = config.DatabaseUrl.TrimEnd('/');
                var url = $"{baseUrl}/lobbies/{challengeId}/lastHeartbeat.json?auth={_idToken}";
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

                using (var request = new UnityWebRequest(url, "PUT"))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(timestamp));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    yield return request.SendWebRequest();
                }
            }
        }

        /// <summary>
        /// PATCH a lobby — update specific fields without overwriting the whole record.
        /// </summary>
        public void PatchLobby(string challengeId, string jsonFields, Action<bool> callback = null)
        {
            StartCoroutine(PatchLobbyCoroutine(challengeId, jsonFields, callback));
        }

        private IEnumerator PatchLobbyCoroutine(string challengeId, string jsonFields, Action<bool> callback)
        {
            if (!EnsureAuthenticated())
            {
                Plugin.Log.LogWarning($"PatchLobby: not authenticated, attempting refresh first...");
                bool refreshDone = false;
                bool refreshSuccess = false;
                RefreshTokenIfNeeded(() =>
                {
                    refreshSuccess = _isAuthenticated;
                    refreshDone = true;
                });
                while (!refreshDone) yield return null;

                if (!refreshSuccess)
                {
                    Plugin.Log.LogError("PatchLobby: auth refresh failed, aborting");
                    callback?.Invoke(false);
                    yield break;
                }
            }

            var config = FirebaseConfig.Instance;
            var baseUrl = config.DatabaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/lobbies/{challengeId}.json?auth={_idToken}";

            Plugin.Log.LogInfo($"PatchLobby: {challengeId} token_len={_idToken?.Length} fields={jsonFields}");

            using (var request = new UnityWebRequest(url, "PATCH"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonFields));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                var success = request.responseCode == 200;
                if (!success)
                    HandleApiError("Patch Lobby", request);

                callback?.Invoke(success);
            }
        }

        /// <summary>
        /// Set a lobby to private (remove from public browser listing).
        /// </summary>
        public void SetLobbyPrivate(string challengeId, Action<bool> callback = null)
        {
            PatchLobby(challengeId, "{\"isPublic\":false}", callback);
        }

        /// <summary>
        /// Update the format on a lobby (host changed format).
        /// </summary>
        public void UpdateLobbyFormat(string challengeId, string format, Action<bool> callback = null)
        {
            var json = $"{{\"format\":\"{format}\"}}";
            PatchLobby(challengeId, json, callback);
        }

        public void UpdateLobbyBestOf(string challengeId, bool isBestOf3, Action<bool> callback = null)
        {
            var json = $"{{\"isBestOf3\":{(isBestOf3 ? "true" : "false")}}}";
            PatchLobby(challengeId, json, callback);
        }

        /// <summary>
        /// Delete a lobby from Firebase.
        /// </summary>
        public void DeleteLobby(string challengeId, Action<bool> callback = null)
        {
            StartCoroutine(DeleteLobbyCoroutine(challengeId, callback));
        }

        private IEnumerator DeleteLobbyCoroutine(string challengeId, Action<bool> callback)
        {
            if (!EnsureAuthenticated())
            {
                callback?.Invoke(false);
                yield break;
            }

            var config = FirebaseConfig.Instance;
            var baseUrl = config.DatabaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/lobbies/{challengeId}.json?auth={_idToken}";

            using (var request = UnityWebRequest.Delete(url))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                yield return request.SendWebRequest();

                var success = request.responseCode == 200;
                if (success)
                    Plugin.Log.LogInfo($"Lobby {challengeId} deleted from Firebase");
                else
                    Plugin.Log.LogWarning($"Failed to delete lobby {challengeId}");

                callback?.Invoke(success);
            }
        }

        /// <summary>
        /// Fetch all public lobbies from Firebase.
        /// </summary>
        public void ListPublicLobbies(Action<JObject> callback)
        {
            StartCoroutine(ListPublicLobbiesCoroutine(callback));
        }

        private IEnumerator ListPublicLobbiesCoroutine(Action<JObject> callback)
        {
            if (!EnsureAuthenticated())
            {
                callback?.Invoke(null);
                yield break;
            }

            var config = FirebaseConfig.Instance;
            var baseUrl = config.DatabaseUrl.TrimEnd('/');
            // Fetch all lobbies — filter client-side for isPublic and freshness
            var url = $"{baseUrl}/lobbies.json?auth={_idToken}";

            using (var request = UnityWebRequest.Get(url))
            {
                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    var text = request.downloadHandler.text;
                    if (text == "null" || string.IsNullOrEmpty(text))
                    {
                        callback?.Invoke(null);
                    }
                    else
                    {
                        var data = JObject.Parse(text);
                        callback?.Invoke(data);
                    }
                }
                else
                {
                    HandleApiError("List Lobbies", request);
                    callback?.Invoke(null);
                }
            }
        }

        /// <summary>
        /// Generic authenticated GET request to the database.
        /// </summary>
        /// <summary>
        /// Fetches the format list from /formatList in Firebase and populates ChallengeFormatState.
        /// </summary>
        public void FetchFormatList()
        {
            DatabaseGet("formatList", data =>
            {
                if (data == null || data.Type == JTokenType.Null)
                {
                    Plugin.Log.LogWarning("No format list found in Firebase — using defaults");
                    // Fall back to just Pauper
                    ChallengeFormatState.SetFormats(
                        new System.Collections.Generic.List<string> { "pauper" },
                        new System.Collections.Generic.List<string> { "Pauper" }
                    );
                    return;
                }

                var keys = new System.Collections.Generic.List<string>();
                var names = new System.Collections.Generic.List<string>();

                foreach (var prop in ((JObject)data).Properties())
                {
                    keys.Add(prop.Name);
                    var displayName = prop.Value["displayName"]?.ToString() ?? prop.Name;
                    names.Add(displayName);
                }

                if (keys.Count > 0)
                {
                    ChallengeFormatState.SetFormats(keys, names);
                }
                else
                {
                    Plugin.Log.LogWarning("Format list was empty — using defaults");
                    ChallengeFormatState.SetFormats(
                        new System.Collections.Generic.List<string> { "pauper" },
                        new System.Collections.Generic.List<string> { "Pauper" }
                    );
                }
            });
        }

        public void DatabaseGet(string path, Action<JToken> callback)
        {
            StartCoroutine(DatabaseGetCoroutine(path, callback));
        }

        private IEnumerator DatabaseGetCoroutine(string path, Action<JToken> callback)
        {
            if (!EnsureAuthenticated())
            {
                callback?.Invoke(null);
                yield break;
            }

            var config = FirebaseConfig.Instance;
            var baseUrl = config.DatabaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/{path}.json?auth={_idToken}";

            using (var request = UnityWebRequest.Get(url))
            {
                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    var data = JToken.Parse(request.downloadHandler.text);
                    callback?.Invoke(data);
                }
                else
                {
                    HandleApiError("Database Read", request);
                    callback?.Invoke(null);
                }
            }
        }

        /// <summary>
        /// Get the current auth token (for SSE listeners etc.)
        /// </summary>
        public string IdToken => _idToken;

        /// <summary>
        /// Re-authenticate if the token is missing or expired.
        /// Tries refresh token first (fast, no Cloud Function call).
        /// Falls back to full re-auth via Cloud Function.
        /// </summary>
        public void RefreshTokenIfNeeded(Action callback)
        {
            if (_isAuthenticated && !string.IsNullOrEmpty(_idToken))
            {
                callback?.Invoke();
                return;
            }

            // Try refresh token first (faster, no Cloud Function round-trip)
            if (!string.IsNullOrEmpty(_refreshToken))
            {
                Plugin.Log.LogInfo("Refreshing via refresh token...");
                StartCoroutine(RefreshViaRefreshToken(_refreshToken, success =>
                {
                    if (success)
                    {
                        Plugin.Log.LogInfo("Token refreshed via refresh token");
                        FirebaseSseListener.Instance?.UpdateAuthToken(_idToken);
                        callback?.Invoke();
                    }
                    else
                    {
                        // Refresh token failed, try full re-auth
                        Plugin.Log.LogWarning("Refresh token failed, trying full re-auth...");
                        FullReAuth(callback);
                    }
                }));
            }
            else
            {
                FullReAuth(callback);
            }
        }

        private void FullReAuth(Action callback)
        {
            if (!string.IsNullOrEmpty(_cachedPersonaId) && !string.IsNullOrEmpty(_cachedDisplayName))
            {
                Plugin.Log.LogInfo("Full re-auth via Cloud Function...");
                SignInWithMTGAIdentity(_cachedPersonaId, _cachedDisplayName, success =>
                {
                    if (success)
                    {
                        Plugin.Log.LogInfo("Full re-auth succeeded");
                        FirebaseSseListener.Instance?.UpdateAuthToken(_idToken);
                    }
                    else
                    {
                        Plugin.Log.LogWarning("Full re-auth failed");
                    }
                    callback?.Invoke();
                });
            }
            else
            {
                Plugin.Log.LogWarning("Cannot refresh token — no cached credentials");
                callback?.Invoke();
            }
        }

        /// <summary>
        /// Exchange a refresh token for a new ID token via the secure token API.
        /// No Cloud Function needed — direct Google API call.
        /// </summary>
        private IEnumerator RefreshViaRefreshToken(string refreshToken, Action<bool> callback)
        {
            var config = FirebaseConfig.Instance;
            var url = $"https://securetoken.googleapis.com/v1/token?key={config.ApiKey}";
            var payload = $"grant_type=refresh_token&refresh_token={UnityWebRequest.EscapeURL(refreshToken)}";

            using (var request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");

                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    var response = JObject.Parse(request.downloadHandler.text);
                    _idToken = response["id_token"]?.ToString();
                    _refreshToken = response["refresh_token"]?.ToString();
                    _isAuthenticated = !string.IsNullOrEmpty(_idToken);
                    Plugin.Log.LogInfo($"Refresh token exchange succeeded (new token length={_idToken?.Length})");
                    callback?.Invoke(true);
                }
                else
                {
                    Plugin.Log.LogWarning($"Refresh token exchange failed: {request.responseCode} {request.downloadHandler?.text}");
                    callback?.Invoke(false);
                }
            }
        }

        /// <summary>
        /// Start a background coroutine that refreshes the ID token every 50 minutes
        /// (Firebase ID tokens expire after 60 minutes).
        /// </summary>
        private void StartProactiveTokenRefresh()
        {
            if (_tokenRefreshCoroutine != null)
                StopCoroutine(_tokenRefreshCoroutine);
            _tokenRefreshCoroutine = StartCoroutine(ProactiveTokenRefreshLoop());
        }

        private IEnumerator ProactiveTokenRefreshLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(50 * 60); // 50 minutes

                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    Plugin.Log.LogInfo("Proactive token refresh (50m timer)...");
                    bool done = false;
                    RefreshTokenIfNeeded(() => { done = true; });
                    while (!done) yield return null;
                }
            }
        }

        /// <summary>
        /// POSTs JSON to a Cloud Function endpoint. Callback receives (success, responseBody).
        /// </summary>
        public void CallCloudFunction(string functionName, string jsonBody, Action<bool, string> callback)
        {
            StartCoroutine(CallCloudFunctionCoroutine(functionName, jsonBody, callback));
        }

        private IEnumerator CallCloudFunctionCoroutine(string functionName, string jsonBody, Action<bool, string> callback)
        {
            var fnConfig = FirebaseConfig.Instance;
            var url = $"{fnConfig.FunctionUrl}/{functionName}";
            Plugin.Log.LogInfo($"CallCloudFunction: POST {url} ({jsonBody.Length} bytes)");

            using (var request = new UnityWebRequest(url, "POST"))
            {
                var bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                if (!string.IsNullOrEmpty(_idToken))
                    request.SetRequestHeader("Authorization", $"Bearer {_idToken}");

                yield return request.SendWebRequest();

                if (request.responseCode >= 200 && request.responseCode < 300)
                {
                    callback?.Invoke(true, request.downloadHandler.text);
                }
                else
                {
                    Plugin.Log.LogError($"CallCloudFunction {functionName}: HTTP {request.responseCode} - {request.downloadHandler?.text}");
                    callback?.Invoke(false, request.downloadHandler?.text ?? request.error);
                }
            }
        }

        private bool EnsureAuthenticated()
        {
            return _isAuthenticated && !string.IsNullOrEmpty(_idToken);
        }

        private string _lastToastMessage;
        private float _lastToastTime;

        private void HandleApiError(string operation, UnityWebRequest request)
        {
            var message = $"{operation} failed";

            if (request.responseCode == 401 || request.responseCode == 403)
            {
                Plugin.Log.LogWarning($"{operation}: Auth expired (HTTP {request.responseCode}), attempting token refresh...");

                // Don't permanently kill auth — attempt a refresh instead
                _isAuthenticated = false;
                _idToken = null;

                RefreshTokenIfNeeded(() =>
                {
                    if (_isAuthenticated)
                    {
                        Plugin.Log.LogInfo($"Token refreshed after {operation} 401");
                    }
                    else
                    {
                        ShowDedupedToast($"{operation}: Auth failed. Try restarting game.", true);
                        Plugin.Log.LogError($"{operation}: Token refresh failed after 401");
                    }
                });
            }
            else
            {
                message = $"{operation}: {request.error}";
                ShowDedupedToast(message, false);
                Plugin.Log.LogError($"{message} (HTTP {request.responseCode}: {request.downloadHandler?.text})");
            }
        }

        private void ShowDedupedToast(string message, bool isError)
        {
            if (message == _lastToastMessage && Time.unscaledTime - _lastToastTime < 30f)
                return;

            _lastToastMessage = message;
            _lastToastTime = Time.unscaledTime;

            if (isError)
                Toast.Error(message);
            else
                Toast.Warning(message);
        }
    }
}
