using System;
using System.Text;
using MTGAEnhancementSuite.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.Features
{
    /// <summary>
    /// Injects a button to the right of the deck editor's filter bar.
    ///
    /// Anchoring: we clone <c>SearchAndFilterBar.AdvancedFiltersButton</c> and
    /// reparent it to the same container, so it inherits style, size and
    /// material instead of having them reproduced by hand.
    ///
    /// Positioning: if the container has a LayoutGroup we let Unity place the
    /// button (appending it is enough); otherwise we offset it manually to the
    /// right of the original. Which case applies is not knowable statically, so
    /// both are handled and the layout found is logged for tuning.
    ///
    /// Clicking opens <see cref="UI.PresetMenu"/>.
    /// </summary>
    internal static class DeckPresetButton
    {
        private const string ButtonName = "MTGAES_PresetButton";

        /// <summary>Gap between the original button and ours, when placing manually.</summary>
        private const float GapPixels = 8f;

        /// <summary>Name of the PNG in icons/ to use as the button icon.</summary>
        private const string IconName = "107582";

        /// <summary>Gold. Image multiplies this colour with the sprite, so the
        /// icon must be supplied white-on-transparent for it to work.</summary>
        private static readonly Color Gold = new Color(0.831f, 0.686f, 0.216f, 1f);

        private const float ScanInterval = 1f;

        private static float _nextScan;
        private static GameObject _button;
        private static SearchAndFilterBar _bar;

        public static void Tick()
        {
            try
            {
                if (Time.unscaledTime < _nextScan) return;
                _nextScan = Time.unscaledTime + ScanInterval;

                var bar = UnityEngine.Object.FindObjectOfType<SearchAndFilterBar>();
                if (bar == null)
                {
                    // Outside the deck editor: the button dies with the bar.
                    _bar = null;
                    _button = null;
                    return;
                }

                // Already injected into this instance of the bar?
                if (_bar == bar && _button != null) return;

                _bar = bar;
                Inject(bar);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"DeckPresetButton.Tick: {ex}");
                _nextScan = Time.unscaledTime + 15f;
            }
        }

        private static void Inject(SearchAndFilterBar bar)
        {
            var source = bar.AdvancedFiltersButton;
            if (source == null)
            {
                Plugin.Log.LogWarning("DeckPresetButton: AdvancedFiltersButton is null, bar not ready.");
                _bar = null;
                return;
            }

            var parent = source.transform.parent;
            if (parent == null)
            {
                Plugin.Log.LogWarning("DeckPresetButton: the filters button has no parent.");
                _bar = null;
                return;
            }

            // Guard against double injection (the bar can be rebuilt).
            var existing = parent.Find(ButtonName);
            if (existing != null)
            {
                _button = existing.gameObject;
                return;
            }

            var clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
            clone.name = ButtonName;
            clone.SetActive(true);

            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(OnClicked);
                btn.interactable = true;
            }

            // The clone carries the advanced-filters icon: swap in ours and
            // tint it gold. A button identical to the original would only
            // confuse.
            ApplySkin(clone);

            // If the clone carries a text label, clear it: the icon is enough.
            var label = clone.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = string.Empty;

            PositionIt(source.gameObject, clone, parent);
            _button = clone;

            LogLayout(bar, source.gameObject, clone, parent, label != null);
        }

        /// <summary>
        /// Swaps in our icon sprite and applies the gold tint. A clone can hold
        /// several Images (background + glyph): we touch the one that already
        /// has a sprite, i.e. the glyph.
        /// </summary>
        private static void ApplySkin(GameObject clone)
        {
            var sprite = IconLoader.Get(IconName);
            if (sprite == null)
            {
                Plugin.Log.LogWarning($"DeckPresetButton: icon '{IconName}.png' not found in icons/, keeping the original.");
            }

            var images = clone.GetComponentsInChildren<Image>(true);
            bool applied = false;
            foreach (var img in images)
            {
                if (img == null) continue;
                if (img.sprite == null) continue;   // sfondo/riempimento: non tocchiamo
                if (sprite != null)
                {
                    img.sprite = sprite;
                    img.preserveAspect = true;
                }
                img.color = Gold;
                applied = true;
            }

            // No Image carried a sprite: tint the first one anyway, so the
            // button is still distinguishable without our icon.
            if (!applied && images.Length > 0 && images[0] != null)
            {
                if (sprite != null) { images[0].sprite = sprite; images[0].preserveAspect = true; }
                images[0].color = Gold;
            }

            Plugin.Log.LogInfo($"DeckPresetButton: skin applied (Images found={images.Length}, sprite={(sprite != null ? IconName : "original")}).");
        }

        /// <summary>
        /// With a LayoutGroup on the container Unity decides the position, so we
        /// just append the button. Without one, we offset it to the right of the
        /// original by its own width plus a margin.
        /// </summary>
        private static void PositionIt(GameObject source, GameObject clone, Transform parent)
        {
            var layout = parent.GetComponent<LayoutGroup>();
            var srcRect = source.GetComponent<RectTransform>();
            var rect = clone.GetComponent<RectTransform>();
            if (rect == null || srcRect == null) return;

            if (layout != null)
            {
                clone.transform.SetAsLastSibling();
                return;
            }

            rect.anchorMin = srcRect.anchorMin;
            rect.anchorMax = srcRect.anchorMax;
            rect.pivot = srcRect.pivot;
            rect.sizeDelta = srcRect.sizeDelta;
            rect.localScale = srcRect.localScale;
            rect.localRotation = srcRect.localRotation;

            float width = srcRect.rect.width;
            if (width <= 1f) width = 40f;
            rect.anchoredPosition = srcRect.anchoredPosition + new Vector2(width + GapPixels, 0f);
        }

        private static void OnClicked()
        {
            // The button lives on Arena's canvas, which is scaled: its "world"
            // coordinates are NOT screen pixels. They must be converted, or the
            // menu lands near the origin (bottom-left corner). For an overlay
            // canvas the camera is null.
            Vector2 at = Input.mousePosition;
            if (_button != null)
            {
                var rt = _button.GetComponent<RectTransform>();
                var canvas = _button.GetComponentInParent<Canvas>();
                if (rt != null)
                {
                    Camera cam = null;
                    if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        cam = canvas.worldCamera;

                    var corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    // corners[0] = basso-sinistra: il menu si apre da li' verso il basso.
                    at = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                }
            }
            Plugin.Log.LogInfo($"DeckPresetButton: click, menu a schermo {at} (screen {Screen.width}x{Screen.height}).");
            PresetMenu.Show(at);
        }

        /// <summary>
        /// Dumps the layout we found, so the position can be tuned from data
        /// rather than guesswork.
        /// </summary>
        private static void LogLayout(SearchAndFilterBar bar, GameObject source, GameObject clone,
                                      Transform parent, bool hadLabel)
        {
            var sb = new StringBuilder();
            var layout = parent.GetComponent<LayoutGroup>();
            var srcRect = source.GetComponent<RectTransform>();
            var rect = clone.GetComponent<RectTransform>();

            sb.AppendLine("[PresetBtn] ===== injection =====");
            sb.AppendLine($"[PresetBtn] bar='{bar.name}' parent='{parent.name}' children={parent.childCount}");
            sb.AppendLine($"[PresetBtn] LayoutGroup on parent: {(layout == null ? "none (manual positioning)" : layout.GetType().Name)}");
            sb.AppendLine($"[PresetBtn] source pos={srcRect.anchoredPosition} size={srcRect.rect.size}");
            sb.AppendLine($"[PresetBtn] clone    pos={rect.anchoredPosition} size={rect.rect.size} label={hadLabel}");

            sb.AppendLine("[PresetBtn] siblings in container (name | active | x | width):");
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var cr = child as RectTransform;
                string x = cr != null ? cr.anchoredPosition.x.ToString("0.#") : "?";
                string w = cr != null ? cr.rect.width.ToString("0.#") : "?";
                sb.AppendLine($"[PresetBtn]    {i,2}. {child.name} | {child.gameObject.activeSelf} | {x} | {w}");
            }
            Plugin.Log.LogInfo(sb.ToString().TrimEnd());
        }
    }
}
