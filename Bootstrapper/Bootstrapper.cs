using System;
using System.IO;
using BepInEx;

namespace MTGAESBootstrapper
{
    /// <summary>
    /// Tiny BepInEx plugin that runs BEFORE the main Enhancement Suite plugin.
    /// Its only job: check for staged update files (.pending) and swap them in
    /// before the main DLL gets loaded and locked by the runtime.
    ///
    /// This DLL rarely changes — updates are applied to MTGAEnhancementSuite.dll only.
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Bootstrapper : BaseUnityPlugin
    {
        public const string GUID = "com.mtgaenhancement.bootstrapper";
        public const string NAME = "MTGA+ Bootstrapper";
        public const string VERSION = "1.0.0";

        private void Awake()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(Info.Location);
                var updateDir = Path.Combine(pluginDir, "update");

                if (!Directory.Exists(updateDir))
                    return;

                var pendingFiles = Directory.GetFiles(updateDir, "*.pending");
                if (pendingFiles.Length == 0)
                    return;

                Logger.LogInfo($"Found {pendingFiles.Length} pending update(s)");

                foreach (var pendingFile in pendingFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(pendingFile); // e.g. "MTGAEnhancementSuite.dll"
                    var targetPath = Path.Combine(pluginDir, fileName);

                    try
                    {
                        // Back up current file
                        if (File.Exists(targetPath))
                        {
                            var backupPath = targetPath + ".bak";
                            if (File.Exists(backupPath))
                                File.Delete(backupPath);
                            File.Move(targetPath, backupPath);
                            Logger.LogInfo($"Backed up {fileName} -> {fileName}.bak");
                        }

                        // Move pending file into place
                        File.Move(pendingFile, targetPath);
                        Logger.LogInfo($"Updated {fileName} from staged file");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to apply update for {fileName}: {ex.Message}");

                        // Try to restore backup
                        var backupPath = targetPath + ".bak";
                        if (!File.Exists(targetPath) && File.Exists(backupPath))
                        {
                            try
                            {
                                File.Move(backupPath, targetPath);
                                Logger.LogInfo($"Restored {fileName} from backup");
                            }
                            catch (Exception restoreEx)
                            {
                                Logger.LogError($"Failed to restore backup: {restoreEx.Message}");
                            }
                        }
                    }
                }

                // Clean up update directory if empty
                try
                {
                    if (Directory.GetFiles(updateDir).Length == 0)
                        Directory.Delete(updateDir);
                }
                catch { }

                // Clean up old backups
                try
                {
                    foreach (var bakFile in Directory.GetFiles(pluginDir, "*.bak"))
                    {
                        File.Delete(bakFile);
                    }
                }
                catch { }

                Logger.LogInfo("Update process complete");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Bootstrapper error: {ex}");
            }
        }
    }
}
