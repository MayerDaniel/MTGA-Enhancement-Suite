using System;
using System.Collections.Generic;

namespace MTGAEnhancementSuite.State
{
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

        // Dynamic format lists — populated from Firebase /formatList on startup.
        // "No Format" / "none" is always prepended.
        private static List<string> _formatOptions = new List<string> { "No Format" };
        private static List<string> _formatKeys = new List<string> { "none" };

        public static string[] FormatOptions => _formatOptions.ToArray();
        public static string[] FormatKeys => _formatKeys.ToArray();

        /// <summary>
        /// Replaces the format lists with data fetched from Firebase.
        /// Called once after auth succeeds.
        /// </summary>
        public static void SetFormats(List<string> keys, List<string> displayNames)
        {
            _formatKeys = new List<string> { "none" };
            _formatOptions = new List<string> { "No Format" };
            _formatKeys.AddRange(keys);
            _formatOptions.AddRange(displayNames);
            FormatsLoaded = true;
            Plugin.Log.LogInfo($"Format list loaded: {string.Join(", ", _formatKeys)}");
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
