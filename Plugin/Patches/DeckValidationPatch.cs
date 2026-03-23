using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MTGAEnhancementSuite.Patches
{
    /// <summary>
    /// Intercepts SetDeckForChallenge on PVPChallengeController to validate
    /// the selected deck against the format's legal cards list from Firebase
    /// (sourced from Scryfall) before allowing it to be set.
    /// </summary>
    internal static class DeckValidationPatch
    {
        private static bool _bypassValidation = false;
        private static HashSet<string> _cachedLegalCards;
        private static string _cachedFormat;

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
                            targetMethod = AccessTools.Method(type, "SetDeckForChallenge");
                            if (targetMethod != null)
                            {
                                Plugin.Log.LogInfo($"Found PVPChallengeController.SetDeckForChallenge in {asm.GetName().Name}");
                                break;
                            }
                        }
                    }
                    if (targetMethod != null) break;
                }

                if (targetMethod == null)
                {
                    Plugin.Log.LogWarning("DeckValidationPatch: Could not find PVPChallengeController.SetDeckForChallenge");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(DeckValidationPatch), nameof(Prefix));
                harmony.Patch(targetMethod, prefix: prefix);
                Plugin.Log.LogInfo("DeckValidationPatch applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckValidationPatch.Apply failed: {ex}");
            }
        }

        static bool Prefix(object __instance, Guid challengeId, Guid deckId)
        {
            try
            {
                if (_bypassValidation)
                {
                    _bypassValidation = false;
                    return true;
                }

                if (!ChallengeFormatState.HasFormat)
                    return true;

                if (deckId == Guid.Empty)
                    return true; // Clearing deck selection, allow

                Plugin.Log.LogInfo($"Intercepting deck selection for format validation (deck: {deckId})");
                Toast.Info("Checking deck legality...");

                // Start async validation
                FirebaseClient.Instance.StartCoroutine(
                    ValidateAndSetDeck(__instance, challengeId, deckId));

                return false; // Block until validation completes
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckValidationPatch.Prefix failed: {ex}");
                return true;
            }
        }

        private static IEnumerator ValidateAndSetDeck(object controller, Guid challengeId, Guid deckId)
        {
            // Step 0: Ensure authenticated — prompt retry if needed
            if (!FirebaseClient.Instance.IsAuthenticated)
            {
                bool? authResult = null;
                AuthPatch.RetryAuth(success => authResult = success);

                float authTimeout = 15f;
                while (authResult == null && authTimeout > 0f)
                {
                    yield return new WaitForSeconds(0.2f);
                    authTimeout -= 0.2f;
                }

                if (authResult != true)
                {
                    Toast.Error("Cannot validate deck without MTGA+ connection.");
                    yield break;
                }
            }

            // Step 1: Get the legal cards list from Firebase (with caching)
            HashSet<string> legalCards = null;
            string format = ChallengeFormatState.SelectedFormat;

            if (_cachedFormat == format && _cachedLegalCards != null)
            {
                legalCards = _cachedLegalCards;
                Plugin.Log.LogInfo($"Using cached {format} legal cards list ({legalCards.Count} cards)");
            }
            else
            {
                bool fetchDone = false;
                JToken fetchResult = null;

                FirebaseClient.Instance.DatabaseGet($"formats/{format}/legalCards", result =>
                {
                    fetchResult = result;
                    fetchDone = true;
                });

                float timeout = 15f;
                while (!fetchDone && timeout > 0f)
                {
                    yield return new WaitForSeconds(0.1f);
                    timeout -= 0.1f;
                }

                if (!fetchDone || fetchResult == null || fetchResult.Type == JTokenType.Null)
                {
                    Toast.Error($"Could not fetch {format} legal cards list. Is the format synced?");
                    // Allow deck selection anyway if we can't validate
                    _bypassValidation = true;
                    var m = AccessTools.Method(controller.GetType(), "SetDeckForChallenge");
                    m.Invoke(controller, new object[] { challengeId, deckId });
                    yield break;
                }

                legalCards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (fetchResult is JObject legalObj)
                {
                    // Keys are lowercase card names, URL-encoded for Firebase key safety
                    foreach (var prop in legalObj.Properties())
                    {
                        var name = DecodeFirebaseKey(prop.Name);
                        legalCards.Add(name);
                    }
                }

                _cachedLegalCards = legalCards;
                _cachedFormat = format;
                Plugin.Log.LogInfo($"Fetched {format} legal cards list: {legalCards.Count} cards");
            }

            // Step 2: Get the deck's card list
            List<DeckCard> deckCards = null;
            string error = null;

            try
            {
                deckCards = ExtractDeckCardsByDeckId(deckId);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Plugin.Log.LogError($"ExtractDeckCardsByDeckId failed: {ex}");
            }

            if (deckCards == null)
            {
                Toast.Warning(error ?? "Could not read deck contents, allowing selection");
                _bypassValidation = true;
                var m = AccessTools.Method(controller.GetType(), "SetDeckForChallenge");
                m.Invoke(controller, new object[] { challengeId, deckId });
                yield break;
            }

            // Step 3: Check each card against the legal list BY NAME
            // This is printing-agnostic: if any printing of "Plains" is legal,
            // all printings are allowed
            var illegalCards = new List<string>();
            var checkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var card in deckCards)
            {
                // Skip cards we've already checked (same name, different printing)
                if (checkedNames.Contains(card.Name))
                    continue;

                checkedNames.Add(card.Name);

                // Skip cards with fallback names (Card#123) — can't validate without a name
                if (card.Name.StartsWith("Card#"))
                {
                    Plugin.Log.LogWarning($"Could not resolve name for grpId {card.ArenaId}, skipping validation");
                    continue;
                }

                if (!legalCards.Contains(card.Name))
                {
                    illegalCards.Add($"{card.Name} x{card.Quantity}");
                }
            }

            // Step 4: Result
            if (illegalCards.Count == 0)
            {
                Plugin.Log.LogInfo("Deck validation passed");
                Toast.Success("Deck is legal!");

                _bypassValidation = true;
                var m = AccessTools.Method(controller.GetType(), "SetDeckForChallenge");
                m.Invoke(controller, new object[] { challengeId, deckId });
            }
            else
            {
                string formatName = format.Substring(0, 1).ToUpper() + format.Substring(1);
                string message = $"Deck not legal in {formatName}.\n\nIllegal cards:\n";
                int shown = 0;
                foreach (var c in illegalCards)
                {
                    message += $"  - {c}\n";
                    shown++;
                    if (shown >= 15)
                    {
                        message += $"  ... and {illegalCards.Count - shown} more\n";
                        break;
                    }
                }

                Plugin.Log.LogWarning(message);
                Toast.Error($"Deck not legal in {formatName} ({illegalCards.Count} illegal cards)");
                ShowErrorDialog("Deck Not Legal", message);
            }
        }

        /// <summary>
        /// Gets card list directly by deck ID without needing the challenge controller.
        /// </summary>
        private static List<DeckCard> ExtractDeckCardsByDeckId(Guid deckId)
        {
            // Use DecksManager (global namespace, concrete class) to get the deck
            var pantryType = AccessTools.TypeByName("Pantry");
            if (pantryType == null) throw new Exception("Pantry type not found");

            // Try DecksManager first (global namespace in Core.dll)
            Type decksManagerType = typeof(DecksManager);
            var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(decksManagerType);
            var decksManager = getMethod.Invoke(null, null);

            if (decksManager == null) throw new Exception("DecksManager is null from Pantry");

            var getDeckMethod = AccessTools.Method(decksManagerType, "GetDeck", new[] { typeof(Guid) });
            if (getDeckMethod == null) throw new Exception("DecksManager.GetDeck(Guid) not found");

            var deck = getDeckMethod.Invoke(decksManager, new object[] { deckId });
            if (deck == null)
                throw new Exception($"Deck {deckId} not found");

            var contentsProperty = AccessTools.Property(deck.GetType(), "Contents");
            var contents = contentsProperty.GetValue(deck);
            var pilesField = AccessTools.Field(contents.GetType(), "Piles");
            var piles = pilesField.GetValue(contents) as IDictionary;

            // Get card title provider for name lookup
            object cardTitleProvider = null;
            MethodInfo getCardTitleMethod = null;

            try
            {
                // CardDatabase (Wotc.Mtga.Cards.Database) has a CardTitleProvider property
                Type cardDbType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "CardDatabase" && !t.IsAbstract && !t.IsInterface)
                        {
                            cardDbType = t;
                            break;
                        }
                    }
                    if (cardDbType != null) break;
                }

                if (cardDbType == null) throw new Exception("CardDatabase type not found");

                var getCardDb = pantryType.GetMethod("Get").MakeGenericMethod(cardDbType);
                var cardDb = getCardDb.Invoke(null, null);
                if (cardDb != null)
                {
                    var titleProviderProp = AccessTools.Property(cardDbType, "CardTitleProvider");
                    if (titleProviderProp != null)
                    {
                        cardTitleProvider = titleProviderProp.GetValue(cardDb);
                        if (cardTitleProvider != null)
                        {
                            getCardTitleMethod = AccessTools.Method(cardTitleProvider.GetType(),
                                "GetCardTitle", new[] { typeof(uint), typeof(bool), typeof(string) });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not get CardTitleProvider: {ex.Message}");
            }

            var result = new List<DeckCard>();

            foreach (System.Collections.DictionaryEntry pile in piles)
            {
                var cards = pile.Value as IList;
                if (cards == null) continue;

                foreach (var card in cards)
                {
                    var idField = AccessTools.Field(card.GetType(), "Id");
                    var qtyField = AccessTools.Field(card.GetType(), "Quantity");
                    var grpId = (uint)idField.GetValue(card);
                    var qty = (uint)qtyField.GetValue(card);

                    string cardName = $"Card#{grpId}";

                    if (cardTitleProvider != null && getCardTitleMethod != null)
                    {
                        try
                        {
                            // formatted=false to get plain text without HTML tags like <nobr>
                            var name = getCardTitleMethod.Invoke(cardTitleProvider,
                                new object[] { grpId, false, null }) as string;
                            if (!string.IsNullOrEmpty(name))
                            {
                                // Strip any residual HTML tags (e.g. <nobr>Deep-Cavern</nobr>)
                                cardName = System.Text.RegularExpressions.Regex.Replace(name, "<[^>]+>", "");
                            }
                        }
                        catch { }
                    }

                    result.Add(new DeckCard
                    {
                        ArenaId = grpId,
                        Name = cardName,
                        Quantity = qty
                    });
                }
            }

            return result;
        }

        private static void ShowErrorDialog(string title, string message)
        {
            try
            {
                var smType = AccessTools.TypeByName("SystemMessageManager");
                var instanceProp = AccessTools.Property(smType, "Instance");
                var manager = instanceProp.GetValue(null);

                if (manager != null)
                {
                    var showOkMethod = AccessTools.Method(smType, "ShowOk",
                        new[] { typeof(string), typeof(string), typeof(Action),
                                typeof(string), smType.GetNestedType("SystemMessagePriority"),
                                typeof(string) });

                    showOkMethod.Invoke(manager, new object[] {
                        title, message, null, null, 0, null
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not show native dialog: {ex.Message}");
            }

            Plugin.Log.LogWarning($"[{title}] {message}");
        }

        private static string DecodeFirebaseKey(string key)
        {
            return key
                .Replace("%2F", "/").Replace("%2f", "/")
                .Replace("%5D", "]").Replace("%5d", "]")
                .Replace("%5B", "[").Replace("%5b", "[")
                .Replace("%23", "#")
                .Replace("%24", "$")
                .Replace("%2E", ".").Replace("%2e", ".")
                .Replace("%25", "%");
        }

        private struct DeckCard
        {
            public uint ArenaId;
            public string Name;
            public uint Quantity;
        }
    }
}
