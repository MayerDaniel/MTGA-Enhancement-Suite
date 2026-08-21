using System;
using System.Reflection;
using GreClient.CardData;
using HarmonyLib;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using Wotc.Mtga.Cards.Database;
using Wotc.Mtga.DuelScene;
using Wotc.Mtga.DuelScene.Examine;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Forces the simplified card view: no special styling (showcase,
    /// borderless, alt-art), artwork preserved.
    ///
    /// Nothing here is reimplemented. Arena already has this mechanism and
    /// applies it to selected cards through an asset lookup tree — we simply
    /// always answer yes.
    ///
    /// A) CARDS IN A MATCH — <c>DuelScene_CDC.CheckSimplifyOverride</c>
    ///    Populates <c>SimplifiedOverride</c>, which <c>VisualModel</c> returns
    ///    ahead of the real model. With the toggle on we set it regardless,
    ///    calling the same <c>CardSimplifier.Simplify</c> with the same context
    ///    (<c>ModelOverride</c>, <c>keepArtId: true</c>).
    ///
    /// B) EXAMINE VIEW — <c>ExamineViewCardHolder.SetExamineState</c>
    ///    Rewrites only the <c>Styled</c> state into <c>Unstyled</c>. Every
    ///    other state (Printing, Instance, Specialize...) passes through
    ///    untouched, so "view printing" keeps working, and unstyled cards use
    ///    <c>Instance</c> and are never affected.
    ///
    /// HIDDEN CARDS — why this file carries three extra guards.
    ///
    /// Upstream sets <c>SimplifiedOverride</c> on a handful of cards; we set it
    /// on every card, which surfaces two latent problems in its lifecycle:
    ///
    ///  1. <c>CardData.IsDisplayedFaceDown</c> is computed partly from
    ///     <c>Instance.GrpId &gt; 11</c>. Hidden cards carry a low GrpId (card
    ///     back, Obscured); Simplify rebuilds the printing record from that
    ///     GrpId and returns a card with a real identity, flipping the flag.
    ///     <c>BaseHandCardHolder</c> reads that flag to decide whether to rotate
    ///     the card 180°, so the opponent's hand rendered face-up.
    ///
    ///  2. <c>DuelScene_CDC.Teardown()</c> returns the component to the pool and
    ///     clears <c>PreviousGrpId</c> and <c>RevealOverride</c> — but not
    ///     <c>SimplifiedOverride</c>. Harmless upstream, where it is almost
    ///     always null; here the pooled component carried a revealed card's
    ///     identity onto the next, hidden, card.
    ///
    /// The guards below refuse to simplify face-down cards, clear the override
    /// whenever the component is recycled or retargeted, and validate it once
    /// more at draw time. The failure mode is showing the wrong identity for a
    /// hidden card, so the redundancy is deliberate: losing the simplified look
    /// is a cosmetic nuisance, revealing a card is not.
    ///
    /// The toggle is read on every invocation: turning it off restores the
    /// original behaviour without a restart.
    /// </summary>
    internal static class ForceSimplifiedPatch
    {
        private static PropertyInfo _simplifiedOverrideProp;
        private static bool _warnedFaceMismatch;
        private static bool _warnedStaleOverride;

        public static void Apply(Harmony harmony)
        {
            // --- A) cards in a match ---
            try
            {
                var target = AccessTools.Method(typeof(DuelScene_CDC), "CheckSimplifyOverride");
                if (target == null)
                {
                    Plugin.Log.LogWarning("ForceSimplified: DuelScene_CDC.CheckSimplifyOverride not found, hook A skipped.");
                }
                else
                {
                    _simplifiedOverrideProp = AccessTools.Property(typeof(DuelScene_CDC), "SimplifiedOverride");
                    if (_simplifiedOverrideProp == null || _simplifiedOverrideProp.GetSetMethod(true) == null)
                    {
                        Plugin.Log.LogWarning("ForceSimplified: SimplifiedOverride is not writable, hook A skipped.");
                    }
                    else
                    {
                        harmony.Patch(target, postfix: new HarmonyMethod(
                            typeof(ForceSimplifiedPatch), nameof(CheckSimplifyOverride_Postfix)));
                        Plugin.Log.LogInfo("ForceSimplified: hook A (cards in match) applied.");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: hook A failed: {ex.Message}");
            }

            // --- A2/A3) clear the override on recycle and on retarget ---
            try
            {
                var teardown = AccessTools.Method(typeof(DuelScene_CDC), "Teardown");
                if (teardown != null)
                {
                    harmony.Patch(teardown, postfix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(ClearOverride_Postfix)));
                    Plugin.Log.LogInfo("ForceSimplified: hook A2 (clear on Teardown) applied.");
                }

                var setModel = AccessTools.Method(typeof(DuelScene_CDC), "SetModel");
                if (setModel != null)
                {
                    harmony.Patch(setModel, postfix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(ClearOverride_Postfix)));
                    Plugin.Log.LogInfo("ForceSimplified: hook A3 (clear on SetModel) applied.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: clear hooks failed: {ex.Message}");
            }

            // --- A4) validate at draw time ---
            try
            {
                var visual = AccessTools.PropertyGetter(typeof(DuelScene_CDC), "VisualModel");
                if (visual != null)
                {
                    harmony.Patch(visual, postfix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(VisualModel_Postfix)));
                    Plugin.Log.LogInfo("ForceSimplified: hook A4 (VisualModel validation) applied.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: VisualModel validation failed: {ex.Message}");
            }

            // --- B) examine view ---
            try
            {
                var target = AccessTools.Method(typeof(ExamineViewCardHolder), "SetExamineState");
                if (target == null)
                {
                    Plugin.Log.LogWarning("ForceSimplified: ExamineViewCardHolder.SetExamineState not found, hook B skipped.");
                }
                else
                {
                    harmony.Patch(target, prefix: new HarmonyMethod(
                        typeof(ForceSimplifiedPatch), nameof(SetExamineState_Prefix)));
                    Plugin.Log.LogInfo("ForceSimplified: hook B (examine view) applied.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ForceSimplified: hook B failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A) After the original computation: if the toggle is on and Arena has
        /// not already decided to simplify, we decide for it.
        /// </summary>
        private static void CheckSimplifyOverride_Postfix(DuelScene_CDC __instance, ICardDataAdapter model)
        {
            try
            {
                if (!ModSettings.Instance.ForceSimplifiedCards) return;
                if (__instance == null || model == null) return;

                // Arena already set the override for this card: recomputing
                // would produce the same result.
                if (__instance.SimplifiedOverride != null) return;

                // Guard 1 — never simplify a card that is drawn face-down.
                // Arena refuses the same thing in the examine view
                // (ViewSimplifiedButton.ShouldShowButton).
                if (model.IsDisplayedFaceDown) return;

                var inst = model.Instance;
                if (inst != null && inst.FaceDownState != null &&
                    (inst.FaceDownState.IsFaceDown || inst.FaceDownState.IsCopiedFaceDown)) return;

                var db = GameReflection.PantryGet<CardDatabase>();
                if (db == null) return;

                var simplified = CardSimplifier.Simplify(
                    CardSimplifier.Context.ModelOverride,
                    model,
                    db.CardDataProvider,
                    db.AbilityDataProvider,
                    keepArtId: true);

                if (simplified == null || ReferenceEquals(simplified, model)) return;

                // Guard 2 — invariant check. If simplifying would change which
                // face is shown, substituting it would reveal (or hide)
                // information, so we decline. This also covers causes we have
                // not anticipated, not just the GrpId one.
                if (simplified.IsDisplayedFaceDown != model.IsDisplayedFaceDown)
                {
                    if (!_warnedFaceMismatch)
                    {
                        _warnedFaceMismatch = true;
                        Plugin.Log.LogWarning(
                            "ForceSimplified: discarded a simplification that would have changed the " +
                            $"displayed face (grpId={model.GrpId}). Reported once per session.");
                    }
                    return;
                }

                _simplifiedOverrideProp.SetValue(__instance, simplified, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ForceSimplified (match): {ex.Message}");
            }
        }

        /// <summary>
        /// A2/A3) Clears the override when the component returns to the pool or
        /// is pointed at a different card. If visuals are about to update,
        /// CheckSimplifyOverride recomputes it immediately afterwards;
        /// otherwise it stays null and VisualModel falls back to the real
        /// model, which is exactly Arena's own behaviour.
        /// </summary>
        private static void ClearOverride_Postfix(DuelScene_CDC __instance)
        {
            try
            {
                if (__instance == null || _simplifiedOverrideProp == null) return;
                if (__instance.SimplifiedOverride != null)
                    _simplifiedOverrideProp.SetValue(__instance, null, null);
            }
            catch { /* best-effort cleanup: must never fail the caller */ }
        }

        /// <summary>
        /// A4) Last line of defence, at draw time: if the override in play does
        /// not match the card currently assigned, discard it and return the real
        /// model. The two clears above should make this unreachable, but the
        /// cost is an integer comparison and the failure mode is showing the
        /// wrong identity for a hidden card.
        /// </summary>
        private static void VisualModel_Postfix(DuelScene_CDC __instance, ref ICardDataAdapter __result)
        {
            try
            {
                if (__instance == null || __result == null) return;
                var ov = __instance.SimplifiedOverride;
                if (ov == null || !ReferenceEquals(__result, ov)) return;

                var real = __instance.Model;
                if (real == null) return;
                if (ov.GrpId == real.GrpId) return;   // consistent, nothing to do

                if (!_warnedStaleOverride)
                {
                    _warnedStaleOverride = true;
                    Plugin.Log.LogWarning(
                        $"ForceSimplified: discarded a stale override (override grpId={ov.GrpId}, " +
                        $"card grpId={real.GrpId}). Reported once per session.");
                }
                __result = real;
            }
            catch { /* when in doubt, show the real model */ }
        }

        /// <summary>
        /// B) Before the state is applied: "styled" becomes "unstyled". All
        /// other states pass through unchanged.
        /// </summary>
        private static void SetExamineState_Prefix(ref ExamineState state)
        {
            try
            {
                if (!ModSettings.Instance.ForceSimplifiedCards) return;
                if (state == ExamineState.Styled) state = ExamineState.Unstyled;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ForceSimplified (examine): {ex.Message}");
            }
        }
    }
}
