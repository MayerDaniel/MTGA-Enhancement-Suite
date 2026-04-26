using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MTGAEnhancementSuite.Firebase
{
    /// <summary>
    /// Uploads the MTGA Raw_CardDatabase_*.mtga file to the uploadCardDb Cloud Function
    /// when the local hash differs from /cardMetadataVersion in Firebase. Runs once
    /// per session, after authentication.
    ///
    /// The 32-character hex hash embedded in the filename (e.g.
    /// Raw_CardDatabase_3496a613c4c9f4416ca8d7aa5b8bd47a.mtga) is the version key —
    /// MTGA generates a new hash whenever the card DB content changes.
    /// </summary>
    internal static class CardDbUploader
    {
        private static readonly string[] CardDbSearchRoots = new[]
        {
            @"C:\Program Files\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files (x86)\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files (x86)\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"C:\Program Files\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"D:\SteamLibrary\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"E:\SteamLibrary\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
            @"R:\SteamLibrary\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
        };

        private static bool _hasRunThisSession;

        public static IEnumerator CheckAndUpload()
        {
            if (_hasRunThisSession) yield break;
            _hasRunThisSession = true;

            string dbPath = null;
            try { dbPath = FindLatestCardDb(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"CardDbUploader: error finding card DB: {ex.Message}"); }

            if (string.IsNullOrEmpty(dbPath))
            {
                Plugin.Log.LogInfo("CardDbUploader: no MTGA card DB found in known locations, skipping");
                yield break;
            }

            string hash = ExtractHashFromFilename(dbPath);
            if (string.IsNullOrEmpty(hash))
            {
                Plugin.Log.LogInfo($"CardDbUploader: could not extract hash from filename: {Path.GetFileName(dbPath)}");
                yield break;
            }

            // Compare with server hash before uploading
            string serverHash = null;
            bool fetchDone = false;
            FirebaseClient.Instance.DatabaseGet("cardMetadataVersion", data =>
            {
                if (data != null && data.Type == JTokenType.Object)
                    serverHash = data["hash"]?.ToString();
                fetchDone = true;
            });
            float timeout = 10f;
            while (!fetchDone && timeout > 0f)
            {
                yield return new WaitForSeconds(0.25f);
                timeout -= 0.25f;
            }

            if (string.Equals(hash, serverHash, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogInfo($"CardDbUploader: server hash matches local ({Snippet(hash)}), skipping upload");
                yield break;
            }

            Plugin.Log.LogInfo($"CardDbUploader: server={Snippet(serverHash) ?? "<none>"} != local={Snippet(hash)}, uploading {Path.GetFileName(dbPath)}");

            byte[] dbBytes;
            try
            {
                dbBytes = File.ReadAllBytes(dbPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"CardDbUploader: failed to read card DB: {ex.Message}");
                yield break;
            }

            yield return UploadCoroutine(dbBytes, hash, Path.GetFileName(dbPath));
        }

        private static IEnumerator UploadCoroutine(byte[] dbBytes, string hash, string fileName)
        {
            var config = FirebaseConfig.Instance;
            var url = config.ScopeFunctionUrl($"{config.FunctionUrl}/uploadCardDb?hash={hash}");

            var formParts = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("db", dbBytes, fileName, "application/octet-stream"),
                new MultipartFormDataSection("hash", hash),
            };

            using (var request = UnityWebRequest.Post(url, formParts))
            {
                request.timeout = 300; // 5 minutes
                var idToken = FirebaseClient.Instance.IdToken;
                if (!string.IsNullOrEmpty(idToken))
                    request.SetRequestHeader("Authorization", "Bearer " + idToken);

                yield return request.SendWebRequest();

                if (request.responseCode == 200)
                {
                    Plugin.Log.LogInfo($"CardDbUploader: upload succeeded: {request.downloadHandler.text}");
                }
                else
                {
                    Plugin.Log.LogWarning($"CardDbUploader: upload failed (HTTP {request.responseCode}): {request.error} {request.downloadHandler?.text}");
                }
            }
        }

        private static string FindLatestCardDb()
        {
            string newestPath = null;
            DateTime newestWriteTime = DateTime.MinValue;

            foreach (var root in CardDbSearchRoots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] files;
                try { files = Directory.GetFiles(root, "Raw_CardDatabase_*.mtga"); }
                catch { continue; }

                foreach (var path in files)
                {
                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(path);
                        if (lastWrite > newestWriteTime)
                        {
                            newestWriteTime = lastWrite;
                            newestPath = path;
                        }
                    }
                    catch { }
                }
            }

            return newestPath;
        }

        private static string ExtractHashFromFilename(string filePath)
        {
            // Filename pattern: Raw_CardDatabase_<hex chars>.mtga
            var name = Path.GetFileNameWithoutExtension(filePath);
            const string prefix = "Raw_CardDatabase_";
            if (!name.StartsWith(prefix)) return null;
            var hash = name.Substring(prefix.Length);
            if (hash.Length < 8 || !IsHex(hash)) return null;
            return hash.ToLowerInvariant();
        }

        private static bool IsHex(string s)
        {
            return s.All(c =>
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F'));
        }

        private static string Snippet(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length <= 8 ? s : s.Substring(0, 8) + "…";
        }
    }
}
