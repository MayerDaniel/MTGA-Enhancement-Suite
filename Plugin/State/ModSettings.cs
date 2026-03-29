using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace MTGAEnhancementSuite.State
{
    /// <summary>
    /// Persistent mod settings stored in settings.json alongside the plugin DLL.
    /// </summary>
    internal class ModSettings
    {
        private static ModSettings _instance;
        private static string _settingsPath;

        [JsonProperty("disableCompanions")]
        public bool DisableCompanions { get; set; } = false;

        [JsonProperty("disableCardVFX")]
        public bool DisableCardVFX { get; set; } = false;

        public static ModSettings Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Load();
                return _instance;
            }
        }

        private static string GetSettingsPath()
        {
            if (_settingsPath == null)
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                _settingsPath = Path.Combine(pluginDir, "settings.json");
            }
            return _settingsPath;
        }

        private static ModSettings Load()
        {
            try
            {
                var path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonConvert.DeserializeObject<ModSettings>(json);
                    Plugin.Log.LogInfo($"Settings loaded: companions={!settings.DisableCompanions}, cardVFX={!settings.DisableCardVFX}");
                    return settings;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not load settings: {ex.Message}");
            }
            return new ModSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(GetSettingsPath(), json);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not save settings: {ex.Message}");
            }
        }
    }
}
