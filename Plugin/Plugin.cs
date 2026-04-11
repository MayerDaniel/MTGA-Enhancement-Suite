using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using MTGAEnhancementSuite.UrlScheme;
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

            // Enable TLS 1.2 for all HTTP requests (Mono compatibility)
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not set TLS 1.2: {ex.Message}");
            }

            // Check for URL scheme argument FIRST — before anything else.
            // If another MTGA instance is running, forward the URL and kill this process immediately.
            var url = UrlSchemeRegistrar.GetUrlFromArgs();
            if (url != null)
            {
                if (TcpIpcServer.TrySendToExisting(url))
                {
                    Log.LogInfo("Sent URL to existing MTGA instance, killing this process");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    return; // unreachable, but makes the compiler happy
                }
                else
                {
                    // We're the only instance — store as pending join
                    if (UrlSchemeRegistrar.ParseJoinUrl(url, out var challengeId, out var format))
                    {
                        ChallengeFormatState.SetPendingJoin(challengeId, format);
                        Log.LogInfo($"Pending join stored: {challengeId} ({format})");
                    }
                }
            }

            // Register URL scheme (idempotent)
            UrlSchemeRegistrar.Register();

            try
            {
                _harmony = new Harmony(PluginInfo.GUID);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo("Attribute-based Harmony patches applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to apply attribute-based Harmony patches: {ex}");
            }

            try
            {
                Patches.ChallengeCreatePatch.Apply(_harmony);
                Patches.DeckValidationPatch.Apply(_harmony);
                Patches.ChallengeJoinNotifyPatch.Apply(_harmony);
                Patches.BestOfPatch.Apply(_harmony);
                Patches.CompanionDisablePatch.Apply(_harmony);
                Patches.CardVFXPatch.Apply(_harmony);
                Log.LogInfo("Manual Harmony patches applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to apply manual Harmony patches: {ex}");
            }

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

    internal class PluginBehaviour : MonoBehaviour
    {
        private void Awake()
        {
            Plugin.Log.LogInfo("PluginBehaviour.Awake - starting coroutines");
            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(PollForNavBar());
            StartCoroutine(Patches.AuthPatch.WaitForLoginAndAuth());
            StartCoroutine(AutoUpdater.CheckForUpdate());

            // Start named pipe server for receiving URLs from other instances
            TcpIpcServer.OnUrlReceived = OnUrlReceivedFromPipe;
            TcpIpcServer.Start();

            // Process pending join after auth completes
            StartCoroutine(ProcessPendingJoinAfterAuth());

            // Hook challenge lifecycle events after Pantry is ready
            StartCoroutine(HookChallengeLifecycleEvents());
        }

        private void OnUrlReceivedFromPipe(string url)
        {
            Plugin.Log.LogInfo($"URL received from pipe: {url}");

            if (UrlSchemeRegistrar.ParseJoinUrl(url, out var challengeId, out var format))
            {
                // Queue the join on the main thread
                MainThreadDispatcher.Enqueue(() =>
                {
                    EnhancementSuitePanel.JoinLobby(challengeId, format);
                });
            }
        }

        private IEnumerator ProcessPendingJoinAfterAuth()
        {
            // Wait until Firebase auth is complete
            while (!Firebase.FirebaseClient.Instance.IsAuthenticated)
                yield return new WaitForSeconds(2f);

            // Check for pending join
            if (ChallengeFormatState.HasPendingJoin)
            {
                Plugin.Log.LogInfo($"Processing pending join: {ChallengeFormatState.PendingJoinChallengeId}");

                // Wait for the main menu to fully load:
                // 1. NavBarController must exist (main menu is rendered)
                // 2. IChallengeCommunicationWrapper must be in Pantry (challenge system ready)
                Plugin.Log.LogInfo("Waiting for main menu and challenge system...");

                // Wait for NavBarController
                float timeout = 120f; // 2 minute max wait
                while (timeout > 0f)
                {
                    var navBar = UnityEngine.Object.FindObjectOfType<NavBarController>();
                    if (navBar != null)
                    {
                        Plugin.Log.LogInfo("NavBarController found — main menu is loaded");
                        break;
                    }
                    yield return new WaitForSeconds(2f);
                    timeout -= 2f;
                }

                if (timeout <= 0f)
                {
                    Plugin.Log.LogWarning("Timed out waiting for main menu");
                    ChallengeFormatState.ClearPendingJoin();
                    yield break;
                }

                // Wait for challenge system to be ready in Pantry
                var pantryType = AccessTools.TypeByName("Pantry");
                Type commWrapperType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                        { commWrapperType = t; break; }
                    }
                    if (commWrapperType != null) break;
                }

                if (commWrapperType != null && pantryType != null)
                {
                    timeout = 30f;
                    while (timeout > 0f)
                    {
                        try
                        {
                            var wrapper = pantryType.GetMethod("Get")
                                .MakeGenericMethod(commWrapperType)
                                .Invoke(null, null);
                            if (wrapper != null)
                            {
                                Plugin.Log.LogInfo("IChallengeCommunicationWrapper ready in Pantry");
                                break;
                            }
                        }
                        catch { }
                        yield return new WaitForSeconds(2f);
                        timeout -= 2f;
                    }
                }

                // Extra buffer for UI to fully stabilize
                yield return new WaitForSeconds(5f);

                if (!ChallengeFormatState.HasPendingJoin)
                {
                    Plugin.Log.LogInfo("Pending join was cleared while waiting");
                    yield break;
                }

                var challengeId = ChallengeFormatState.PendingJoinChallengeId;
                var format = ChallengeFormatState.PendingJoinFormat;
                ChallengeFormatState.ClearPendingJoin();

                Plugin.Log.LogInfo($"Executing pending join: {challengeId} format={format}");
                EnhancementSuitePanel.JoinLobby(challengeId, format);
            }
        }

        private IEnumerator HookChallengeLifecycleEvents()
        {
            // Wait for Pantry to be available and populated
            Type pantryType = null;
            while (pantryType == null)
            {
                yield return new WaitForSeconds(3f);
                pantryType = AccessTools.TypeByName("Pantry");
            }

            // Wait for IChallengeCommunicationWrapper to be registered
            Type commWrapperType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                    {
                        commWrapperType = t;
                        break;
                    }
                }
                if (commWrapperType != null) break;
            }

            if (commWrapperType == null)
            {
                Plugin.Log.LogWarning("Could not find IChallengeCommunicationWrapper for lifecycle hooks");
                yield break;
            }

            object wrapper = null;
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSeconds(2f);
                try
                {
                    wrapper = pantryType.GetMethod("Get").MakeGenericMethod(commWrapperType)
                        .Invoke(null, null);
                    if (wrapper != null) break;
                }
                catch { }
            }

            if (wrapper == null)
            {
                Plugin.Log.LogWarning("IChallengeCommunicationWrapper not available in Pantry");
                yield break;
            }

            Plugin.Log.LogInfo($"Hooking challenge lifecycle on {wrapper.GetType().Name}");

            // Hook OnChallengeCountdownStart — Action<Guid, int>
            try
            {
                var countdownProp = AccessTools.Property(wrapper.GetType(), "OnChallengeCountdownStart");
                if (countdownProp != null)
                {
                    var existing = countdownProp.GetValue(wrapper) as Delegate;
                    Action<Guid, int> handler = OnChallengeCountdownStart;
                    var combined = Delegate.Combine(existing, handler);
                    countdownProp.SetValue(wrapper, combined);
                    Plugin.Log.LogInfo("Hooked OnChallengeCountdownStart");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to hook OnChallengeCountdownStart: {ex}");
            }

            // Hook OnChallengeClosedMessage — Action<Guid>
            try
            {
                var closedProp = AccessTools.Property(wrapper.GetType(), "OnChallengeClosedMessage");
                if (closedProp != null)
                {
                    var existing = closedProp.GetValue(wrapper) as Delegate;
                    Action<Guid> handler = OnChallengeClosed;
                    var combined = Delegate.Combine(existing, handler);
                    closedProp.SetValue(wrapper, combined);
                    Plugin.Log.LogInfo("Hooked OnChallengeClosedMessage");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to hook OnChallengeClosedMessage: {ex}");
            }
        }

        private static void OnChallengeCountdownStart(Guid challengeId, int countdownSeconds)
        {
            Plugin.Log.LogInfo($"Match countdown started: {challengeId} ({countdownSeconds}s)");
            ChallengeFormatState.IsInMatch = true;

            // Pause SSE listener during match
            Firebase.FirebaseSseListener.Instance?.Stop();
        }

        private static void OnChallengeClosed(Guid challengeId)
        {
            Plugin.Log.LogInfo($"Challenge closed: {challengeId}");
            PerPlayerLog.Info($"Challenge closed: {challengeId} (InLobbySession={ChallengeFormatState.InLobbySession}, IsInMatch={ChallengeFormatState.IsInMatch}, format={ChallengeFormatState.SelectedFormat})");

            // MTGA's server sends ChallengeClose after every match — it removes the old
            // challenge and the game creates a fresh one for rematches. Our format/lobby
            // state is independent of MTGA's challenge lifecycle.
            //
            // If we're in a lobby session, preserve our format state. The new challenge ID
            // will be captured from the widget when the blade re-opens.
            // Firebase lobby cleanup for the OLD challenge ID is fine — we'll register a
            // new one when the host clicks Copy Link / Make Public again, or the
            // InjectFormatSpinner captures the new ID.
            if (ChallengeFormatState.InLobbySession)
            {
                Plugin.Log.LogInfo($"Challenge closed in lobby session — preserving format state, clearing old challenge ID");
                PerPlayerLog.Info($"Preserving lobby session (format={ChallengeFormatState.SelectedFormat})");

                // Delete old lobby from Firebase, but keep format/joining/session state
                if (!ChallengeFormatState.IsJoining && ChallengeFormatState.ActiveChallengeId != Guid.Empty)
                {
                    Firebase.FirebaseClient.Instance.StopHeartbeat();
                    Firebase.FirebaseClient.Instance.DeleteLobby(ChallengeFormatState.ActiveChallengeId.ToString());
                }
                Firebase.FirebaseSseListener.Instance?.Dispose();
                ChallengeFormatState.ActiveChallengeId = Guid.Empty;
                return;
            }

            // Not in a lobby session — full cleanup
            if (!ChallengeFormatState.IsJoining && ChallengeFormatState.ActiveChallengeId != Guid.Empty)
            {
                Firebase.FirebaseClient.Instance.StopHeartbeat();
                Firebase.FirebaseClient.Instance.DeleteLobby(ChallengeFormatState.ActiveChallengeId.ToString());
            }

            Firebase.FirebaseSseListener.Instance?.Dispose();
            ChallengeFormatState.Reset();
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
                    if (navBar != null && navBar.MasteryButton != null)
                    {
                        // Check Base_Middle (where tab is injected), not Base
                        var baseMiddle = navBar.MasteryButton.transform.parent;
                        if (baseMiddle.Find(GameRefs.EnhancementSuiteTabName) == null)
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
            TcpIpcServer.Stop();
        }
    }

    /// <summary>
    /// Simple main thread dispatcher for executing actions from background threads.
    /// </summary>
    internal class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly System.Collections.Generic.Queue<Action> _queue =
            new System.Collections.Generic.Queue<Action>();

        public static void Enqueue(Action action)
        {
            if (_instance == null)
            {
                var go = new GameObject("MainThreadDispatcher");
                go.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MainThreadDispatcher>();
            }

            lock (_queue)
            {
                _queue.Enqueue(action);
            }
        }

        private void Update()
        {
            lock (_queue)
            {
                while (_queue.Count > 0)
                {
                    try { _queue.Dequeue()?.Invoke(); }
                    catch (Exception ex) { Plugin.Log.LogError($"MainThreadDispatcher error: {ex}"); }
                }
            }
        }
    }

    internal static class PluginInfo
    {
        public const string GUID = "com.mtgaenhancement.suite";
        public const string NAME = "MTGA Enhancement Suite";
        public const string VERSION = "0.14.0";
    }
}
