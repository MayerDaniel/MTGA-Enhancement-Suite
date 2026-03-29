using System;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.State;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Suppresses companion (pet) rendering by patching CompanionBuilder.Create
    /// to return null when disabled. This prevents the 3D pet model from loading,
    /// its animations from running, and its sounds from playing.
    /// </summary>
    internal static class CompanionDisablePatch
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo targetMethod = null;
                Type companionBuilderType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "CompanionBuilder")
                        {
                            companionBuilderType = t;
                            targetMethod = AccessTools.Method(t, "Create");
                            break;
                        }
                    }
                    if (targetMethod != null) break;
                }

                if (targetMethod == null)
                {
                    Plugin.Log.LogWarning("CompanionDisablePatch: Could not find CompanionBuilder.Create");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(CompanionDisablePatch), nameof(Prefix));
                harmony.Patch(targetMethod, prefix: prefix);
                Plugin.Log.LogInfo("CompanionDisablePatch applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CompanionDisablePatch.Apply failed: {ex}");
            }
        }

        static bool Prefix(ref object __result)
        {
            try
            {
                if (ModSettings.Instance.DisableCompanions)
                {
                    __result = null;
                    PerPlayerLog.Info("CompanionDisablePatch: Blocked companion creation");
                    return false; // skip original
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"CompanionDisablePatch.Prefix error: {ex}");
            }
            return true;
        }
    }
}
