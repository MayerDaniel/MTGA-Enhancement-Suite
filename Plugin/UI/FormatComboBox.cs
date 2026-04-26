using System;
using System.Collections;
using System.Linq;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Inline searchable dropdown for the in-game challenge view.
    /// Sits in the same place the format spinner used to. Click the row to
    /// open an inline list below it; type to filter. Click an item to select.
    ///
    /// Backed by a hidden Spinner_OptionSelector clone — when the user picks
    /// an item, this widget calls spinner.SelectOption(idx), which fires the
    /// existing onValueChanged listener (driving Firebase pushes, deck clear,
    /// match-type push, etc.).
    /// </summary>
    internal static class FormatComboBox
    {
        private const string GameObjectName = "MTGAES_FormatComboBox";

        private static GameObject _root;
        private static GameObject _rowGO;
        private static TMP_InputField _inputField;
        private static GameObject _listPanel;
        private static Transform _listContent;
        private static Spinner_OptionSelector _spinnerRef;
        private static int _currentIndex = 0;
        private static bool _locked = false;
        private static bool _suppressInputChange = false;

        public static void Mount(Transform parent, RectTransform sourceRect, Spinner_OptionSelector spinner)
        {
            _spinnerRef = spinner;

            // Don't double-mount
            var existing = parent.Find(GameObjectName);
            if (existing != null)
            {
                _root = existing.gameObject;
                _rowGO = _root.transform.Find("Row")?.gameObject;
                _inputField = _rowGO?.GetComponentInChildren<TMP_InputField>(true);
                _listPanel = _root.transform.Find("ListPanel")?.gameObject;
                _listContent = _listPanel?.transform.Find("Viewport/Content");
                return;
            }

            // Root: same parent and same anchored rect as the source spinner.
            _root = new GameObject(GameObjectName);
            _root.transform.SetParent(parent, false);
            var rootRect = _root.AddComponent<RectTransform>();
            rootRect.anchorMin = sourceRect.anchorMin;
            rootRect.anchorMax = sourceRect.anchorMax;
            rootRect.pivot = sourceRect.pivot;
            rootRect.anchoredPosition = sourceRect.anchoredPosition;
            rootRect.sizeDelta = sourceRect.sizeDelta;

            // Canvas overlay so we render above MTGA's challenge UI without
            // being clipped by the parent's mask.
            var canvas = _root.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 60;
            _root.AddComponent<GraphicRaycaster>();

            BuildRow(rootRect);
            BuildListPanel(rootRect);
        }

        /// <summary>Called from the spinner's onValueChanged to update the visible label.</summary>
        public static void SyncDisplay(int index)
        {
            _currentIndex = index;
            if (_inputField == null) return;

            // Update the input text without firing onValueChanged
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

        /// <summary>Lock interaction (for joiners — format chosen by host).</summary>
        public static void SetLocked(bool locked)
        {
            _locked = locked;
            if (_inputField != null)
            {
                _inputField.interactable = !locked;
            }
            if (locked) HideList();
        }

        // ---- UI construction ----

        private static void BuildRow(RectTransform rootRect)
        {
            _rowGO = NewChild(_root.transform, "Row");
            var rowRect = _rowGO.GetComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0f, 0f);
            rowRect.anchorMax = new Vector2(1f, 1f);
            rowRect.sizeDelta = Vector2.zero;
            _rowGO.AddComponent<Image>().color = new Color(0.10f, 0.12f, 0.18f, 0.92f);

            // Border via a child outline image
            var border = NewChild(_rowGO.transform, "Border");
            Stretch(border);
            var borderImg = border.AddComponent<Image>();
            borderImg.color = new Color(1f, 1f, 1f, 0f);
            // Give it a subtle border by using outline color via Image type Sliced is overkill —
            // just put a thin colored top/bottom strip via an inset:
            borderImg.color = new Color(0.4f, 0.4f, 0.55f, 0.25f);
            borderImg.raycastTarget = false;
            // Inset the border by tweaking offsets so it doesn't fully overlap
            var br = border.GetComponent<RectTransform>();
            br.offsetMin = new Vector2(0, 0);
            br.offsetMax = new Vector2(0, 0);

            // Search/display input field
            var inputObj = NewChild(_rowGO.transform, "Input");
            var inputRect = inputObj.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0f, 0f);
            inputRect.anchorMax = new Vector2(0.92f, 1f);
            inputRect.sizeDelta = Vector2.zero;
            // Transparent background — the row already has one
            inputObj.AddComponent<Image>().color = new Color(0, 0, 0, 0);
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
            phTmp.text = "Search formats…";
            phTmp.fontSize = 18;
            phTmp.color = new Color(0.45f, 0.5f, 0.6f);
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.Left;
            phTmp.margin = new Vector4(10, 0, 6, 0);
            _inputField.placeholder = phTmp;

            _inputField.onValueChanged.AddListener(new UnityAction<string>(OnInputChanged));
            _inputField.onSelect.AddListener(new UnityAction<string>(_ => ShowList()));

            // ▾ caret on the right
            var caret = NewChild(_rowGO.transform, "Caret");
            var caretRect = caret.GetComponent<RectTransform>();
            caretRect.anchorMin = new Vector2(0.92f, 0f);
            caretRect.anchorMax = new Vector2(1f, 1f);
            caretRect.sizeDelta = Vector2.zero;
            var caretTmp = caret.AddComponent<TextMeshProUGUI>();
            caretTmp.text = "▾";
            caretTmp.fontSize = 22;
            caretTmp.color = new Color(0.7f, 0.7f, 0.85f);
            caretTmp.alignment = TextAlignmentOptions.Center;
            caretTmp.raycastTarget = true;

            // Make the caret area also toggle the list (useful when the input
            // is already focused; clicking the caret toggles the list visibility)
            var caretImg = caret.AddComponent<Image>();
            caretImg.color = new Color(0, 0, 0, 0);
            caretImg.raycastTarget = true;
            var caretBtn = caret.AddComponent<Button>();
            caretBtn.transition = Selectable.Transition.None;
            caretBtn.onClick.AddListener(new UnityAction(() =>
            {
                if (_locked) return;
                if (_listPanel != null && _listPanel.activeSelf) HideList();
                else ShowList();
            }));
        }

        private static void BuildListPanel(RectTransform rootRect)
        {
            _listPanel = NewChild(_root.transform, "ListPanel");
            var lpRect = _listPanel.GetComponent<RectTransform>();
            // Anchor to the bottom of the row, expand downward.
            lpRect.anchorMin = new Vector2(0f, 0f);
            lpRect.anchorMax = new Vector2(1f, 0f);
            lpRect.pivot = new Vector2(0.5f, 1f);
            lpRect.anchoredPosition = new Vector2(0f, -2f);
            lpRect.sizeDelta = new Vector2(0f, 220f); // 220px tall

            _listPanel.AddComponent<Image>().color = new Color(0.06f, 0.07f, 0.11f, 0.98f);

            var scroll = _listPanel.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = NewChild(_listPanel.transform, "Viewport");
            Stretch(viewport);
            viewport.AddComponent<Image>().color = new Color(0, 0, 0, 0);
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            scroll.viewport = viewport.GetComponent<RectTransform>();

            var content = NewChild(viewport.transform, "Content");
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 0f);
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
            _listPanel.SetActive(false);
        }

        // ---- Behavior ----

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
            if (_listPanel == null) return;
            if (_listPanel.activeSelf)
            {
                RebuildList(_inputField?.text ?? "");
                return;
            }
            _listPanel.SetActive(true);
            RebuildList(_inputField?.text ?? "");
        }

        private static void HideList()
        {
            if (_listPanel != null) _listPanel.SetActive(false);
        }

        private static void RebuildList(string filter)
        {
            if (_listContent == null) return;
            for (int i = _listContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listContent.GetChild(i).gameObject);

            // Filter only against the user-typed search. If text == current
            // selection's display name, treat as no filter (so the list shows
            // everything when the input still has the old selection text).
            var query = (filter ?? "").Trim().ToLowerInvariant();
            string currentLabel = (_currentIndex >= 0 && _currentIndex < ChallengeFormatState.FormatOptions.Length)
                ? ChallengeFormatState.FormatOptions[_currentIndex] : "";
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
    }
}
