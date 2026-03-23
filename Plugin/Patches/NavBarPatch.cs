using System;
using HarmonyLib;
using MTGAEnhancementSuite.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Wotc.Mtga.Loc;

namespace MTGAEnhancementSuite.Patches
{
    [HarmonyPatch(typeof(NavBarController), "Awake")]
    internal static class NavBarPatch
    {
        [HarmonyPostfix]
        static void Postfix(NavBarController __instance)
        {
            Plugin.Log.LogInfo("NavBarController.Awake postfix fired");
            InjectTab(__instance);
        }

        public static void InjectTab(NavBarController navBar)
        {
            try
            {
                if (navBar.MasteryButton == null)
                {
                    Plugin.Log.LogWarning("MasteryButton is null, NavBar not ready");
                    return;
                }

                // Use MasteryButton as source — it's always active and in Base_Middle
                var sourceGO = navBar.MasteryButton.gameObject;
                var baseMiddle = sourceGO.transform.parent;

                // Guard against double-injection
                if (baseMiddle.Find(GameRefs.EnhancementSuiteTabName) != null)
                {
                    // Tab already exists — skip silently
                    return;
                }

                // Clone Mastery button into Base_Middle
                var clone = UnityEngine.Object.Instantiate(sourceGO, baseMiddle);
                clone.name = GameRefs.EnhancementSuiteTabName;
                clone.SetActive(true);

                // Place at the end of Base_Middle (after all existing tabs)
                clone.transform.SetAsLastSibling();

                // Destroy Localize components
                foreach (var loc in clone.GetComponentsInChildren<Localize>(true))
                {
                    UnityEngine.Object.Destroy(loc);
                }

                // Configure the button
                var btn = clone.GetComponent<CustomButton>();
                if (btn != null)
                {
                    btn.OnClick.RemoveAllListeners();
                    btn.OnMouseover.RemoveAllListeners();
                    btn.OnMouseoff.RemoveAllListeners();
                    btn.SetText("MTGA+");
                    btn.Interactable = true;
                    btn.OnClick.AddListener(new UnityAction(() =>
                    {
                        EnhancementSuitePanel.Toggle();
                    }));
                }

                // Disable decorative children (overlays, indicators, locks, notify dots)
                for (int i = 0; i < clone.transform.childCount; i++)
                {
                    var child = clone.transform.GetChild(i);
                    var n = child.name;
                    if (n.Contains("Overlay") || n.Contains("Indicator") || n.Contains("Lock")
                        || n.Contains("NotifyDot") || n.Contains("SparkyHighlight") || n.Contains("Particles"))
                    {
                        child.gameObject.SetActive(false);
                    }
                }

                Plugin.Log.LogInfo($"Tab injected into {baseMiddle.name}, clone active: {clone.activeSelf}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"NavBarPatch.InjectTab failed: {ex}");
            }
        }
    }
}
