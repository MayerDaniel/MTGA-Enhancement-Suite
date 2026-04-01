using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Brings the MTGA window to the foreground when another player joins
    /// the local player's challenge lobby. Hooks HandleChallengeGeneralUpdate
    /// on PVPChallengeController and detects when ChallengePlayers count
    /// increases from 1 to 2.
    /// </summary>
    internal static class ChallengeJoinNotifyPatch
    {
        // Track player count per challenge to detect joins
        private static readonly Dictionary<Guid, int> _previousPlayerCounts = new Dictionary<Guid, int>();

        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo targetMethod = null;
                Type controllerType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "PVPChallengeController")
                        {
                            controllerType = type;
                            targetMethod = AccessTools.Method(type, "HandleChallengeGeneralUpdate");
                            if (targetMethod != null) break;
                        }
                    }
                    if (targetMethod != null) break;
                }

                if (targetMethod == null)
                {
                    Plugin.Log.LogWarning("ChallengeJoinNotifyPatch: Could not find HandleChallengeGeneralUpdate");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(ChallengeJoinNotifyPatch), nameof(Postfix));
                harmony.Patch(targetMethod, postfix: postfix);
                Plugin.Log.LogInfo("ChallengeJoinNotifyPatch applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ChallengeJoinNotifyPatch.Apply failed: {ex}");
            }
        }

        static void Postfix(object __0) // __0 = PVPChallengeData challenge
        {
            try
            {
                if (__0 == null) return;

                // Get ChallengeId (Guid field)
                var cidField = AccessTools.Field(__0.GetType(), "ChallengeId");
                if (cidField == null)
                {
                    PerPlayerLog.Warning("ChallengeJoinNotify: ChallengeId field not found");
                    return;
                }
                var challengeId = (Guid)cidField.GetValue(__0);

                // Get ChallengePlayers (Dictionary<string, ChallengePlayer>)
                var playersField = AccessTools.Field(__0.GetType(), "ChallengePlayers");
                if (playersField == null)
                {
                    PerPlayerLog.Warning("ChallengeJoinNotify: ChallengePlayers field not found");
                    return;
                }
                var players = playersField.GetValue(__0);
                if (players == null)
                {
                    PerPlayerLog.Warning("ChallengeJoinNotify: ChallengePlayers is null");
                    return;
                }

                // Get Count via reflection (it's a Dictionary)
                var countProp = players.GetType().GetProperty("Count");
                if (countProp == null)
                {
                    PerPlayerLog.Warning("ChallengeJoinNotify: Count property not found on ChallengePlayers");
                    return;
                }
                int currentCount = (int)countProp.GetValue(players, null);

                int previousCount;
                _previousPlayerCounts.TryGetValue(challengeId, out previousCount);
                _previousPlayerCounts[challengeId] = currentCount;

                PerPlayerLog.Info($"ChallengeJoinNotify: challenge={challengeId}, players {previousCount} -> {currentCount}");

                // Player joined: count went from 1 to 2
                if (previousCount <= 1 && currentCount >= 2)
                {
                    PerPlayerLog.Info($"Player joined lobby {challengeId}! Bringing window to front.");
                    WindowHelper.BringToFront();

                    // Delist lobby from server browser (host side)
                    var cidStr = challengeId.ToString();
                    FirebaseClient.Instance.SetLobbyPrivate(cidStr, success =>
                    {
                        if (success)
                            PerPlayerLog.Info($"Host: lobby {cidStr} set to private after player joined");
                        else
                            PerPlayerLog.Warning($"Host: failed to set lobby {cidStr} to private");
                    });
                    FirebaseClient.Instance.StopHeartbeat();
                }

                // Player left: count went from 2 to 1 — re-publicize if it was public before
                if (previousCount >= 2 && currentCount <= 1 && ChallengeFormatState.IsLobbyPublic)
                {
                    var cidStr = challengeId.ToString();
                    PerPlayerLog.Info($"Player left lobby {cidStr}, re-publicizing...");

                    FirebaseClient.Instance.PatchLobby(cidStr, "{\"isPublic\":true}", success =>
                    {
                        if (success)
                        {
                            FirebaseClient.Instance.StartHeartbeat(cidStr);
                            PerPlayerLog.Info($"Host: lobby {cidStr} re-publicized after player left");
                            UI.Toast.Info("Lobby is public again — waiting for opponent.");
                        }
                        else
                        {
                            PerPlayerLog.Warning($"Host: failed to re-publicize lobby {cidStr}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"ChallengeJoinNotifyPatch.Postfix error: {ex}");
            }
        }

        /// <summary>
        /// Clears tracked state for a challenge (call when leaving lobby).
        /// </summary>
        public static void ClearChallenge(Guid challengeId)
        {
            _previousPlayerCounts.Remove(challengeId);
        }
    }
}
