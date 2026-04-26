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
    /// Modal popup that shows a searchable list of game modes.
    /// Used in the challenge lobby as a richer alternative to the spinner
    /// when there are many user-defined modes.
    ///
    /// Open via GameModePicker.Open(currentId, mode => { ... }).
    /// </summary>
    internal static class GameModePicker
    {
        private static GameObject _root;
        private static TMP_InputField _search;
        private static Transform _listContainer;
        private static Action<GameMode> _onSelect;
        private static string _currentId;

        public static void Open(string currentId, Action<GameMode> onSelect)
        {
            _currentId = currentId ?? "none";
            _onSelect = onSelect;

            if (_root == null) CreatePanel();

            _search.text = "";
            RefreshList("");
            _root.SetActive(true);

            // Focus search after a frame
            _search.Select();
        }

        public static void Close()
        {
            if (_root != null) _root.SetActive(false);
        }

        private static void CreatePanel()
        {
            _root = new GameObject("MTGAES_GameModePicker");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 175; // above EnhancementSuitePanel (100), below toasts (200)

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _root.AddComponent<GraphicRaycaster>();

            // Backdrop (clicks close)
            var bg = NewChild(_root.transform, "Backdrop");
            Stretch(bg);
            bg.AddComponent<Image>().color = new Color(0.05f, 0.05f, 0.1f, 0.85f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            // Content panel (centered)
            var content = NewChild(_root.transform, "Content");
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.32f, 0.18f);
            contentRect.anchorMax = new Vector2(0.68f, 0.82f);
            contentRect.sizeDelta = Vector2.zero;
            content.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.14f, 0.98f);

            // Title
            var title = NewChild(content.transform, "Title");
            var titleRect = title.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.05f, 0.92f);
            titleRect.anchorMax = new Vector2(0.95f, 1f);
            titleRect.sizeDelta = Vector2.zero;
            var titleTmp = title.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "Select Game Mode";
            titleTmp.fontSize = 22;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Left;

            // Close X
            var closeBtn = NewButton(content.transform, "X",
                new Vector2(0.92f, 0.93f), new Vector2(0.98f, 0.99f),
                new Color(0.6f, 0.2f, 0.2f, 0.9f));
            closeBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(Close));

            // Search field
            var searchObj = NewChild(content.transform, "Search");
            var searchRect = searchObj.GetComponent<RectTransform>();
            searchRect.anchorMin = new Vector2(0.05f, 0.84f);
            searchRect.anchorMax = new Vector2(0.95f, 0.91f);
            searchRect.sizeDelta = Vector2.zero;
            searchObj.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f, 1f);
            _search = searchObj.AddComponent<TMP_InputField>();

            var textArea = NewChild(searchObj.transform, "TextArea");
            Stretch(textArea);
            textArea.AddComponent<RectMask2D>();
            var textObj = NewChild(textArea.transform, "Text");
            Stretch(textObj);
            var inputTmp = textObj.AddComponent<TextMeshProUGUI>();
            inputTmp.fontSize = 16; inputTmp.color = Color.white; inputTmp.alignment = TextAlignmentOptions.Left;
            inputTmp.margin = new Vector4(8, 4, 8, 4);
            _search.textComponent = inputTmp;
            _search.textViewport = textArea.GetComponent<RectTransform>();

            var phObj = NewChild(textArea.transform, "Placeholder");
            Stretch(phObj);
            var phTmp = phObj.AddComponent<TextMeshProUGUI>();
            phTmp.text = "Search modes...";
            phTmp.fontSize = 16; phTmp.color = new Color(0.4f, 0.4f, 0.5f);
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.margin = new Vector4(8, 4, 8, 4);
            _search.placeholder = phTmp;

            _search.onValueChanged.AddListener(new UnityAction<string>(s => RefreshList(s)));

            // Scrollable list
            var scrollArea = NewChild(content.transform, "ScrollArea");
            var scrollRect = scrollArea.GetComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0.05f, 0.05f);
            scrollRect.anchorMax = new Vector2(0.95f, 0.83f);
            scrollRect.sizeDelta = Vector2.zero;

            var scrollView = scrollArea.AddComponent<ScrollRect>();
            scrollView.horizontal = false;
            scrollView.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = NewChild(scrollArea.transform, "Viewport");
            Stretch(viewport);
            viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.3f);
            viewport.AddComponent<Mask>().showMaskGraphic = true;
            scrollView.viewport = viewport.GetComponent<RectTransform>();

            var listContent = NewChild(viewport.transform, "ListContent");
            var listRect = listContent.GetComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0f, 1f);
            listRect.anchorMax = new Vector2(1f, 1f);
            listRect.pivot = new Vector2(0.5f, 1f);
            listRect.sizeDelta = new Vector2(0f, 0f);

            var vlg = listContent.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight = false;
            vlg.childControlWidth = true;

            var csf = listContent.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollView.content = listRect;
            _listContainer = listContent.transform;

            _root.SetActive(false);
        }

        private static void RefreshList(string filter)
        {
            if (_listContainer == null) return;
            for (int i = _listContainer.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_listContainer.GetChild(i).gameObject);

            var query = (filter ?? "").Trim().ToLowerInvariant();
            var modes = ChallengeFormatState.GameModes
                .Where(m => string.IsNullOrEmpty(query)
                    || (m.DisplayName ?? "").ToLowerInvariant().Contains(query)
                    || (m.Id ?? "").ToLowerInvariant().Contains(query))
                .ToList();

            foreach (var mode in modes)
            {
                CreateRow(mode);
            }
        }

        private static void CreateRow(GameMode mode)
        {
            var row = new GameObject("Row_" + mode.Id);
            row.transform.SetParent(_listContainer, false);
            var rt = row.AddComponent<RectTransform>();
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 44;

            bool isCurrent = mode.Id == _currentId;
            row.AddComponent<Image>().color = isCurrent
                ? new Color(0.2f, 0.4f, 0.25f, 0.95f)
                : new Color(0.12f, 0.12f, 0.20f, 0.9f);

            var btn = row.AddComponent<Button>();
            var capturedMode = mode;
            btn.onClick.AddListener(new UnityAction(() =>
            {
                Close();
                _onSelect?.Invoke(capturedMode);
            }));

            // Display name
            var nameObj = new GameObject("Name");
            nameObj.transform.SetParent(row.transform, false);
            var nameRect = nameObj.AddComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0.02f, 0f);
            nameRect.anchorMax = new Vector2(0.6f, 1f);
            nameRect.sizeDelta = Vector2.zero;
            var nameTmp = nameObj.AddComponent<TextMeshProUGUI>();
            nameTmp.text = mode.DisplayName ?? mode.Id;
            nameTmp.fontSize = 16;
            nameTmp.color = Color.white;
            nameTmp.alignment = TextAlignmentOptions.Left;
            nameTmp.margin = new Vector4(8, 0, 0, 0);

            // Match type badge on the right
            var badgeObj = new GameObject("Badge");
            badgeObj.transform.SetParent(row.transform, false);
            var badgeRect = badgeObj.AddComponent<RectTransform>();
            badgeRect.anchorMin = new Vector2(0.6f, 0.1f);
            badgeRect.anchorMax = new Vector2(0.98f, 0.9f);
            badgeRect.sizeDelta = Vector2.zero;
            var badgeTmp = badgeObj.AddComponent<TextMeshProUGUI>();
            badgeTmp.text = (mode.MatchType ?? "DirectGame") + (mode.IsBestOf3Default ? " · Bo3" : " · Bo1");
            badgeTmp.fontSize = 12;
            badgeTmp.color = isCurrent ? new Color(0.7f, 0.9f, 0.7f) : new Color(0.55f, 0.55f, 0.7f);
            badgeTmp.alignment = TextAlignmentOptions.Right;
            badgeTmp.margin = new Vector4(0, 0, 8, 0);
        }

        // --- Tiny UI helpers ---
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

        private static GameObject NewButton(Transform parent, string label,
            Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            var btn = NewChild(parent, "Btn_" + label);
            var r = btn.GetComponent<RectTransform>();
            r.anchorMin = anchorMin;
            r.anchorMax = anchorMax;
            r.sizeDelta = Vector2.zero;
            btn.AddComponent<Image>().color = color;
            btn.AddComponent<Button>();
            var txt = NewChild(btn.transform, "Text");
            Stretch(txt);
            var tmp = txt.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            return btn;
        }
    }
}
