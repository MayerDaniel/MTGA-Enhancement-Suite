using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MTGAEnhancementSuite
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Awake called");

            try
            {
                _harmony = new Harmony(PluginInfo.GUID);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo("Harmony patches applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to apply Harmony patches: {ex}");
            }

            // Create a separate persistent GameObject that won't be destroyed
            var persistentObj = new GameObject("MTGAEnhancementSuite_Persistent");
            persistentObj.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(persistentObj);
            persistentObj.AddComponent<PluginBehaviour>();

            Log.LogInfo("Persistent behaviour created");
        }

        private void OnDestroy()
        {
            Log.LogInfo("Plugin OnDestroy called (this is expected)");
        }
    }

    /// <summary>
    /// Separate MonoBehaviour on a hidden, persistent GameObject.
    /// Survives scene loads and game cleanup.
    /// </summary>
    internal class PluginBehaviour : MonoBehaviour
    {
        private void Awake()
        {
            Plugin.Log.LogInfo("PluginBehaviour.Awake - starting coroutine");
            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(PollForNavBar());
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo($"Scene loaded: {scene.name} (mode: {mode})");
        }

        private IEnumerator PollForNavBar()
        {
            Plugin.Log.LogInfo("PollForNavBar coroutine running");

            while (true)
            {
                yield return new WaitForSeconds(2f);

                try
                {
                    var navBar = FindObjectOfType<NavBarController>();
                    if (navBar != null && navBar.HomeButton != null)
                    {
                        var parent = navBar.HomeButton.transform.parent;
                        if (parent.Find(GameRefs.EnhancementSuiteTabName) == null)
                        {
                            Plugin.Log.LogInfo("NavBarController found, injecting tab...");
                            Patches.NavBarPatch.InjectTab(navBar);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Poll error: {ex}");
                }
            }
        }

        private void OnDestroy()
        {
            Plugin.Log.LogInfo("PluginBehaviour.OnDestroy called (should NOT happen)");
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }

    internal static class PluginInfo
    {
        public const string GUID = "com.mtgaenhancement.suite";
        public const string NAME = "MTGA Enhancement Suite";
        public const string VERSION = "0.1.0";
    }
}
