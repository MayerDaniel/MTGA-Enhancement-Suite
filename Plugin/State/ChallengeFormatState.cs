using System;
using System.Collections.Generic;
using System.Linq;

namespace MTGAEnhancementSuite.State
{
    /// <summary>
    /// Lightweight client-side representation of a game mode definition.
    /// Mirrors the Firebase /gameModes/{id} schema — minus legalitySource
    /// (only needed server-side) and minus legalityCache (also server-side).
    /// </summary>
    internal class GameMode
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public string MatchType = "DirectGame";
        public bool IsBestOf3Default = true;
        // postProcessing is opaque to the client — server enforces it.
    }

    internal static class ChallengeFormatState
    {
        public static string SelectedFormat = "none";
        public static Guid ActiveChallengeId = Guid.Empty;
        public static bool IsBestOf3 = false;

        /// <summary>True when the lobby has been explicitly made public by the host.</summary>
        public static bool IsLobbyPublic = false;

        /// <summary>True when a format is selected (not "none").</summary>
        public static bool HasFormat => SelectedFormat != "none";

        /// <summary>True when joining an existing lobby — prevents format spinner changes.</summary>
        public static bool IsJoining = false;

        /// <summary>True during match (between countdown start and return to lobby). Prevents cleanup on OnDisable.</summary>
        public static bool IsInMatch = false;

        /// <summary>
        /// True when we are in an active lobby session (from challenge creation/join until
        /// the user navigates away). Survives match transitions and OnChallengeClosed events.
        /// Only cleared by explicit user navigation away from the challenge.
        /// </summary>
        public static bool InLobbySession = false;

        /// <summary>
        /// Timestamp of last match end. Used to distinguish "match ended -> back to lobby"
        /// from "user left lobby" in OnChallengeClosed.
        /// </summary>
        public static float LastMatchEndTime = -999f;

        /// <summary>Pending join from URL scheme or server browser.</summary>
        public static string PendingJoinChallengeId = null;
        public static string PendingJoinFormat = null;

        public static bool HasPendingJoin => !string.IsNullOrEmpty(PendingJoinChallengeId);

        /// <summary>Whether the format list has been fetched from Firebase.</summary>
        public static bool FormatsLoaded = false;

        // Game mode list — populated from Firebase /gameModes (or legacy /formatList).
        // "none" / "No Format" is always prepended as a sentinel.
        private static List<GameMode> _gameModes = new List<GameMode>
        {
            new GameMode { Id = "none", DisplayName = "No Format", MatchType = "DirectGame", IsBestOf3Default = false },
        };

        public static IReadOnlyList<GameMode> GameModes => _gameModes;
        public static GameMode GetGameMode(string id) =>
            _gameModes.FirstOrDefault(m => m.Id == id);

        // Backward-compat string arrays for older code paths (filter UI etc.)
        public static string[] FormatOptions => _gameModes.Select(m => m.DisplayName).ToArray();
        public static string[] FormatKeys => _gameModes.Select(m => m.Id).ToArray();

        /// <summary>
        /// Replaces the game mode list. Called once after auth succeeds.
        /// </summary>
        public static void SetGameModes(List<GameMode> modes)
        {
            _gameModes = new List<GameMode>
            {
                new GameMode { Id = "none", DisplayName = "No Format", MatchType = "DirectGame", IsBestOf3Default = false },
            };
            if (modes != null) _gameModes.AddRange(modes);
            FormatsLoaded = true;
            Plugin.Log.LogInfo($"Game modes loaded ({_gameModes.Count}): {string.Join(", ", _gameModes.Select(m => m.Id))}");
        }

        /// <summary>
        /// Legacy entry point used by FetchFormatList — converts simple
        /// (key, displayName) pairs into stub GameMode records.
        /// </summary>
        public static void SetFormats(List<string> keys, List<string> displayNames)
        {
            var modes = new List<GameMode>();
            for (int i = 0; i < keys.Count; i++)
            {
                modes.Add(new GameMode
                {
                    Id = keys[i],
                    DisplayName = i < displayNames.Count ? displayNames[i] : keys[i],
                    MatchType = "DirectGame",
                    IsBestOf3Default = true,
                });
            }
            SetGameModes(modes);
        }

        public static void SetPendingJoin(string challengeId, string format)
        {
            PendingJoinChallengeId = challengeId;
            PendingJoinFormat = format;
        }

        public static void ClearPendingJoin()
        {
            PendingJoinChallengeId = null;
            PendingJoinFormat = null;
        }

        public static void Reset()
        {
            SelectedFormat = "none";
            ActiveChallengeId = Guid.Empty;
            IsBestOf3 = false;
            IsLobbyPublic = false;
            IsJoining = false;
            IsInMatch = false;
            InLobbySession = false;
            ClearPendingJoin();
        }

        /// <summary>
        /// Strips the #number discriminator from a display name.
        /// "rooftoptile#80175" → "rooftoptile"
        /// </summary>
        public static string StripDiscriminator(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return displayName;
            var hashIndex = displayName.LastIndexOf('#');
            return hashIndex > 0 ? displayName.Substring(0, hashIndex) : displayName;
        }
    }
}
