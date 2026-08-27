using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MTGAEnhancementSuite.State
{
    /// <summary>
    /// One card inside a preset, identified by <c>grpId</c> — the specific
    /// printing. That is the same key Arena's deck builder takes
    /// (<c>DeckBuilderModel.AddCardToMainDeck(uint grpId, uint count)</c>),
    /// so applying a preset needs no conversion step.
    /// </summary>
    internal class PresetCard
    {
        [JsonProperty("grpId")]
        public uint GrpId { get; set; }

        [JsonProperty("count")]
        public uint Count { get; set; }

        /// <summary>
        /// Human-readable name, stored only so a preset's contents can be
        /// listed without querying the card database. Not authoritative:
        /// insertion always goes by grpId.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        public PresetCard() { }

        public PresetCard(uint grpId, uint count, string name)
        {
            GrpId = grpId;
            Count = count;
            Name = name;
        }
    }

    /// <summary>
    /// A reusable group of cards, inserted in bulk into a deck open in the
    /// deck editor. Lives only in the mod's settings.json — it never touches
    /// Arena's own decks and is never sent to the server.
    /// </summary>
    internal class CardPreset
    {
        [JsonProperty("id")]
        public Guid Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("cards")]
        public List<PresetCard> Cards { get; set; } = new List<PresetCard>();

        [JsonProperty("createdAt")]
        public long CreatedAt { get; set; }

        public CardPreset() { }

        public CardPreset(string name)
        {
            Id = Guid.NewGuid();
            Name = string.IsNullOrEmpty(name) ? "Preset" : name;
            Cards = new List<PresetCard>();
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Total number of copies, not of distinct entries.</summary>
        [JsonIgnore]
        public uint TotalCards
        {
            get
            {
                uint n = 0;
                if (Cards != null)
                    foreach (var c in Cards) { if (c != null) n += c.Count; }
                return n;
            }
        }
    }
}
