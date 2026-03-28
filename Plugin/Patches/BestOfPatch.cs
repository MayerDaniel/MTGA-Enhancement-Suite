using System;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.State;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Hooks PVPChallengeController.SetGameSettings to detect Bo1/Bo3 changes
    /// and push them to Firebase so the server browser and Discord show the correct info.
    /// </summary>
    internal static class BestOfPatch
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo targetMethod = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "PVPChallengeController")
                        {
                            targetMethod = AccessTools.Method(type, "SetGameSettings");
                            if (targetMethod != null)
                            {
                                Plugin.Log.LogInfo($"Found PVPChallengeController.SetGameSettings in {asm.GetName().Name}");
                                break;
                            }
                        }
                    }
                    if (targetMethod != null) break;
                }

                if (targetMethod == null)
                {
                    Plugin.Log.LogWarning("BestOfPatch: Could not find PVPChallengeController.SetGameSettings");
                    return;
                }

                var postfix = new HarmonyMethod(typeof(BestOfPatch), nameof(Postfix));
                harmony.Patch(targetMethod, postfix: postfix);
                Plugin.Log.LogInfo("BestOfPatch applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"BestOfPatch.Apply failed: {ex}");
            }
        }

        // SetGameSettings(Guid challengeId, ChallengeMatchTypes matchType, WhoPlaysFirst whoPlaysFirst, bool isBestOf3)
        // The 4th parameter (index 3) is isBestOf3
        static void Postfix(object __instance, Guid challengeId, object matchType, object whoPlaysFirst, bool isBestOf3)
        {
            try
            {
                bool changed = ChallengeFormatState.IsBestOf3 != isBestOf3;
                ChallengeFormatState.IsBestOf3 = isBestOf3;

                if (!changed) return;

                Plugin.Log.LogInfo($"Best-of changed: Bo{(isBestOf3 ? "3" : "1")} (challengeId={challengeId})");
                PerPlayerLog.Info($"Best-of changed: Bo{(isBestOf3 ? "3" : "1")}");

                // Push to Firebase if we have an active lobby
                if (ChallengeFormatState.ActiveChallengeId != Guid.Empty &&
                    !ChallengeFormatState.IsJoining &&
                    FirebaseClient.Instance.IsAuthenticated)
                {
                    FirebaseClient.Instance.UpdateLobbyBestOf(
                        ChallengeFormatState.ActiveChallengeId.ToString(), isBestOf3);
                    Plugin.Log.LogInfo($"Pushed Bo{(isBestOf3 ? "3" : "1")} to Firebase");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"BestOfPatch.Postfix failed: {ex}");
            }
        }
    }
}
