using System;
using System.Collections.Generic;
using GreClient.Rules;
using TMPro;
using UnityEngine;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Shows the remaining turn time above the local player's timeout
    /// hourglass and below the opponent's.
    ///
    /// Anchoring: we clone the <see cref="TMP_Text"/> the hourglass uses for
    /// its "x3" counter and reparent it to the same transform. Cloning rather
    /// than rebuilding inherits font, size, material, outline and colour
    /// without guessing at them, and keeps us consistent if MTGA restyles.
    ///
    /// Which timer to show (rule derived from logged game states):
    ///   deciding player == active player -> TimerType.ActivePlayer    (61s)
    ///   deciding player != active player -> TimerType.NonActivePlayer (45s)
    /// The rule is applied per player, so both sides always show a value.
    ///
    /// ElapsedTime does not advance on its own: it arrives from the server and
    /// must be anchored to MtgTimer.CreatedAt — exactly what Arena's own
    /// MatchTimer and LowTimeWarning do.
    /// </summary>
    internal static class TurnTimerDisplay
    {
        /// <summary>Vertical offset from the hourglass, in multiples of the text height.</summary>
        private const float OffsetFactor = 1.15f;

        /// <summary>How often to rescan for hourglasses (unscaled seconds).</summary>
        private const float RescanInterval = 2f;

        /// <summary>Opacity of the number while the timer is not running.</summary>
        private const float IdleAlpha = 0.45f;

        /// <summary>
        /// Black outline. The number sits over the playmat and the avatar,
        /// which vary from match to match — without an outline it becomes
        /// unreadable against some light backgrounds.
        /// </summary>
        private const float OutlineWidth = 0.25f;
        private static readonly Color OutlineColor = new Color(0f, 0f, 0f, 1f);

        private sealed class Slot
        {
            public PlayerTimeoutDisplay Host;
            public TMP_Text Label;
            public bool IsLocal;
            public Color BaseColor;
        }

        private static readonly List<Slot> _slots = new List<Slot>();
        private static float _nextRescan;
        private static bool _warnedNoHost;

        public static void Tick()
        {
            try
            {
                // Toggle off: tear the labels down. Turning it back on has the
                // next rescan recreate them; no restart needed either way.
                if (!State.ModSettings.Instance.ShowTurnTimer)
                {
                    if (_slots.Count > 0) Clear();
                    return;
                }

                var gm = UnityEngine.Object.FindObjectOfType<GameManager>();
                if (gm == null) { Clear(); return; }

                if (Time.unscaledTime >= _nextRescan)
                {
                    _nextRescan = Time.unscaledTime + RescanInterval;
                    Rescan();
                }
                if (_slots.Count == 0) return;

                // LatestGameState e non CurrentGameState: durante le animazioni
                // lunghe il playback resta indietro (nella diagnostica i due
                // divergevano nel 40% dei campioni) e il timer si bloccherebbe.
                var state = gm.LatestGameState ?? gm.CurrentGameState;
                if (state == null) return;

                foreach (var slot in _slots)
                {
                    if (slot.Label == null) continue;
                    var player = FindPlayer(state, slot.IsLocal);
                    bool warn, running;
                    slot.Label.text = FormatFor(state, player, out warn, out running);

                    // A timer fermo il valore NON e' predittivo: il server puo'
                    // rivederlo verso l'alto nell'istante in cui la priorita'
                    // arriva davvero (osservati 45 -> 55/58/60/70 nella stessa
                    // partita). Lo smorziamo per distinguere "in attesa" da
                    // "sta scorrendo".
                    var c = warn ? new Color(0.95f, 0.35f, 0.30f) : slot.BaseColor;
                    if (!running) c.a *= IdleAlpha;
                    slot.Label.color = c;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"TurnTimerDisplay.Tick: {ex}");
                _nextRescan = Time.unscaledTime + 10f;
            }
        }

        /// <summary>
        /// Adds a black outline to the number. We use <c>fontMaterial</c> and
        /// not <c>fontSharedMaterial</c>: the fontMaterial getter creates a
        /// per-label copy of the material. With the shared material the outline
        /// would also land on Arena's own "x3" counter, which shares it with us
        /// precisely because we are its clone.
        /// </summary>
        private static void ApplyOutline(TMP_Text label)
        {
            try
            {
                var mat = label.fontMaterial;
                if (mat == null) return;
                mat.EnableKeyword(ShaderUtilities.Keyword_Outline);
                mat.SetColor(ShaderUtilities.ID_OutlineColor, OutlineColor);
                mat.SetFloat(ShaderUtilities.ID_OutlineWidth, OutlineWidth);
                label.UpdateMeshPadding();
            }
            catch (Exception ex)
            {
                // The outline improves readability but is not essential: if the
                // shader does not support it, carry on without.
                Plugin.Log.LogWarning($"TurnTimer: contorno non applicato ({ex.Message}).");
            }
        }

        /// <summary>Call from OnDestroy or on scene change.</summary>
        public static void Clear()
        {
            foreach (var s in _slots)
            {
                if (s.Label != null) UnityEngine.Object.Destroy(s.Label.gameObject);
            }
            _slots.Clear();
            _warnedNoHost = false;
        }

        private static void Rescan()
        {
            // Drop slots whose host was destroyed (match over, scene change).
            for (int i = _slots.Count - 1; i >= 0; i--)
            {
                if (_slots[i].Host == null || _slots[i].Label == null)
                {
                    if (_slots[i].Label != null) UnityEngine.Object.Destroy(_slots[i].Label.gameObject);
                    _slots.RemoveAt(i);
                }
            }

            var hosts = UnityEngine.Object.FindObjectsOfType<PlayerTimeoutDisplay>();
            if (hosts == null || hosts.Length == 0)
            {
                // MaxTimeoutCount == 0: Arena builds no hourglass, so we have no anchor.
                if (!_warnedNoHost)
                {
                    _warnedNoHost = true;
                    Plugin.Log.LogInfo("TurnTimer: no hourglass in scene (timeouts disabled?), display not attached.");
                }
                return;
            }
            _warnedNoHost = false;

            foreach (var host in hosts)
            {
                if (host == null) continue;
                bool already = false;
                foreach (var s in _slots) { if (s.Host == host) { already = true; break; } }
                if (already) continue;
                Attach(host, hosts);
            }
        }

        private static void Attach(PlayerTimeoutDisplay host, PlayerTimeoutDisplay[] all)
        {
            var source = host.GetComponentInChildren<TMP_Text>(true);
            if (source == null)
            {
                Plugin.Log.LogWarning($"TurnTimer: '{host.name}' non ha TMP_Text, salto.");
                return;
            }

            bool isLocal = DetermineIsLocal(host, all);

            var clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            clone.name = "MTGAES_TurnTimer";
            var label = clone.GetComponent<TMP_Text>();
            if (label == null) { UnityEngine.Object.Destroy(clone); return; }

            // The clone inherits font, material and outline from the original.
            // We only strip what belongs to the "x3" counter's animation.
            foreach (var a in clone.GetComponentsInChildren<Animation>(true)) UnityEngine.Object.Destroy(a);
            foreach (var a in clone.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.Destroy(a);

            var srcRect = source.rectTransform;
            var rect = label.rectTransform;
            rect.anchorMin = srcRect.anchorMin;
            rect.anchorMax = srcRect.anchorMax;
            rect.pivot = srcRect.pivot;
            rect.sizeDelta = srcRect.sizeDelta;
            rect.localScale = srcRect.localScale;
            rect.localRotation = srcRect.localRotation;

            float step = Mathf.Max(srcRect.rect.height, 20f) * OffsetFactor;
            // Local player above the hourglass, opponent below.
            float dy = isLocal ? step : -step;
            rect.localPosition = srcRect.localPosition + new Vector3(0f, dy, 0f);

            label.text = "--";
            if (label.font == null) label.font = UI.TmpFontHelper.Get();

            ApplyOutline(label);

            _slots.Add(new Slot
            {
                Host = host,
                Label = label,
                IsLocal = isLocal,
                BaseColor = label.color
            });

            Plugin.Log.LogInfo(
                $"TurnTimer: agganciato a '{host.name}' (locale={isLocal}) " +
                $"posY {srcRect.localPosition.y:0.#} -> {rect.localPosition.y:0.#} (step={step:0.#})");
        }

        /// <summary>
        /// Tells the local hourglass from the opponent's. Tries the prefab name
        /// first; if that is inconclusive it falls back to screen position, since
        /// ours sits at the bottom and the opponent's at the top.
        /// </summary>
        private static bool DetermineIsLocal(PlayerTimeoutDisplay host, PlayerTimeoutDisplay[] all)
        {
            string n = host.name ?? string.Empty;
            if (n.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf("opponent", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            float y = host.transform.position.y;
            float other = y;
            foreach (var h in all)
            {
                if (h == null || h == host) continue;
                other = h.transform.position.y;
                break;
            }
            // With a single host present, assume it is the local one.
            return y <= other;
        }

        private static MtgPlayer FindPlayer(MtgGameState state, bool wantLocal)
        {
            if (state.Players == null) return null;
            foreach (var p in state.Players)
            {
                if (p == null) continue;
                if (p.IsLocalPlayer == wantLocal) return p;
            }
            return null;
        }

        private static string FormatFor(MtgGameState state, MtgPlayer player, out bool warn, out bool running)
        {
            warn = false;
            running = false;
            if (player == null || player.Timers == null) return "--";

            bool isActivePlayer = state.ActivePlayer != null
                                  && state.ActivePlayer.InstanceId == player.InstanceId;
            var want = isActivePlayer
                ? Wotc.Mtgo.Gre.External.Messaging.TimerType.ActivePlayer
                : Wotc.Mtgo.Gre.External.Messaging.TimerType.NonActivePlayer;

            MtgTimer timer = null;
            foreach (var t in player.Timers)
            {
                if (t != null && t.TimerType == want) { timer = t; break; }
            }
            if (timer == null) return "--";

            running = timer.Running;
            float remaining = timer.RemainingTime;
            if (timer.Running)
            {
                float age = (float)(DateTime.UtcNow - timer.CreatedAt).TotalSeconds;
                if (age > 0f) remaining -= age;
            }
            if (remaining < 0f) remaining = 0f;

            warn = timer.WarningThreshold > 0 && remaining <= timer.WarningThreshold;
            return Format(remaining);
        }

        private static string Format(float seconds)
        {
            int s = Mathf.CeilToInt(seconds);
            if (s < 60) return s.ToString();
            return string.Format("{0}:{1:00}", s / 60, s % 60);
        }
    }
}
