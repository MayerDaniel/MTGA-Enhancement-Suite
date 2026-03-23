using System;
using System.IO;

namespace MTGAEnhancementSuite
{
    /// <summary>
    /// Writes a separate log file per player so multiple MTGA instances
    /// don't overwrite each other's logs.
    /// Located at: BepInEx/plugins/MTGAEnhancementSuite/mtgaes_{playerName}.log
    /// </summary>
    internal static class PerPlayerLog
    {
        private static StreamWriter _writer;
        private static string _playerName;

        public static bool IsInitialized => _writer != null;

        public static void Init(string path, string playerName)
        {
            try
            {
                _playerName = playerName;
                _writer = new StreamWriter(path, false) { AutoFlush = true };
                _writer.WriteLine($"=== MTGA+ Log for {playerName} started at {DateTime.Now} ===");
                Plugin.Log.LogInfo($"Per-player log: {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not create per-player log: {ex.Message}");
            }
        }

        public static void Log(string message)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
            catch { }
        }

        public static void Info(string message)
        {
            Log($"[Info] {message}");
            Plugin.Log.LogInfo(message);
        }

        public static void Warning(string message)
        {
            Log($"[Warn] {message}");
            Plugin.Log.LogWarning(message);
        }

        public static void Error(string message)
        {
            Log($"[Error] {message}");
            Plugin.Log.LogError(message);
        }
    }
}
