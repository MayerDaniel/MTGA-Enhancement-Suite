using System;
using System.Collections.Generic;
using MTGAEnhancementSuite.Features;
using MTGAEnhancementSuite.Patches;
using MTGAEnhancementSuite.State;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    /// <summary>
    /// Modal that lets the user move a selection of decks into an existing
    /// folder, create a new folder containing them, or move them back to
    /// the root (un-foldered) view.
    ///
    /// Built on its own top-level ScreenSpaceOverlay canvas. All folder
    /// mutations go through <see cref="DeckOrganizationManager"/> which
    /// handles persistence; we trigger a deck-grid refresh after each
    /// successful action.
    /// </summary>
    internal static class MoveToFolderModal
    {
        public static void Show(IReadOnlyList<Guid> deckIds)
        {
            if (deckIds == null || deckIds.Count == 0) return;

            var modal = new GameObject("MTGAES_MoveToFolderModal");
            UnityEngine.Object.DontDestroyOnLoad(modal);

            var canvas = modal.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = modal.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            modal.AddComponent<GraphicRaycaster>();

            // Dim backdrop — click to cancel.
            var bg = NewChild(modal.transform, "Backdrop");
            StretchFull(bg);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0, 0, 0, 0.7f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(() => UnityEngine.Object.Destroy(modal)));

            // Dialog panel
            var dialog = NewChild(modal.transform, "Dialog");
            var dRt = dialog.GetComponent<RectTransform>();
            dRt.anchorMin = new Vector2(0.30f, 0.18f);
            dRt.anchorMax = new Vector2(0.70f, 0.82f);
            dRt.sizeDelta = Vector2.zero;
            dialog.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.18f, 0.98f);

            // Title
            var title = NewChild(dialog.transform, "Title");
            var tRt = title.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0.05f, 0.90f);
            tRt.anchorMax = new Vector2(0.95f, 0.98f);
            tRt.sizeDelta = Vector2.zero;
            var titleTmp = title.AddComponent<TextMeshProUGUI>();
            titleTmp.text = deckIds.Count == 1
                ? "Move 1 deck to…"
                : $"Move {deckIds.Count} decks to…";
            titleTmp.fontSize = 24;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Left;

            // Scroll list of options
            var listArea = NewChild(dialog.transform, "ListArea");
            var lRt = listArea.GetComponent<RectTransform>();
            lRt.anchorMin = new Vector2(0.05f, 0.18f);
            lRt.anchorMax = new Vector2(0.95f, 0.88f);
            lRt.sizeDelta = Vector2.zero;
            var scroll = listArea.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.scrollSensitivity = 24f;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = NewChild(listArea.transform, "Viewport");
            StretchFull(viewport);
            viewport.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            viewport.AddComponent<RectMask2D>();
            scroll.viewport = viewport.GetComponent<RectTransform>();

            var content = NewChild(viewport.transform, "Content");
            var cRt = content.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(0f, 1f);
            cRt.anchorMax = new Vector2(1f, 1f);
            cRt.pivot = new Vector2(0.5f, 1f);
            cRt.sizeDelta = Vector2.zero;
            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6;
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childForceExpandWidth = true;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandHeight = false;
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = cRt;

            // Row: + New Folder…
            MakeRow(content.transform, "+ New Folder…",
                new Color(0.20f, 0.45f, 0.30f, 1f),
                () => ShowCreateFolderModal(deckIds, () => UnityEngine.Object.Destroy(modal)));

            // Row per existing folder
            foreach (var folder in DeckOrganizationManager.Folders)
            {
                var captured = folder;
                int count = folder.DeckIds?.Count ?? 0;
                MakeRow(content.transform,
                    $"{captured.Name}   ({count})",
                    new Color(0.18f, 0.22f, 0.36f, 1f),
                    () =>
                    {
                        foreach (var id in deckIds)
                            DeckOrganizationManager.MoveDeckToFolder(id, captured.Id);
                        Plugin.Log.LogInfo($"Moved {deckIds.Count} deck(s) to folder '{captured.Name}'");
                        UnityEngine.Object.Destroy(modal);
                        DeckMultiSelectState.ExitMode();
                        DeckManagerControllerPatch.RefreshDeckGrid();
                    });
            }

            // Row: Remove from folder (if any selected decks are in one)
            bool anyInFolder = false;
            foreach (var id in deckIds)
                if (DeckOrganizationManager.FindFolderContaining(id) != null) { anyInFolder = true; break; }
            if (anyInFolder)
            {
                MakeRow(content.transform, "↶ Remove from folder",
                    new Color(0.45f, 0.25f, 0.20f, 1f),
                    () =>
                    {
                        foreach (var id in deckIds)
                            DeckOrganizationManager.MoveDeckToRoot(id);
                        Plugin.Log.LogInfo($"Removed {deckIds.Count} deck(s) from their folder(s)");
                        UnityEngine.Object.Destroy(modal);
                        DeckMultiSelectState.ExitMode();
                        DeckManagerControllerPatch.RefreshDeckGrid();
                    });
            }

            // Cancel button at the bottom
            var cancel = NewChild(dialog.transform, "Cancel");
            var cRt2 = cancel.GetComponent<RectTransform>();
            cRt2.anchorMin = new Vector2(0.30f, 0.04f);
            cRt2.anchorMax = new Vector2(0.70f, 0.14f);
            cRt2.sizeDelta = Vector2.zero;
            cancel.AddComponent<Image>().color = new Color(0.25f, 0.25f, 0.32f, 1f);
            var cb = cancel.AddComponent<Button>();
            cb.transition = Selectable.Transition.None;
            cb.onClick.AddListener(new UnityAction(() => UnityEngine.Object.Destroy(modal)));

            var cText = NewChild(cancel.transform, "Text");
            StretchFull(cText);
            var ctmp = cText.AddComponent<TextMeshProUGUI>();
            ctmp.text = "Cancel";
            ctmp.fontSize = 18;
            ctmp.color = Color.white;
            ctmp.alignment = TextAlignmentOptions.Center;
        }

        // -----------------------------------------------------------------
        // + New Folder text-input flow
        //
        // Public so the deck-manager bottom strip can open a "create a new
        // empty folder" prompt without having to first enter multi-select
        // and pick the move action. Passing an empty / null
        // <paramref name="deckIds"/> creates an empty folder; passing a
        // non-empty list creates the folder and moves those decks in.
        // -----------------------------------------------------------------
        internal static void ShowCreateFolderModal(IReadOnlyList<Guid> deckIds = null, Action onDone = null)
        {
            if (deckIds == null) deckIds = Array.Empty<Guid>();
            var modal = new GameObject("MTGAES_NewFolderModal");
            UnityEngine.Object.DontDestroyOnLoad(modal);
            var canvas = modal.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 210; // above the move modal
            var scaler = modal.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            modal.AddComponent<GraphicRaycaster>();

            var bg = NewChild(modal.transform, "Backdrop");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0, 0, 0, 0.6f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(() => UnityEngine.Object.Destroy(modal)));

            var dialog = NewChild(modal.transform, "Dialog");
            var dRt = dialog.GetComponent<RectTransform>();
            dRt.anchorMin = new Vector2(0.32f, 0.40f);
            dRt.anchorMax = new Vector2(0.68f, 0.60f);
            dRt.sizeDelta = Vector2.zero;
            dialog.AddComponent<Image>().color = new Color(0.10f, 0.10f, 0.18f, 0.98f);

            var title = NewChild(dialog.transform, "Title");
            var tRt = title.GetComponent<RectTransform>();
            tRt.anchorMin = new Vector2(0.05f, 0.72f);
            tRt.anchorMax = new Vector2(0.95f, 0.95f);
            tRt.sizeDelta = Vector2.zero;
            var titleTmp = title.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "New folder name";
            titleTmp.fontSize = 22;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = Color.white;
            titleTmp.alignment = TextAlignmentOptions.Left;

            // Text input
            var inputObj = NewChild(dialog.transform, "Input");
            var iRt = inputObj.GetComponent<RectTransform>();
            iRt.anchorMin = new Vector2(0.05f, 0.40f);
            iRt.anchorMax = new Vector2(0.95f, 0.68f);
            iRt.sizeDelta = Vector2.zero;
            inputObj.AddComponent<Image>().color = new Color(0.18f, 0.18f, 0.28f, 1f);
            var inputField = inputObj.AddComponent<TMP_InputField>();

            var textArea = NewChild(inputObj.transform, "TextArea");
            StretchFull(textArea);
            textArea.AddComponent<RectMask2D>();
            var textGO = NewChild(textArea.transform, "Text");
            StretchFull(textGO);
            var textTmp = textGO.AddComponent<TextMeshProUGUI>();
            textTmp.fontSize = 18;
            textTmp.color = Color.white;
            textTmp.alignment = TextAlignmentOptions.Left;
            textTmp.margin = new Vector4(10, 0, 6, 0);
            inputField.textComponent = textTmp;
            inputField.textViewport = textArea.GetComponent<RectTransform>();

            var ph = NewChild(textArea.transform, "Placeholder");
            StretchFull(ph);
            var phTmp = ph.AddComponent<TextMeshProUGUI>();
            phTmp.text = "e.g. Pauper";
            phTmp.fontSize = 18;
            phTmp.color = new Color(0.45f, 0.5f, 0.6f);
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.margin = new Vector4(10, 0, 6, 0);
            inputField.placeholder = phTmp;

            // Cancel / Create buttons
            MakeBtn(dialog.transform, "Cancel", "Cancel",
                new Vector2(0.08f, 0.08f), new Vector2(0.46f, 0.30f),
                new Color(0.3f, 0.3f, 0.4f, 1f),
                () => UnityEngine.Object.Destroy(modal));

            MakeBtn(dialog.transform, "Create", "Create",
                new Vector2(0.54f, 0.08f), new Vector2(0.92f, 0.30f),
                new Color(0.20f, 0.55f, 0.30f, 1f),
                () =>
                {
                    var name = (inputField.text ?? "").Trim();
                    if (string.IsNullOrEmpty(name)) name = "New Folder";
                    DeckOrganizationManager.CreateFolder(name, deckIds);
                    Plugin.Log.LogInfo($"Created folder '{name}' with {deckIds.Count} deck(s)");
                    UnityEngine.Object.Destroy(modal);
                    DeckMultiSelectState.ExitMode();
                    // Re-inject folder views (the new one needs a DeckFolderView
                    // before SetDecks can route any decks into it) then refresh.
                    DeckViewSelectorPatch.RebuildUserFolders();
                    DeckManagerControllerPatch.RefreshDeckGrid();
                    onDone?.Invoke();
                });

            // Custom blinking caret (TMP_InputField's built-in caret doesn't
            // render reliably on runtime-built input fields).
            BlinkingCaret.Attach(inputField);

            // Activate input for immediate typing
            inputField.ActivateInputField();
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------
        private static void MakeRow(Transform parent, string label, Color bg, UnityAction onClick)
        {
            var row = NewChild(parent, $"Row_{label}");
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 44;
            le.minHeight = 44;
            row.AddComponent<Image>().color = bg;

            var btn = row.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(onClick);

            var text = NewChild(row.transform, "Text");
            var tRt = text.GetComponent<RectTransform>();
            tRt.anchorMin = Vector2.zero;
            tRt.anchorMax = Vector2.one;
            tRt.sizeDelta = Vector2.zero;
            var tmp = text.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 18;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.margin = new Vector4(14, 0, 14, 0);
            tmp.raycastTarget = false;
        }

        private static void MakeBtn(Transform parent, string name, string label,
            Vector2 anchorMin, Vector2 anchorMax, Color bgColor, UnityAction onClick)
        {
            var btn = NewChild(parent, name);
            var rt = btn.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.sizeDelta = Vector2.zero;
            btn.AddComponent<Image>().color = bgColor;
            var b = btn.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(onClick);

            var t = NewChild(btn.transform, "Text");
            StretchFull(t);
            var tmp = t.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 18;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        private static void StretchFull(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;
        }
    }
}
