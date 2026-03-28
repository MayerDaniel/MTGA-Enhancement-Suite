using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MTGAEnhancementSuite.Firebase;
using MTGAEnhancementSuite.State;
using MTGAEnhancementSuite.UrlScheme;
using Newtonsoft.Json.Linq;
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
        private static Transform _lobbyListContainer;
        private static TextMeshProUGUI _statusText;
        private static Coroutine _refreshCoroutine;
        private static MonoBehaviour _coroutineRunner;
        private static string _filterFormat = "all"; // "all" = show everything

        public static bool IsOpen => _isOpen;

        public static void Toggle()
        {
            try
            {
                if (_panelRoot == null)
                    CreatePanel();

                _isOpen = !_isOpen;
                _panelRoot.SetActive(_isOpen);

                if (_isOpen)
                    RefreshLobbies();
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"EnhancementSuitePanel.Toggle failed: {ex}");
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
            _panelRoot = new GameObject("EnhancementSuitePanel");
            UnityEngine.Object.DontDestroyOnLoad(_panelRoot);
            _coroutineRunner = FirebaseClient.Instance; // Use FirebaseClient's MonoBehaviour

            var canvas = _panelRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = _panelRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _panelRoot.AddComponent<GraphicRaycaster>();

            // Semi-transparent dark background (clicking closes panel)
            var bg = CreateChild(_panelRoot.transform, "Background");
            StretchFull(bg);
            bg.AddComponent<Image>().color = new Color(0.05f, 0.05f, 0.1f, 0.92f);
            var bgBtn = bg.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(new UnityAction(Close));

            // Content area
            var content = CreateChild(_panelRoot.transform, "Content");
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.2f, 0.1f);
            contentRect.anchorMax = new Vector2(0.8f, 0.9f);
            contentRect.sizeDelta = Vector2.zero;
            content.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.14f, 0.98f);

            // Title
            var title = CreateChild(content.transform, "Title");
            var titleRect = title.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.05f, 0.92f);
            titleRect.anchorMax = new Vector2(0.7f, 1f);
            titleRect.sizeDelta = Vector2.zero;
            var titleText = title.AddComponent<TextMeshProUGUI>();
            titleText.text = "MTGA+ Server Browser";
            titleText.fontSize = 28;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Left;
            titleText.color = Color.white;

            // Refresh button
            var refreshBtn = CreateButton(content.transform, "Refresh", "Refresh",
                new Vector2(0.72f, 0.93f), new Vector2(0.85f, 0.99f));
            refreshBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() => RefreshLobbies()));

            // Close button
            var closeBtn = CreateButton(content.transform, "CloseBtn", "X",
                new Vector2(0.92f, 0.93f), new Vector2(0.98f, 0.99f));
            closeBtn.GetComponent<Image>().color = new Color(0.6f, 0.2f, 0.2f, 0.9f);
            closeBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(Close));

            // Status text
            var status = CreateChild(content.transform, "Status");
            var statusRect = status.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0.05f, 0.86f);
            statusRect.anchorMax = new Vector2(0.95f, 0.92f);
            statusRect.sizeDelta = Vector2.zero;
            _statusText = status.AddComponent<TextMeshProUGUI>();
            _statusText.text = "Loading lobbies...";
            _statusText.fontSize = 16;
            _statusText.color = new Color(0.6f, 0.6f, 0.7f);
            _statusText.alignment = TextAlignmentOptions.Left;

            // Format filter row
            var filterRow = CreateChild(content.transform, "FilterRow");
            var filterRowRect = filterRow.GetComponent<RectTransform>();
            filterRowRect.anchorMin = new Vector2(0.05f, 0.80f);
            filterRowRect.anchorMax = new Vector2(0.95f, 0.86f);
            filterRowRect.sizeDelta = Vector2.zero;

            var filterLabel = CreateChild(filterRow.transform, "FilterLabel");
            var filterLabelRect = filterLabel.GetComponent<RectTransform>();
            filterLabelRect.anchorMin = new Vector2(0f, 0f);
            filterLabelRect.anchorMax = new Vector2(0.15f, 1f);
            filterLabelRect.sizeDelta = Vector2.zero;
            var filterLabelText = filterLabel.AddComponent<TextMeshProUGUI>();
            filterLabelText.text = "Filter:";
            filterLabelText.fontSize = 16;
            filterLabelText.color = new Color(0.7f, 0.7f, 0.8f);
            filterLabelText.alignment = TextAlignmentOptions.Left;

            // "All" button
            var allBtn = CreateButton(filterRow.transform, "FilterAll", "All",
                new Vector2(0.16f, 0.05f), new Vector2(0.28f, 0.95f));
            allBtn.GetComponent<Image>().color = new Color(0.3f, 0.5f, 0.7f, 0.9f); // highlighted by default
            var allBtnRef = allBtn;

            // Build format filter buttons from loaded formats
            var formatKeys = ChallengeFormatState.FormatKeys;
            var formatOptions = ChallengeFormatState.FormatOptions;
            var filterButtons = new List<GameObject> { allBtn };

            float btnStart = 0.30f;
            float btnWidth = 0.14f;
            float btnGap = 0.01f;
            for (int i = 1; i < formatKeys.Length && i < 6; i++) // skip "none", max 5 format buttons
            {
                float left = btnStart + (i - 1) * (btnWidth + btnGap);
                float right = left + btnWidth;
                var fmtBtn = CreateButton(filterRow.transform, $"Filter_{formatKeys[i]}", formatOptions[i],
                    new Vector2(left, 0.05f), new Vector2(right, 0.95f));
                fmtBtn.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.25f, 0.9f);
                filterButtons.Add(fmtBtn);

                var capturedKey = formatKeys[i];
                var capturedButtons = filterButtons;
                var capturedIdx = filterButtons.Count - 1;
                fmtBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
                {
                    _filterFormat = capturedKey;
                    HighlightFilterButton(capturedButtons, capturedIdx);
                    RefreshLobbies();
                }));
            }

            // Wire up the All button
            var allFilterButtons = filterButtons;
            allBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                _filterFormat = "all";
                HighlightFilterButton(allFilterButtons, 0);
                RefreshLobbies();
            }));

            // Lobby list scroll area
            var scrollArea = CreateChild(content.transform, "ScrollArea");
            var scrollRect = scrollArea.GetComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0.03f, 0.12f);
            scrollRect.anchorMax = new Vector2(0.97f, 0.79f);
            scrollRect.sizeDelta = Vector2.zero;

            var scrollView = scrollArea.AddComponent<ScrollRect>();
            scrollView.horizontal = false;
            scrollView.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            var viewport = CreateChild(scrollArea.transform, "Viewport");
            StretchFull(viewport);
            viewport.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.3f);
            viewport.AddComponent<Mask>().showMaskGraphic = true;
            scrollView.viewport = viewport.GetComponent<RectTransform>();

            var listContent = CreateChild(viewport.transform, "ListContent");
            var listRect = listContent.GetComponent<RectTransform>();
            listRect.anchorMin = new Vector2(0f, 1f);
            listRect.anchorMax = new Vector2(1f, 1f);
            listRect.pivot = new Vector2(0.5f, 1f);
            listRect.sizeDelta = new Vector2(0f, 0f);

            var vlg = listContent.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight = false;
            vlg.childControlWidth = true;

            var csf = listContent.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollView.content = listRect;
            _lobbyListContainer = listContent.transform;

            // Paste invite section (bottom)
            var pasteSection = CreateChild(content.transform, "PasteSection");
            var pasteSectionRect = pasteSection.GetComponent<RectTransform>();
            pasteSectionRect.anchorMin = new Vector2(0.05f, 0.02f);
            pasteSectionRect.anchorMax = new Vector2(0.95f, 0.10f);
            pasteSectionRect.sizeDelta = Vector2.zero;

            var pasteLabel = CreateChild(pasteSection.transform, "Label");
            var pasteLabelRect = pasteLabel.GetComponent<RectTransform>();
            pasteLabelRect.anchorMin = new Vector2(0f, 0f);
            pasteLabelRect.anchorMax = new Vector2(0.2f, 1f);
            pasteLabelRect.sizeDelta = Vector2.zero;
            var pasteLabelText = pasteLabel.AddComponent<TextMeshProUGUI>();
            pasteLabelText.text = "Paste Invite:";
            pasteLabelText.fontSize = 16;
            pasteLabelText.color = Color.white;
            pasteLabelText.alignment = TextAlignmentOptions.Left;

            // Input field for pasting
            var inputObj = CreateChild(pasteSection.transform, "Input");
            var inputRect = inputObj.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0.22f, 0.1f);
            inputRect.anchorMax = new Vector2(0.78f, 0.9f);
            inputRect.sizeDelta = Vector2.zero;
            inputObj.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f, 1f);
            var inputField = inputObj.AddComponent<TMP_InputField>();

            var inputTextArea = CreateChild(inputObj.transform, "TextArea");
            StretchFull(inputTextArea);
            inputTextArea.AddComponent<RectMask2D>();

            var inputText = CreateChild(inputTextArea.transform, "Text");
            StretchFull(inputText);
            var inputTmp = inputText.AddComponent<TextMeshProUGUI>();
            inputTmp.fontSize = 14;
            inputTmp.color = Color.white;
            inputTmp.alignment = TextAlignmentOptions.Left;
            inputField.textComponent = inputTmp;
            inputField.textViewport = inputTextArea.GetComponent<RectTransform>();

            var placeholder = CreateChild(inputTextArea.transform, "Placeholder");
            StretchFull(placeholder);
            var phTmp = placeholder.AddComponent<TextMeshProUGUI>();
            phTmp.text = "mtgaes://join/...";
            phTmp.fontSize = 14;
            phTmp.color = new Color(0.4f, 0.4f, 0.5f);
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.Left;
            inputField.placeholder = phTmp;

            // Join button for pasted invite
            var joinPasteBtn = CreateButton(pasteSection.transform, "JoinPaste", "Join",
                new Vector2(0.80f, 0.1f), new Vector2(1f, 0.9f));
            var capturedInput = inputField;
            joinPasteBtn.GetComponent<Button>().onClick.AddListener(new UnityAction(() =>
            {
                var url = capturedInput.text?.Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    JoinFromUrl(url);
                }
            }));

            _panelRoot.SetActive(false);
            PerPlayerLog.Info("Server browser panel created");
        }

        public static void RefreshLobbies()
        {
            if (_statusText != null)
                _statusText.text = "Loading lobbies...";

            ClearLobbyList();

            FirebaseClient.Instance.ListPublicLobbies(lobbies =>
            {
                ClearLobbyList();

                if (lobbies == null || !lobbies.HasValues)
                {
                    if (_statusText != null)
                        _statusText.text = "No public lobbies found.";
                    return;
                }

                int count = 0;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var prop in lobbies.Properties())
                {
                    var lobby = prop.Value as JObject;
                    if (lobby == null) continue;

                    var isPublic = lobby["isPublic"]?.Value<bool>() ?? false;
                    if (!isPublic) continue;

                    // Filter stale lobbies (no heartbeat in last 2 minutes)
                    var lastHeartbeat = lobby["lastHeartbeat"]?.Value<long>() ?? 0;
                    if (now - lastHeartbeat > 120) continue;

                    var challengeId = prop.Name;
                    var hostName = lobby["hostDisplayName"]?.ToString() ?? "Unknown";
                    var format = lobby["format"]?.ToString() ?? "none";
                    var isBo3 = lobby["isBestOf3"]?.Value<bool>() ?? false;

                    // Apply format filter
                    if (_filterFormat != "all" && format != _filterFormat)
                        continue;

                    CreateLobbyRow(challengeId, hostName, format, isBo3);
                    count++;
                }

                if (_statusText != null)
                    _statusText.text = count > 0
                        ? $"{count} public {(count == 1 ? "lobby" : "lobbies")} found"
                        : "No public lobbies found.";
            });
        }

        private static void HighlightFilterButton(List<GameObject> buttons, int activeIndex)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var img = buttons[i].GetComponent<Image>();
                if (img != null)
                    img.color = i == activeIndex
                        ? new Color(0.3f, 0.5f, 0.7f, 0.9f)   // highlighted
                        : new Color(0.15f, 0.15f, 0.25f, 0.9f); // default
            }
        }

        private static void ClearLobbyList()
        {
            if (_lobbyListContainer == null) return;
            for (int i = _lobbyListContainer.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_lobbyListContainer.GetChild(i).gameObject);
        }

        private static void CreateLobbyRow(string challengeId, string hostName, string format, bool isBo3)
        {
            if (_lobbyListContainer == null) return;

            var row = new GameObject("LobbyRow");
            row.transform.SetParent(_lobbyListContainer, false);

            var rowRect = row.AddComponent<RectTransform>();
            var le = row.AddComponent<LayoutElement>();
            le.preferredHeight = 45;

            row.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.20f, 0.9f);

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10;
            hlg.padding = new RectOffset(12, 12, 6, 6);
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;

            // Host name
            var hostObj = new GameObject("Host");
            hostObj.transform.SetParent(row.transform, false);
            var hostLe = hostObj.AddComponent<LayoutElement>();
            hostLe.flexibleWidth = 2;
            var hostText = hostObj.AddComponent<TextMeshProUGUI>();
            hostText.text = hostName;
            hostText.fontSize = 18;
            hostText.color = Color.white;
            hostText.alignment = TextAlignmentOptions.Left;

            // Format
            var formatObj = new GameObject("Format");
            formatObj.transform.SetParent(row.transform, false);
            var formatLe = formatObj.AddComponent<LayoutElement>();
            formatLe.flexibleWidth = 1;
            var formatText = formatObj.AddComponent<TextMeshProUGUI>();
            formatText.text = format.Substring(0, 1).ToUpper() + format.Substring(1);
            formatText.fontSize = 16;
            formatText.color = new Color(0.4f, 0.8f, 1f);
            formatText.alignment = TextAlignmentOptions.Center;

            // Bo1/Bo3
            var boObj = new GameObject("BestOf");
            boObj.transform.SetParent(row.transform, false);
            var boLe = boObj.AddComponent<LayoutElement>();
            boLe.preferredWidth = 45;
            var boText = boObj.AddComponent<TextMeshProUGUI>();
            boText.text = isBo3 ? "Bo3" : "Bo1";
            boText.fontSize = 14;
            boText.color = isBo3 ? new Color(1f, 0.8f, 0.3f) : new Color(0.6f, 0.6f, 0.7f);
            boText.alignment = TextAlignmentOptions.Center;

            // Join button
            var joinObj = new GameObject("JoinBtn");
            joinObj.transform.SetParent(row.transform, false);
            var joinLe = joinObj.AddComponent<LayoutElement>();
            joinLe.preferredWidth = 80;
            joinObj.AddComponent<Image>().color = new Color(0.2f, 0.5f, 0.3f, 0.9f);
            var joinBtn = joinObj.AddComponent<Button>();

            var joinTextObj = new GameObject("Text");
            joinTextObj.transform.SetParent(joinObj.transform, false);
            StretchFull(joinTextObj);
            var joinText = joinTextObj.AddComponent<TextMeshProUGUI>();
            joinText.text = "Join";
            joinText.fontSize = 16;
            joinText.color = Color.white;
            joinText.alignment = TextAlignmentOptions.Center;

            var capturedId = challengeId;
            var capturedFormat = format;
            joinBtn.onClick.AddListener(new UnityAction(() =>
            {
                PerPlayerLog.Info($"Join button clicked for lobby {capturedId}");
                JoinLobby(capturedId, capturedFormat);
            }));

            PerPlayerLog.Info($"Created lobby row: {hostName} ({format}) id={challengeId}");
        }

        public static void JoinFromUrl(string url)
        {
            if (UrlSchemeRegistrar.ParseJoinUrl(url, out var challengeId, out var format))
            {
                JoinLobby(challengeId, format);
            }
            else
            {
                Toast.Error("Invalid invite link.");
            }
        }

        public static void JoinLobby(string challengeId, string format)
        {
            PerPlayerLog.Info($"Attempting to join lobby {challengeId} with format {format}");
            Toast.Info($"Joining lobby...");

            ChallengeFormatState.SelectedFormat = format;
            ChallengeFormatState.IsJoining = true;
            ChallengeFormatState.InLobbySession = true;
            ChallengeFormatState.ActiveChallengeId = Guid.Parse(challengeId);

            try
            {
                Close();

                // Use ChallengeCommunicationWrapper to join — it calls ChallengeJoin
                // AND converts the response into PVPChallengeData AND feeds it into
                // the controller via HandleChallengeGeneralUpdate.
                // This is what the normal invite-accept flow does.
                FirebaseClient.Instance.StartCoroutine(
                    JoinViaChallengeCommunicationWrapper(challengeId));
            }
            catch (Exception ex)
            {
                PerPlayerLog.Error($"JoinLobby failed: {ex}");
                Toast.Error($"Failed to join: {ex.Message}");
                ChallengeFormatState.IsJoining = false;
            }
        }

        private static IEnumerator JoinViaChallengeCommunicationWrapper(string challengeId)
        {
            PerPlayerLog.Info($"JoinViaChallengeCommunicationWrapper: {challengeId}");
            var guidId = Guid.Parse(challengeId);
            var pantryType = AccessTools.TypeByName("Pantry");

            // Step 1: Get IChallengeCommunicationWrapper from Pantry
            // (PVPChallengeController is NOT in Pantry, but IChallengeCommunicationWrapper IS)
            Type commWrapperInterfaceType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                    {
                        commWrapperInterfaceType = t; break;
                    }
                }
                if (commWrapperInterfaceType != null) break;
            }

            if (commWrapperInterfaceType == null)
            {
                PerPlayerLog.Error("IChallengeCommunicationWrapper type not found");
                Toast.Error("Challenge system not available");
                yield break;
            }

            var commWrapper = pantryType.GetMethod("Get").MakeGenericMethod(commWrapperInterfaceType)
                .Invoke(null, null);

            if (commWrapper == null)
            {
                PerPlayerLog.Error("IChallengeCommunicationWrapper not in Pantry");
                Toast.Error("Challenge system not initialized");
                yield break;
            }

            PerPlayerLog.Info($"Got ChallengeCommunicationWrapper: {commWrapper.GetType().Name}");

            // Step 2: Get the ChallengeService (IChallengeServiceWrapper) from the wrapper
            var svcField = AccessTools.Field(commWrapper.GetType(), "_challengeService");
            if (svcField == null)
            {
                PerPlayerLog.Error("_challengeService field not found");
                yield break;
            }

            var challengeService = svcField.GetValue(commWrapper);
            PerPlayerLog.Info($"Got ChallengeService: {challengeService.GetType().Name}");

            // Step 3: Call ChallengeJoin(string challengeId) -> Promise<ChallengeStatusResp>
            var joinMethod = AccessTools.Method(challengeService.GetType(), "ChallengeJoin");
            var joinPromise = joinMethod.Invoke(challengeService, new object[] { challengeId });
            PerPlayerLog.Info("ChallengeJoin called");

            // Step 4: Poll the Promise for completion
            // Promise<T> has: bool Successful, T Result (accessible after completion)
            var successProp = AccessTools.Property(joinPromise.GetType(), "Successful");
            var resultProp = AccessTools.Property(joinPromise.GetType(), "Result");

            PerPlayerLog.Info($"Promise type: {joinPromise.GetType().FullName}");
            PerPlayerLog.Info($"Successful prop: {(successProp != null ? "found" : "null")}");
            PerPlayerLog.Info($"Result prop: {(resultProp != null ? "found" : "null")}");

            // Log all properties on the promise for debugging
            foreach (var p in joinPromise.GetType().GetProperties())
            {
                PerPlayerLog.Info($"  Promise property: {p.Name} ({p.PropertyType.Name})");
            }

            bool? joinSuccess = null;
            object joinResult = null;
            var isDoneProp = AccessTools.Property(joinPromise.GetType(), "IsDone");
            var stateProp = AccessTools.Property(joinPromise.GetType(), "State");

            PerPlayerLog.Info($"IsDone prop: {(isDoneProp != null ? "found" : "null")}");

            // Wait for the promise to complete using IsDone, not Successful
            for (int i = 0; i < 30; i++)
            {
                yield return new WaitForSeconds(0.5f);

                try
                {
                    // Check IsDone first — Successful is only valid after the promise resolves
                    bool isDone = false;
                    if (isDoneProp != null)
                    {
                        isDone = (bool)isDoneProp.GetValue(joinPromise);
                    }
                    else if (stateProp != null)
                    {
                        // Fallback: check State enum (Pending=0, Resolved=1, Rejected=2)
                        var state = stateProp.GetValue(joinPromise);
                        isDone = state.ToString() != "Pending";
                        PerPlayerLog.Info($"Promise State: {state}");
                    }

                    if (isDone)
                    {
                        joinSuccess = successProp != null ? (bool)successProp.GetValue(joinPromise) : false;
                        PerPlayerLog.Info($"Promise resolved: IsDone=true, Successful={joinSuccess}");

                        // Always try to read Result, even if Successful=false
                        // The server may have accepted the join even if the promise reports failure
                        if (resultProp != null)
                        {
                            try
                            {
                                joinResult = resultProp.GetValue(joinPromise);
                                PerPlayerLog.Info($"Result: {(joinResult != null ? joinResult.GetType().Name : "null")}");
                            }
                            catch (Exception rex)
                            {
                                PerPlayerLog.Warning($"Could not read Result: {rex.Message}");
                            }
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    PerPlayerLog.Warning($"Promise poll error: {ex.Message}");
                }
            }

            // If we got a Result with challenge data, proceed even if Successful=false
            // The server may have accepted the join (host sees joiner) even when the promise reports failure
            bool hasValidResult = joinResult != null;
            if (hasValidResult && joinSuccess != true)
            {
                PerPlayerLog.Warning($"Promise reports Successful=false but Result is non-null — proceeding with join");
            }

            if (!hasValidResult && joinSuccess != true)
            {
                PerPlayerLog.Error($"ChallengeJoin failed or timed out (success={joinSuccess})");

                // Log all diagnostic fields
                try
                {
                    var promiseType = joinPromise.GetType();
                    var errorProp = AccessTools.Property(promiseType, "Error");
                    var errorSourceProp = AccessTools.Property(promiseType, "ErrorSource");
                    var isConnErrProp = AccessTools.Property(promiseType, "IsConnectionError");
                    var elapsedProp = AccessTools.Property(promiseType, "ElapsedMilliseconds");

                    if (errorProp != null)
                    {
                        var error = errorProp.GetValue(joinPromise);
                        PerPlayerLog.Error($"Error: {error}");
                        if (error != null)
                        {
                            foreach (var p in error.GetType().GetProperties())
                            {
                                try { PerPlayerLog.Error($"  Error.{p.Name} = {p.GetValue(error)}"); } catch { }
                            }
                            foreach (var f in error.GetType().GetFields())
                            {
                                try { PerPlayerLog.Error($"  Error.{f.Name} = {f.GetValue(error)}"); } catch { }
                            }
                        }
                    }
                    if (errorSourceProp != null)
                        PerPlayerLog.Error($"ErrorSource: {errorSourceProp.GetValue(joinPromise)}");
                    if (isConnErrProp != null)
                        PerPlayerLog.Error($"IsConnectionError: {isConnErrProp.GetValue(joinPromise)}");
                    if (elapsedProp != null)
                        PerPlayerLog.Error($"ElapsedMilliseconds: {elapsedProp.GetValue(joinPromise)}");
                }
                catch (Exception diagEx)
                {
                    PerPlayerLog.Error($"Failed to read diagnostics: {diagEx.Message}");
                }

                Toast.Error("Failed to join challenge. Is the host still in the lobby?");
                ChallengeFormatState.IsJoining = false;
                yield break;
            }

            PerPlayerLog.Info("ChallengeJoin succeeded!");

            // Delist lobby from server browser
            FirebaseClient.Instance.SetLobbyPrivate(challengeId, success =>
            {
                if (success)
                    PerPlayerLog.Info($"Lobby {challengeId} set to private after join");
                else
                    PerPlayerLog.Warning($"Failed to set lobby {challengeId} to private");
            });

            // Step 5: Extract Challenge protobuf from ChallengeStatusResp
            // and convert to PVPChallengeData via ConvertToClientModel (private method)
            if (joinResult != null)
            {
                PerPlayerLog.Info($"Result type: {joinResult.GetType().FullName}");

                // Log all properties/fields on the result
                foreach (var p in joinResult.GetType().GetProperties())
                    PerPlayerLog.Info($"  Result property: {p.Name} ({p.PropertyType.Name})");
                foreach (var f in joinResult.GetType().GetFields())
                    PerPlayerLog.Info($"  Result field: {f.Name} ({f.FieldType.Name})");

                // Get Challenge from ChallengeStatusResp
                object challengeProto = null;
                var cp = AccessTools.Property(joinResult.GetType(), "Challenge");
                if (cp != null)
                    challengeProto = cp.GetValue(joinResult);
                else
                {
                    var cf = AccessTools.Field(joinResult.GetType(), "Challenge")
                        ?? AccessTools.Field(joinResult.GetType(), "challenge_");
                    if (cf != null)
                        challengeProto = cf.GetValue(joinResult);
                }

                if (challengeProto != null)
                {
                    PerPlayerLog.Info($"Got Challenge protobuf: {challengeProto.GetType().Name}");

                    // ConvertToClientModel is PRIVATE — use AccessTools which finds private methods
                    var convertMethod = AccessTools.Method(commWrapper.GetType(), "ConvertToClientModel");
                    PerPlayerLog.Info($"ConvertToClientModel: {(convertMethod != null ? "found" : "null")}");

                    if (convertMethod != null)
                    {
                        var pvpData = convertMethod.Invoke(commWrapper, new object[] { challengeProto });
                        PerPlayerLog.Info($"ConvertToClientModel result: {(pvpData != null ? "OK" : "null")}");

                        if (pvpData != null)
                        {
                            // OnChallengeGeneralUpdate is an auto-property with backing field
                            // <OnChallengeGeneralUpdate>k__BackingField
                            // Use AccessTools.Property to get it, then read the delegate
                            var onUpdateProp = AccessTools.Property(commWrapper.GetType(), "OnChallengeGeneralUpdate");
                            if (onUpdateProp != null)
                            {
                                var delegateObj = onUpdateProp.GetValue(commWrapper) as System.Delegate;
                                if (delegateObj != null)
                                {
                                    PerPlayerLog.Info("Invoking OnChallengeGeneralUpdate via property");
                                    delegateObj.DynamicInvoke(pvpData);
                                    PerPlayerLog.Info("OnChallengeGeneralUpdate invoked successfully");
                                }
                                else
                                {
                                    PerPlayerLog.Warning("OnChallengeGeneralUpdate delegate is null (no subscribers)");
                                }
                            }
                            else
                            {
                                // Fallback: try the backing field directly
                                var backingField = commWrapper.GetType().GetField(
                                    "<OnChallengeGeneralUpdate>k__BackingField",
                                    System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.NonPublic);
                                if (backingField != null)
                                {
                                    var delegateObj = backingField.GetValue(commWrapper) as System.Delegate;
                                    if (delegateObj != null)
                                    {
                                        PerPlayerLog.Info("Invoking OnChallengeGeneralUpdate via backing field");
                                        delegateObj.DynamicInvoke(pvpData);
                                        PerPlayerLog.Info("OnChallengeGeneralUpdate invoked successfully");
                                    }
                                }
                                else
                                {
                                    PerPlayerLog.Warning("Could not find OnChallengeGeneralUpdate property or backing field");
                                }
                            }
                        }
                    }
                }
                else
                {
                    PerPlayerLog.Warning("Challenge protobuf not found in response");
                }
            }

            // Step 6: Navigate to the challenge view
            yield return new WaitForSeconds(1f);
            yield return NavigateToChallengeView(guidId);
        }

        private static IEnumerator NavigateToChallengeView(Guid challengeId)
        {
            PerPlayerLog.Info($"NavigateToChallengeView starting for {challengeId}");

            // Wait a moment for the ChallengeJoin to be processed by the server
            yield return new WaitForSeconds(2f);

            // The challenge data may not be in PVPChallengeController because
            // ChallengeJoin alone doesn't trigger HandleChallengeGeneralUpdate.
            // Try multiple approaches:

            // Approach 1: Try SocialUI.OpenPlayBlade (game's own navigation)
            PerPlayerLog.Info("Trying SocialUI.OpenPlayBlade...");
            if (TryOpenPlayBlade(challengeId))
            {
                PerPlayerLog.Info("SocialUI.OpenPlayBlade succeeded");
                // Wait and check if it actually worked
                yield return new WaitForSeconds(2f);
                Toast.Success("Joined lobby!");
                yield break;
            }

            // Approach 2: Navigate to Home with Challenge context, then ViewFriendChallenge
            PerPlayerLog.Info("Trying GoToLanding with Challenge context...");
            NavigateToHomeWithChallengeContext(challengeId);
            yield return new WaitForSeconds(3f);

            if (TryViewFriendChallengeDirect(challengeId))
            {
                Toast.Success("Joined lobby!");
                yield break;
            }

            // Approach 3: Just navigate to Home — the challenge notification system
            // might eventually bring up the challenge UI
            PerPlayerLog.Info("Trying simple GoToLanding...");
            NavigateToHomeSimple();
            yield return new WaitForSeconds(2f);

            Toast.Warning("Joined lobby — navigate to Challenges to see it.");
        }

        private static void NavigateToHomeWithChallengeContext(Guid challengeId)
        {
            try
            {
                var sceneLoaderType = AccessTools.TypeByName("SceneLoader");
                var getLoaderMethod = AccessTools.Method(sceneLoaderType, "GetSceneLoader");
                var sceneLoader = getLoaderMethod.Invoke(null, null);
                if (sceneLoader == null) { PerPlayerLog.Warning("SceneLoader null"); return; }

                var homeContextType = AccessTools.TypeByName("HomePageContext");
                if (homeContextType == null) { PerPlayerLog.Warning("HomePageContext type null"); return; }

                var goToLanding = AccessTools.Method(sceneLoader.GetType(), "GoToLanding",
                    new[] { homeContextType, typeof(bool) });
                if (goToLanding == null) { PerPlayerLog.Warning("GoToLanding method null"); return; }

                var context = Activator.CreateInstance(homeContextType);

                // Set InitialBladeState = Challenge
                var bladeStateField = AccessTools.Field(homeContextType, "InitialBladeState");
                var pbcType = AccessTools.TypeByName("PlayBladeController");
                var statesType = pbcType?.GetNestedType("PlayBladeVisualStates");
                if (bladeStateField != null && statesType != null)
                {
                    bladeStateField.SetValue(context, Enum.Parse(statesType, "Challenge"));
                }

                // Set ChallengeId
                var challengeIdField = AccessTools.Field(homeContextType, "ChallengeId");
                if (challengeIdField != null)
                {
                    challengeIdField.SetValue(context, challengeId);
                }

                goToLanding.Invoke(sceneLoader, new object[] { context, true }); // forceReload=true
                PerPlayerLog.Info($"GoToLanding called with ChallengeId={challengeId}, InitialBladeState=Challenge");
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"NavigateToHomeWithChallengeContext failed: {ex.Message}");
            }
        }

        private static void NavigateToHomeSimple()
        {
            try
            {
                var sceneLoaderType = AccessTools.TypeByName("SceneLoader");
                var getLoaderMethod = AccessTools.Method(sceneLoaderType, "GetSceneLoader");
                var sceneLoader = getLoaderMethod.Invoke(null, null);
                if (sceneLoader == null) return;

                var homeContextType = AccessTools.TypeByName("HomePageContext");
                if (homeContextType == null) return;

                var goToLanding = AccessTools.Method(sceneLoader.GetType(), "GoToLanding",
                    new[] { homeContextType, typeof(bool) });
                if (goToLanding == null) return;

                var context = Activator.CreateInstance(homeContextType);
                goToLanding.Invoke(sceneLoader, new object[] { context, false });
                PerPlayerLog.Info("Simple GoToLanding called");
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"NavigateToHomeSimple failed: {ex.Message}");
            }
        }

        private static object GetChallengeDataFromController(Guid challengeId,
            out Type controllerType, out object controller)
        {
            controllerType = null;
            controller = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "PVPChallengeController") { controllerType = t; break; }
                }
                if (controllerType != null) break;
            }

            if (controllerType == null)
            {
                PerPlayerLog.Warning("PVPChallengeController type not found in any assembly");
                return null;
            }

            // PVPChallengeController is a plain class (NOT MonoBehaviour) — FindObjectOfType won't work.
            // Access it via IChallengeCommunicationWrapper from Pantry instead.
            try
            {
                var pantryType = AccessTools.TypeByName("Pantry");
                Type commWrapperType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "IChallengeCommunicationWrapper" && t.IsInterface)
                        { commWrapperType = t; break; }

                if (pantryType != null && commWrapperType != null)
                {
                    var getMethod = pantryType.GetMethod("Get").MakeGenericMethod(commWrapperType);
                    var commWrapper = getMethod.Invoke(null, null);
                    if (commWrapper != null)
                    {
                        // ChallengeCommunicationWrapper has a _challengeService field
                        var serviceField = AccessTools.Field(commWrapper.GetType(), "_challengeService");
                        if (serviceField != null)
                        {
                            controller = serviceField.GetValue(commWrapper);
                            PerPlayerLog.Info($"Got challenge service via Pantry: {controller?.GetType().Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"Failed to get controller via Pantry: {ex.Message}");
            }

            if (controller == null)
            {
                PerPlayerLog.Warning("PVPChallengeController instance not found via Pantry");
                return null;
            }

            // Try the _challengeDataProvider field path
            var providerField = AccessTools.Field(controllerType, "_challengeDataProvider");
            if (providerField != null)
            {
                var provider = providerField.GetValue(controller);
                if (provider != null)
                {
                    var getChallengeMethod = AccessTools.Method(provider.GetType(), "GetChallengeData",
                        new[] { typeof(Guid) });
                    if (getChallengeMethod != null)
                    {
                        var result = getChallengeMethod.Invoke(provider, new object[] { challengeId });
                        return result;
                    }
                }
            }

            // Fallback: direct method on controller
            var getDataMethod = AccessTools.Method(controllerType, "GetChallengeData",
                new[] { typeof(Guid) });
            if (getDataMethod == null)
            {
                PerPlayerLog.Warning("GetChallengeData(Guid) method not found");
                return null;
            }

            return getDataMethod.Invoke(controller, new object[] { challengeId });
        }

        private static bool TryOpenPlayBlade(Guid challengeId)
        {
            try
            {
                var socialUIType = AccessTools.TypeByName("SocialUI");
                if (socialUIType == null)
                {
                    PerPlayerLog.Warning("SocialUI type not found");
                    return false;
                }

                var instanceField = AccessTools.Field(socialUIType, "_instance");
                var socialUI = instanceField?.GetValue(null);
                if (socialUI == null)
                {
                    PerPlayerLog.Warning("SocialUI._instance is null");
                    return false;
                }

                var openMethod = AccessTools.Method(socialUIType, "OpenPlayBlade",
                    new[] { typeof(Guid) });
                if (openMethod == null)
                {
                    PerPlayerLog.Warning("SocialUI.OpenPlayBlade(Guid) not found");
                    return false;
                }

                openMethod.Invoke(socialUI, new object[] { challengeId });
                PerPlayerLog.Info("Called SocialUI.OpenPlayBlade successfully");
                return true;
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"TryOpenPlayBlade failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryViewFriendChallengeDirect(Guid challengeId)
        {
            try
            {
                var sceneLoaderType = AccessTools.TypeByName("SceneLoader");
                var getLoaderMethod = AccessTools.Method(sceneLoaderType, "GetSceneLoader");
                var sceneLoader = getLoaderMethod.Invoke(null, null);
                if (sceneLoader == null) return false;

                var navContentProp = AccessTools.Property(sceneLoader.GetType(), "CurrentNavContent");
                var navContent = navContentProp?.GetValue(sceneLoader);
                if (navContent == null) return false;

                // Find ChallengeBladeController
                var prop = AccessTools.Property(navContent.GetType(), "ChallengeBladeController");
                object pbc = prop?.GetValue(navContent);

                if (pbc == null)
                {
                    // Try fields
                    foreach (var f in navContent.GetType().GetFields(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public))
                    {
                        if (f.Name.Contains("hallengeBlade") || f.Name.Contains("layBlade"))
                        {
                            pbc = f.GetValue(navContent);
                            if (pbc != null)
                            {
                                PerPlayerLog.Info($"Found PlayBladeController via field: {f.Name}");
                                break;
                            }
                        }
                    }
                }

                if (pbc == null)
                {
                    PerPlayerLog.Warning($"PlayBladeController not found on {navContent.GetType().Name}");
                    return false;
                }

                // Specify Guid overload — there's also a string overload we don't want
                var viewMethod = AccessTools.Method(pbc.GetType(), "ViewFriendChallenge", new[] { typeof(Guid) });
                if (viewMethod == null)
                {
                    PerPlayerLog.Warning("ViewFriendChallenge(Guid) method not found");
                    return false;
                }

                viewMethod.Invoke(pbc, new object[] { challengeId });
                PerPlayerLog.Info("Called ViewFriendChallenge directly");
                return true;
            }
            catch (Exception ex)
            {
                PerPlayerLog.Warning($"TryViewFriendChallengeDirect failed: {ex.Message}");
                return false;
            }
        }

        // --- UI Helpers ---

        private static GameObject CreateChild(Transform parent, string name)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.AddComponent<RectTransform>();
            return obj;
        }

        private static void StretchFull(GameObject obj)
        {
            var rect = obj.GetComponent<RectTransform>() ?? obj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
        }

        private static GameObject CreateButton(Transform parent, string name, string label,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            var btn = CreateChild(parent, name);
            var rect = btn.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.sizeDelta = Vector2.zero;

            btn.AddComponent<Image>().color = new Color(0.2f, 0.3f, 0.5f, 0.9f);
            btn.AddComponent<Button>();

            var textObj = CreateChild(btn.transform, "Text");
            StretchFull(textObj);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 14;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;

            return btn;
        }
    }
}
