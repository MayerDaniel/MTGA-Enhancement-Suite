using System;
using System.Linq;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Inline searchable dropdown for the in-game challenge view.
    /// The "row" lives where the format spinner used to (a TMP_InputField).
    /// The "list" is rendered on a top-level overlay canvas so it isn't
    /// clipped by the MTGA challenge sidebar's mask. The list position is
    /// recomputed each frame to track the row.
    ///
    /// Backed by a hidden Spinner_OptionSelector clone — when the user picks
    /// an item, this widget calls spinner.SelectOption(idx), which fires the
    /// existing onValueChanged listener (driving Firebase pushes, deck clear,
    /// match-type push, etc.).
    /// </summary>
    internal static class FormatComboBox
    {
        private const string GameObjectName = "MTGAES_FormatComboBox";
        private const string ListCanvasName = "MTGAES_FormatComboBoxList";

        private const int MaxVisibleRows = 5;
        private const float RowHeight = 36f;

        private static GameObject _row;
        private static RectTransform _rowRect;
        private static Canvas _rowCanvas; // for camera lookup when computing screen position
        private static TMP_InputField _inputField;
        private static GameObject _caret;

        // List panel — separate top-level canvas, reparented out of the sidebar
        private static GameObject _listCanvasGO;
        private static RectTransform _listPanelRect;
        private static Transform _listContent;
        private static ListPositionTracker _tracker;

        private static Spinner_OptionSelector _spinnerRef;
        private static int _currentIndex;
        private static bool _locked;
        private static bool _suppressInputChange;

        public static void Mount(Transform parent, RectTransform sourceRect, Spinner_OptionSelector spinner)
        {
            _spinnerRef = spinner;

            // Don't double-mount the row
            var existing = parent.Find(GameObjectName);
            if (existing != null)
            {
                _row = existing.gameObject;
                _rowRect = _row.GetComponent<RectTransform>();
                _inputField = _row.GetComponentInChildren<TMP_InputField>(true);
                EnsureListCanvas();
                return;
            }

            // Build the visible row — sits in the same parent at the spinner's position
            _row = new GameObject(GameObjectName);
            _row.transform.SetParent(parent, false);
            _rowRect = _row.AddComponent<RectTransform>();
            _rowRect.anchorMin = sourceRect.anchorMin;
            _rowRect.anchorMax = sourceRect.anchorMax;
            _rowRect.pivot = sourceRect.pivot;
            _rowRect.anchoredPosition = sourceRect.anchoredPosition;
            _rowRect.sizeDelta = sourceRect.sizeDelta;

            _rowCanvas = _row.AddComponent<Canvas>();
            _rowCanvas.overrideSorting = true;
            _rowCanvas.sortingOrder = 60;
            _row.AddComponent<GraphicRaycaster>();

            BuildRow();
            EnsureListCanvas();
            SetCaretOpen(false); // initial: closed → caret points right
        }

        public static void SyncDisplay(int index)
        {
            _currentIndex = index;
            if (_inputField == null) return;

            _suppressInputChange = true;
            try
            {
                var label = (index >= 0 && index < ChallengeFormatState.FormatOptions.Length)
                    ? ChallengeFormatState.FormatOptions[index]
                    : "";
                _inputField.text = label;
            }
            finally { _suppressInputChange = false; }

            HideList();
        }

        public static void SetLocked(bool locked)
        {
            _locked = locked;
            if (_inputField != null) _inputField.interactable = !locked;
            if (locked) HideList();
        }

        // ---- Row UI ----

        private static void BuildRow()
        {
            var rowBg = _row.AddComponent<Image>();
            rowBg.color = new Color(0.10f, 0.12f, 0.18f, 0.92f);

            // Input field (search/display) — fills most of the row
            var inputObj = NewChild(_row.transform, "Input");
            var inputRect = inputObj.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0f, 0f);
            inputRect.anchorMax = new Vector2(0.88f, 1f);
            inputRect.sizeDelta = Vector2.zero;
            inputObj.AddComponent<Image>().color = new Color(0, 0, 0, 0); // transparent hit area
            _inputField = inputObj.AddComponent<TMP_InputField>();

            var textArea = NewChild(inputObj.transform, "TextArea");
            Stretch(textArea);
            textArea.AddComponent<RectMask2D>();
            var textObj = NewChild(textArea.transform, "Text");
            Stretch(textObj);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 18;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.margin = new Vector4(10, 0, 6, 0);
            _inputField.textComponent = tmp;
            _inputField.textViewport = textArea.GetComponent<RectTransform>();

            var phObj = NewChild(textArea.transform, "Placeholder");
            Stretch(phObj);
            var phTmp = phObj.AddComponent<TextMeshProUGUI>();
            phTmp.text = "Search formats...";
            phTmp.fontSize = 18;
            phTmp.color = new Color(0.45f, 0.5f, 0.6f);
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.Left;
            phTmp.margin = new Vector4(10, 0, 6, 0);
            _inputField.placeholder = phTmp;

            _inputField.onValueChanged.AddListener(new UnityAction<string>(OnInputChanged));
            _inputField.onSelect.AddListener(new UnityAction<string>(_ => ShowList()));

            // Caret on the right — built from two thin overlapping rectangles to
            // form a downward triangle. This avoids depending on a unicode glyph
            // that the active TMP font may not support.
            _caret = NewChild(_row.transform, "Caret");
            var caretRect = _caret.GetComponent<RectTransform>();
            caretRect.anchorMin = new Vector2(0.88f, 0f);
            caretRect.anchorMax = new Vector2(1f, 1f);
            caretRect.sizeDelta = Vector2.zero;
            var caretImg = _caret.AddComponent<Image>();
            caretImg.color = new Color(0, 0, 0, 0); // transparent hit area
            caretImg.raycastTarget = true;
            BuildTriangle(_caret.transform, new Color(0.75f, 0.78f, 0.92f));

            var caretBtn = _caret.AddComponent<Button>();
            caretBtn.transition = Selectable.Transition.None;
            caretBtn.onClick.AddListener(new UnityAction(() =>
            {
                if (_locked) return;
                if (_listCanvasGO != null && _listCanvasGO.activeSelf) HideList();
                else ShowList();
            }));
        }

        /// <summary>Builds a small downward-pointing triangle from 5 thin rectangles.</summary>
        private static void BuildTriangle(Transform parent, Color color)
        {
            // Use 5 horizontal strips of decreasing length, stacked vertically.
            // Heights/widths chosen so the result reads as a triangle at common sizes.
            var widths = new[] { 16, 12, 8, 4 };
            float stripH = 3f;
            float total = stripH * widths.Length;
            for (int i = 0; i < widths.Length; i++)
            {
                var strip = new GameObject("TriStrip" + i);
                strip.transform.SetParent(parent, false);
                var rect = strip.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                // Center vertically on the parent, then offset by index
                float yOffset = (total / 2f) - (i * stripH) - (stripH / 2f);
                rect.anchoredPosition = new Vector2(0, yOffset);
                rect.sizeDelta = new Vector2(widths[i], stripH);
                var img = strip.AddComponent<Image>();
                img.color = color;
                img.raycastTarget = false;
            }
        }

        // ---- List panel (top-level overlay so it isn't clipped) ----

        private static void EnsureListCanvas()
        {
            if (_listCanvasGO != null) return;

            _listCanvasGO = new GameObject(ListCanvasName);
            UnityEngine.Object.DontDestroyOnLoad(_listCanvasGO);

            var canvas = _listCanvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90; // above MTGA challenge UI, below toasts

            var scaler = _listCanvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _listCanvasGO.AddComponent<GraphicRaycaster>();

            // Outside-click backdrop — invisible, full-screen, click closes the list
            var backdrop = NewChild(_listCanvasGO.transform, "Backdrop");
            var bRect = backdrop.GetComponent<RectTransform>();
            bRect.anchorMin = Vector2.zero;
            bRect.anchorMax = Vector2.one;
            bRect.sizeDelta = Vector2.zero;
            var bImg = backdrop.AddComponent<Image>();
            bImg.color = new Color(0, 0, 0, 0.001f); // nearly invisible but raycastable
            bImg.raycastTarget = true;
            var bBtn = backdrop.AddComponent<Button>();
            bBtn.transition = Selectable.Transition.None;
            bBtn.onClick.AddListener(new UnityAction(HideList));

            // List panel
            var panel = NewChild(_listCanvasGO.transform, "ListPanel");
            _listPanelRect = panel.GetComponent<RectTransform>();
            _listPanelRect.anchorMin = new Vector2(0f, 1f);
            _listPanelRect.anchorMax = new Vector2(0f, 1f);
            _listPanelRect.pivot = new Vector2(0f, 1f);
            _listPanelRect.sizeDelta = new Vector2(300, 280);

            panel.AddComponent<Image>().color = new Color(0.06f, 0.07f, 0.11f, 0.98f);

            var scroll = panel.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = NewChild(panel.transform, "Viewport");
            Stretch(viewport);
            // Image (transparent) is needed for ScrollRect drag raycasts.
            // RectMask2D handles clipping by rect bounds — unlike Mask, it
            // doesn't depend on the Image's alpha for masking shape.
            viewport.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = viewport.GetComponent<RectTransform>();

            var content = NewChild(viewport.transform, "Content");
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = Vector2.zero;
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 0;
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight = false;
            vlg.childControlWidth = true;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = contentRect;

            _listContent = content.transform;

            _tracker = _listCanvasGO.AddComponent<ListPositionTracker>();
            _listCanvasGO.SetActive(false);
        }

        private static void OnInputChanged(string value)
        {
            if (_suppressInputChange) return;
            if (_locked) return;
            ShowList();
            RebuildList(value);
        }

        private static void ShowList()
        {
            if (_locked) return;
            EnsureListCanvas();
            if (_listCanvasGO == null) return;
            if (!_listCanvasGO.activeSelf)
            {
                _listCanvasGO.SetActive(true);
            }
            RebuildList(_inputField?.text ?? "");
            UpdateListPosition();
            SetCaretOpen(true);
        }

        private static void HideList()
        {
            if (_listCanvasGO != null) _listCanvasGO.SetActive(false);
            SetCaretOpen(false);
        }

        /// <summary>Repositions the list panel to sit just below the row, in screen-space.</summary>
        internal static void UpdateListPosition()
        {
            if (_rowRect == null || _listPanelRect == null) return;
            if (_listCanvasGO == null || !_listCanvasGO.activeSelf) return;

            // The source row's parent canvas may be ScreenSpaceCamera (MTGA's
            // challenge UI typically is). World corners on a camera-space
            // canvas are NOT pixel coords — we must convert using the same
            // camera. For ScreenSpaceOverlay sources, camera is null and the
            // conversion is a passthrough.
            var sourceCamera = (_rowCanvas != null && _rowCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? _rowCanvas.worldCamera
                : null;

            var corners = new Vector3[4];
            _rowRect.GetWorldCorners(corners);
            // 0=BL, 1=TL, 2=TR, 3=BR
            Vector2 blScreen = RectTransformUtility.WorldToScreenPoint(sourceCamera, corners[0]);
            Vector2 brScreen = RectTransformUtility.WorldToScreenPoint(sourceCamera, corners[3]);

            float width = Mathf.Abs(brScreen.x - blScreen.x);
            if (width <= 1) return;

            // Cap height at MaxVisibleRows
            int actualRows = _listContent != null ? _listContent.childCount : 0;
            float rowsHeight = Mathf.Max(1, Mathf.Min(actualRows, MaxVisibleRows)) * RowHeight;

            _listPanelRect.sizeDelta = new Vector2(width, rowsHeight);
            // ScreenSpaceOverlay panel uses pixel coords; pivot is top-left.
            _listPanelRect.position = new Vector3(blScreen.x, blScreen.y, 0);
        }

        private static void SetCaretOpen(bool open)
        {
            if (_caret == null) return;
            // Closed = pointing right (>) = 90° CCW rotation
            // Open   = pointing down (▾) = 0° (default triangle orientation)
            _caret.transform.localRotation = Quaternion.Euler(0, 0, open ? 0 : 90);
        }

        private static void RebuildList(string filter)
        {
            if (_listContent == null) return;
            for (int i = _listContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listContent.GetChild(i).gameObject);

            var query = (filter ?? "").Trim().ToLowerInvariant();
            string currentLabel = (_currentIndex >= 0 && _currentIndex < ChallengeFormatState.FormatOptions.Length)
                ? ChallengeFormatState.FormatOptions[_currentIndex] : "";
            // If the user hasn't actually typed anything new (text == current selection's label),
            // show the entire list rather than filtering down to just the current selection.
            if (!string.IsNullOrEmpty(query) && query == currentLabel.ToLowerInvariant())
                query = "";

            for (int i = 0; i < ChallengeFormatState.FormatKeys.Length; i++)
            {
                var key = ChallengeFormatState.FormatKeys[i];
                var label = ChallengeFormatState.FormatOptions[i];
                if (!string.IsNullOrEmpty(query) &&
                    !label.ToLowerInvariant().Contains(query) &&
                    !key.ToLowerInvariant().Contains(query))
                    continue;
                CreateRow(i, key, label);
            }
        }

        private static void CreateRow(int index, string key, string label)
        {
            var row = new GameObject("Row_" + key);
            row.transform.SetParent(_listContent, false);
            row.AddComponent<RectTransform>();
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 36;

            bool isCurrent = index == _currentIndex;
            row.AddComponent<Image>().color = isCurrent
                ? new Color(0.20f, 0.35f, 0.25f, 0.95f)
                : new Color(0.10f, 0.12f, 0.20f, 0.95f);

            var btn = row.AddComponent<Button>();
            int capturedIndex = index;
            btn.onClick.AddListener(new UnityAction(() =>
            {
                HideList();
                if (_spinnerRef != null) _spinnerRef.SelectOption(capturedIndex);
            }));

            var nameObj = new GameObject("Name");
            nameObj.transform.SetParent(row.transform, false);
            var nameRect = nameObj.AddComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = Vector2.one;
            nameRect.sizeDelta = Vector2.zero;
            var nameTmp = nameObj.AddComponent<TextMeshProUGUI>();
            nameTmp.text = label;
            nameTmp.fontSize = 16;
            nameTmp.color = Color.white;
            nameTmp.alignment = TextAlignmentOptions.Left;
            nameTmp.margin = new Vector4(12, 0, 12, 0);
            nameTmp.raycastTarget = false;
        }

        // ---- helpers ----

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        private static void Stretch(GameObject obj)
        {
            var r = obj.GetComponent<RectTransform>() ?? obj.AddComponent<RectTransform>();
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.sizeDelta = Vector2.zero;
            r.anchoredPosition = Vector2.zero;
        }

        /// <summary>
        /// Tracks the row each frame to keep the list panel aligned. Must run
        /// late enough to catch any layout changes from MTGA's UI animations.
        /// </summary>
        private class ListPositionTracker : MonoBehaviour
        {
            void LateUpdate() => UpdateListPosition();
        }
    }
}
