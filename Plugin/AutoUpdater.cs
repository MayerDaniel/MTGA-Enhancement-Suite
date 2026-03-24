using System;
using System.Collections;
using System.IO;
using System.Net;
using MTGAEnhancementSuite.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MTGAEnhancementSuite
{
    /// <summary>
    /// Checks GitHub for a newer release on startup and stages the update
    /// for the bootstrapper to apply on next launch.
    /// </summary>
    internal static class AutoUpdater
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/MayerDaniel/MTGA-Enhancement-Suite/releases/latest";
        private static readonly Version CurrentVersion = new Version(PluginInfo.VERSION);

        public static IEnumerator CheckForUpdate()
        {
            Plugin.Log.LogInfo($"AutoUpdater: Checking for updates (current: {PluginInfo.VERSION})");

            // Small delay to not compete with auth and other startup tasks
            yield return new WaitForSeconds(10f);

            string json = null;
            Exception fetchError = null;

            // Fetch release info on a background thread
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(GitHubApiUrl);
                    request.UserAgent = $"MTGAEnhancementSuite/{PluginInfo.VERSION}";
                    request.Timeout = 15000;

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        json = reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    fetchError = ex;
                }
            });
            thread.IsBackground = true;
            thread.Start();

            // Wait for thread to finish
            while (thread.IsAlive)
                yield return new WaitForSeconds(0.5f);

            if (fetchError != null)
            {
                Plugin.Log.LogWarning($"AutoUpdater: Failed to check for updates: {fetchError.Message}");
                yield break;
            }

            if (string.IsNullOrEmpty(json))
            {
                Plugin.Log.LogWarning("AutoUpdater: Empty response from GitHub");
                yield break;
            }

            // Parse release info
            JObject release;
            try
            {
                release = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"AutoUpdater: Failed to parse release JSON: {ex.Message}");
                yield break;
            }

            var tagName = release["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tagName))
            {
                Plugin.Log.LogWarning("AutoUpdater: No tag_name in release");
                yield break;
            }

            // Parse version from tag (strip leading 'v' if present)
            var versionStr = tagName.TrimStart('v');
            Version remoteVersion;
            if (!Version.TryParse(versionStr, out remoteVersion))
            {
                Plugin.Log.LogWarning($"AutoUpdater: Could not parse version from tag: {tagName}");
                yield break;
            }

            Plugin.Log.LogInfo($"AutoUpdater: Latest release: {remoteVersion}, current: {CurrentVersion}");

            if (remoteVersion <= CurrentVersion)
            {
                Plugin.Log.LogInfo("AutoUpdater: Up to date");
                yield break;
            }

            // Find the DLL and config.json assets
            var assets = release["assets"] as JArray;
            if (assets == null || assets.Count == 0)
            {
                Plugin.Log.LogWarning("AutoUpdater: No assets in release");
                yield break;
            }

            string dllUrl = null;
            string configUrl = null;

            foreach (var asset in assets)
            {
                var name = asset["name"]?.ToString();
                var url = asset["browser_download_url"]?.ToString();
                if (name == "MTGAEnhancementSuite.dll")
                    dllUrl = url;
                else if (name == "config.json")
                    configUrl = url;
            }

            if (dllUrl == null)
            {
                Plugin.Log.LogWarning("AutoUpdater: No DLL found in release assets");
                yield break;
            }

            // Stage the update
            Toast.Info($"MTGA+ update available: v{remoteVersion}. Downloading...");
            Plugin.Log.LogInfo($"AutoUpdater: Downloading update from {dllUrl}");

            var pluginDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            var updateDir = Path.Combine(pluginDir, "update");

            bool downloadSuccess = false;
            Exception downloadError = null;

            var dlThread = new System.Threading.Thread(() =>
            {
                try
                {
                    if (!Directory.Exists(updateDir))
                        Directory.CreateDirectory(updateDir);

                    // Download DLL to staging
                    var dllPath = Path.Combine(updateDir, "MTGAEnhancementSuite.dll.pending");
                    DownloadFile(dllUrl, dllPath);

                    // Download config.json directly (not locked, can overwrite)
                    if (configUrl != null)
                    {
                        var configPath = Path.Combine(pluginDir, "config.json");
                        DownloadFile(configUrl, configPath);
                    }

                    downloadSuccess = true;
                }
                catch (Exception ex)
                {
                    downloadError = ex;
                }
            });
            dlThread.IsBackground = true;
            dlThread.Start();

            while (dlThread.IsAlive)
                yield return new WaitForSeconds(0.5f);

            if (downloadSuccess)
            {
                Toast.Success($"MTGA+ v{remoteVersion} downloaded. Restart the game to apply.");
                Plugin.Log.LogInfo("AutoUpdater: Update staged successfully");
                PerPlayerLog.Info($"Update staged: v{CurrentVersion} -> v{remoteVersion}");
            }
            else
            {
                Toast.Warning("Failed to download MTGA+ update.");
                Plugin.Log.LogError($"AutoUpdater: Download failed: {downloadError?.Message}");
            }
        }

        private static void DownloadFile(string url, string destPath)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = $"MTGAEnhancementSuite/{PluginInfo.VERSION}";
            request.Timeout = 30000;
            request.AllowAutoRedirect = true;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            {
                stream.CopyTo(fs);
            }
        }
    }
}
