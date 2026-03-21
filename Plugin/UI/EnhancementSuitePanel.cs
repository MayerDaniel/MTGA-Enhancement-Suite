using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace MTGAEnhancementSuite.UI
{
    internal static class EnhancementSuitePanel
    {
        private static GameObject _panelRoot;
        private static bool _isOpen;

        public static bool IsOpen => _isOpen;

        public static void Toggle()
        {
            try
            {
                if (_panelRoot == null)
                    CreatePanel();

                _isOpen = !_isOpen;
                _panelRoot.SetActive(_isOpen);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"EnhancementSuitePanel.Toggle failed: {ex}");
            }
        }

        public static void Close()
        {
            if (_panelRoot != null && _isOpen)
            {
                _isOpen = false;
                _panelRoot.SetActive(false);
            }
        }

        private static void CreatePanel()
        {
            // Root with Canvas
            _panelRoot = new GameObject("EnhancementSuitePanel");
            UnityEngine.Object.DontDestroyOnLoad(_panelRoot);

            var canvas = _panelRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = _panelRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _panelRoot.AddComponent<GraphicRaycaster>();

            // Semi-transparent dark background (clicking it closes the panel)
            var bg = new GameObject("Background");
            bg.transform.SetParent(_panelRoot.transform, false);
            var bgRect = bg.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.05f, 0.05f, 0.1f, 0.92f);
            var bgButton = bg.AddComponent<Button>();
            bgButton.transition = Selectable.Transition.None;
            bgButton.onClick.AddListener(new UnityAction(Close));

            // Content area (centered box)
            var content = new GameObject("Content");
            content.transform.SetParent(_panelRoot.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.2f, 0.15f);
            contentRect.anchorMax = new Vector2(0.8f, 0.85f);
            contentRect.sizeDelta = Vector2.zero;
            var contentBg = content.AddComponent<Image>();
            contentBg.color = new Color(0.08f, 0.08f, 0.14f, 0.98f);

            // Title text
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(content.transform, false);
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.1f, 0.4f);
            titleRect.anchorMax = new Vector2(0.9f, 0.6f);
            titleRect.sizeDelta = Vector2.zero;
            var titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "MTGA Enhancement Suite\n<size=24>Test Panel - It Works!</size>";
            titleText.fontSize = 48;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = Color.white;

            // Close button (top right of content area)
            var closeObj = new GameObject("CloseButton");
            closeObj.transform.SetParent(content.transform, false);
            var closeRect = closeObj.AddComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.92f, 0.9f);
            closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.sizeDelta = Vector2.zero;
            var closeImg = closeObj.AddComponent<Image>();
            closeImg.color = new Color(0.6f, 0.2f, 0.2f, 0.9f);
            var closeBtn = closeObj.AddComponent<Button>();
            closeBtn.onClick.AddListener(new UnityAction(Close));

            var closeTextObj = new GameObject("CloseText");
            closeTextObj.transform.SetParent(closeObj.transform, false);
            var closeTextRect = closeTextObj.AddComponent<RectTransform>();
            closeTextRect.anchorMin = Vector2.zero;
            closeTextRect.anchorMax = Vector2.one;
            closeTextRect.sizeDelta = Vector2.zero;
            var closeText = closeTextObj.AddComponent<TextMeshProUGUI>();
            closeText.text = "X";
            closeText.fontSize = 24;
            closeText.alignment = TextAlignmentOptions.Center;
            closeText.color = Color.white;

            _panelRoot.SetActive(false);

            Plugin.Log.LogInfo("Enhancement Suite panel created");
        }
    }
}
