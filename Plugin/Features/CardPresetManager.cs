using System;
using System.Collections.Generic;
using Core.Code.Decks;
using GreClient.CardData;
using MTGAEnhancementSuite.Helpers;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using Wotc.Mtga.Cards.Database;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Saves and applies card presets in the deck editor.
    ///
    /// The insertion path mirrors what Arena itself does for its auto-lands
    /// button (<c>AutoLandsToggle</c>):
    ///
    ///     Model.AddCardToMainDeck(grpId, count)   for each card
    ///     Model.UpdatePile(MainDeck)              re-applies filters
    ///     VisualsUpdater.UpdateAllDeckVisuals()   one redraw at the end
    ///
    /// A single refresh after the whole insert rather than one per card.
    /// <c>ICardRolloverZoom</c> is not needed: that is required by
    /// <c>AddCardToDeckPile</c>, which we do not use.
    ///
    /// None of this is visible to the server — we drive exactly the same code
    /// the user's own clicks do. Saving the deck sends Arena's normal
    /// request.
    /// </summary>
    internal static class CardPresetManager
    {
        /// <summary>True when the deck editor is open with a loaded model.</summary>
        public static bool IsDeckBuilderReady()
        {
            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            return provider != null && provider.Model != null && provider.Model.HasLoadedDeck;
        }

        public static List<CardPreset> All => ModSettings.Instance.CardPresets
                                              ?? (ModSettings.Instance.CardPresets = new List<CardPreset>());

        // ---------- saving ----------

        /// <summary>
        /// Creates a preset from the currently open main deck. The name is
        /// taken from the deck; on collision a counter is appended so repeated
        /// saves do not overwrite one another. Returns null if the deck editor
        /// is not ready or the deck is empty.
        /// </summary>
        public static CardPreset SaveCurrentDeck()
        {
            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            if (provider == null || provider.Model == null || !provider.Model.HasLoadedDeck)
            {
                Toast.Warning("Presets: no deck open");
                return null;
            }

            var model = provider.Model;
            var cards = new List<PresetCard>();

            // GetFilteredMainDeck returns the deck's card list; we take it as
            // the source of truth for what the preset should contain.
            var main = model.GetFilteredMainDeck();
            if (main != null)
            {
                foreach (var cq in main)
                {
                    if (cq == null || cq.Printing == null || cq.Quantity == 0) continue;
                    cards.Add(new PresetCard(cq.Printing.GrpId, cq.Quantity, SafeName(cq.Printing)));
                }
            }

            if (cards.Count == 0)
            {
                Toast.Warning("Presets: the deck is empty");
                return null;
            }

            string baseName = string.IsNullOrEmpty(model._deckName) ? "Preset" : model._deckName;
            var preset = new CardPreset(UniqueName(baseName)) { Cards = cards };

            All.Add(preset);
            ModSettings.Instance.Save();

            Plugin.Log.LogInfo($"Preset saved '{preset.Name}': {cards.Count} entries, {preset.TotalCards} copies.");
            PerPlayerLog.Info($"Preset saved '{preset.Name}' ({preset.TotalCards} cards)");
            Toast.Success($"Preset '{preset.Name}' saved ({preset.TotalCards} cards)");
            return preset;
        }

        // ---------- applying ----------

        /// <summary>
        /// Inserts every card of the preset into the open main deck. Each copy
        /// is validated with <c>CanAddCardToMainDeck</c> first, which enforces
        /// the same ownership and legality rules as a manual insert; rejected
        /// copies are counted and reported back to the user.
        /// </summary>
        public static void Apply(CardPreset preset)
        {
            if (preset == null) return;

            var provider = GameReflection.PantryGet<DeckBuilderModelProvider>();
            if (provider == null || provider.Model == null || !provider.Model.HasLoadedDeck)
            {
                Toast.Warning("Presets: no deck open");
                return;
            }

            var model = provider.Model;
            uint added = 0, rejected = 0;

            foreach (var card in preset.Cards)
            {
                if (card == null || card.Count == 0) continue;
                for (uint i = 0; i < card.Count; i++)
                {
                    bool ok;
                    try { ok = model.CanAddCardToMainDeck(card.GrpId); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Preset: CanAddCardToMainDeck({card.GrpId}) threw: {ex.Message}");
                        ok = false;
                    }

                    if (!ok) { rejected++; continue; }

                    try { model.AddCardToMainDeck(card.GrpId); added++; }
                    catch (Exception ex)
                    {
                        rejected++;
                        Plugin.Log.LogWarning($"Preset: AddCardToMainDeck({card.GrpId}) failed: {ex.Message}");
                    }
                }
            }

            // A TWO-step refresh, both parts required:
            //
            //  1. Model.UpdateMainDeck() -> _mainDeck.ApplyFilters()
            //     AddCardToMainDeck only appends to the raw pile; the filtered
            //     list the UI renders stays stale until filters are re-applied.
            //     Without this the cards are in the deck but invisible until
            //     the user switches deck-builder modes.
            //
            //  2. VisualsUpdater.UpdateAllDeckVisuals() -> redraws the views.
            //
            // BasicLandSuggester does exactly this (UpdateMainDeck at the end of
            // its loop); AutoLandsToggle then adds step 2.
            try
            {
                model.UpdatePile(DeckBuilderPile.MainDeck);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Preset: UpdatePile(MainDeck) failed: {ex.Message}");
                try { model.UpdateMainDeck(); } catch { }
            }

            try
            {
                var visuals = GameReflection.PantryGet<DeckBuilderVisualsUpdater>();
                if (visuals != null) visuals.UpdateAllDeckVisuals();
                else Plugin.Log.LogWarning("Preset: DeckBuilderVisualsUpdater unavailable, the UI may not refresh.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Preset: UpdateAllDeckVisuals failed: {ex.Message}");
            }

            Plugin.Log.LogInfo($"Preset '{preset.Name}' applied: {added} added, {rejected} rejected.");
            PerPlayerLog.Info($"Preset '{preset.Name}': +{added} cards, {rejected} rejected");

            if (added == 0)
                Toast.Warning($"'{preset.Name}': no cards added ({rejected} rejected)");
            else if (rejected > 0)
                Toast.Info($"'{preset.Name}': +{added} cards, {rejected} rejected");
            else
                Toast.Success($"'{preset.Name}': +{added} cards");
        }

        // ---------- management ----------

        public static void Delete(Guid id)
        {
            var list = All;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Id == id)
                {
                    string name = list[i].Name;
                    list.RemoveAt(i);
                    ModSettings.Instance.Save();
                    Plugin.Log.LogInfo($"Preset deleted '{name}'.");
                    Toast.Info($"Preset '{name}' deleted");
                    return;
                }
            }
        }

        // ---------- helpers ----------

        private static string UniqueName(string baseName)
        {
            var list = All;
            bool Taken(string n)
            {
                foreach (var p in list)
                    if (p != null && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            if (!Taken(baseName)) return baseName;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{baseName} ({i})";
                if (!Taken(candidate)) return candidate;
            }
            return $"{baseName} ({Guid.NewGuid().ToString().Substring(0, 4)})";
        }

        private static string SafeName(CardPrintingData printing)
        {
            try
            {
                var db = GameReflection.PantryGet<CardDatabase>();
                var titles = db?.CardTitleProvider;
                if (titles != null)
                {
                    string t = titles.GetCardTitle(printing.GrpId, null, false);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch { /* the name is cosmetic only: if it cannot be resolved, move on */ }
            return printing.GrpId.ToString();
        }
    }
}
