using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolarExpanse.Multiplayer.Networking.Host;
using SolarExpanse.Multiplayer.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SolarExpanse.Multiplayer.UI;

internal sealed class MainMenuMultiplayerPanel
{
    private readonly Canvas _canvas;
    private readonly Component _menuComponent;
    private GameObject? _root;
    private GameObject? _setupRoot;
    private GameObject? _lobbyRoot;
    private TMP_InputField? _addressInput;
    private TMP_InputField? _portInput;
    private TMP_InputField? _playerNameInput;
    private TMP_InputField? _maxCompaniesInput;
    private TextMeshProUGUI? _statusText;
    private TextMeshProUGUI? _lobbyStatusText;
    private GameObject? _lobbyRowsRoot;
    private GameObject? _startGameButtonRoot;
    private readonly List<LobbyRowControls> _lobbyRows = new List<LobbyRowControls>();
    private string _lobbyRowsKey = string.Empty;

    public MainMenuMultiplayerPanel(Canvas canvas, Component menuComponent)
    {
        _canvas = canvas;
        _menuComponent = menuComponent;
    }

    public void Show()
    {
        EnsureCreated();
        if (_root != null)
        {
            _root.SetActive(true);
            RefreshFromRuntime();
        }
    }

    private void Hide()
    {
        _root?.SetActive(false);
    }

    private void EnsureCreated()
    {
        if (_root != null)
        {
            return;
        }

        _root = new GameObject("Solar Expanse Multiplayer Menu");
        _root.transform.SetParent(_canvas.transform, worldPositionStays: false);
        _root.AddComponent<MainMenuMultiplayerPanelRefresher>().Initialize(this);

        var rootRect = _root.AddComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        var blocker = _root.AddComponent<Image>();
        blocker.color = new Color(0f, 0f, 0f, 0.55f);

        var panel = new GameObject("Panel");
        panel.transform.SetParent(_root.transform, worldPositionStays: false);
        var panelRect = panel.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.anchoredPosition = Vector2.zero;
        panelRect.sizeDelta = new Vector2(640f, 520f);

        var panelImage = panel.AddComponent<Image>();
        panelImage.color = new Color(0.02f, 0.05f, 0.08f, 0.96f);

        CreateText(panel.transform, "SOLAR EXPANSE MULTIPLAYER", new Vector2(0f, 218f), new Vector2(560f, 40f), 28, FontStyles.Bold, new Color(0f, 1f, 0.88f));

        _setupRoot = new GameObject("Setup");
        _setupRoot.transform.SetParent(panel.transform, worldPositionStays: false);
        StretchToParent(_setupRoot);

        _lobbyRoot = new GameObject("Host Lobby");
        _lobbyRoot.transform.SetParent(panel.transform, worldPositionStays: false);
        StretchToParent(_lobbyRoot);
        _lobbyRoot.SetActive(false);

        _statusText = CreateText(_setupRoot.transform, string.Empty, new Vector2(0f, 176f), new Vector2(560f, 34f), 16, FontStyles.Normal, Color.white);

        _playerNameInput = CreateInput(_setupRoot.transform, "Player Name", "Player", new Vector2(-160f, 112f), new Vector2(260f, 42f));
        _addressInput = CreateInput(_setupRoot.transform, "Server Address", "127.0.0.1", new Vector2(-160f, 28f), new Vector2(260f, 42f));
        _portInput = CreateInput(_setupRoot.transform, "Port", "27777", new Vector2(170f, 28f), new Vector2(180f, 42f));
        _maxCompaniesInput = CreateInput(_setupRoot.transform, "Allowed Companies", "4", new Vector2(0f, -40f), new Vector2(220f, 42f));

        CreateButton(_setupRoot.transform, "HOST SERVER", new Vector2(-160f, -130f), new Vector2(210f, 48f), Host);
        CreateButton(_setupRoot.transform, "DIRECT CONNECT", new Vector2(160f, -130f), new Vector2(230f, 48f), Connect);
        CreateButton(_setupRoot.transform, "DISCONNECT", new Vector2(-160f, -196f), new Vector2(210f, 42f), Disconnect);
        CreateButton(_setupRoot.transform, "BACK", new Vector2(160f, -196f), new Vector2(230f, 42f), Hide);

        CreateText(_lobbyRoot.transform, "HOST LOBBY", new Vector2(0f, 160f), new Vector2(560f, 34f), 24, FontStyles.Bold, new Color(0f, 1f, 0.88f));
        _lobbyStatusText = CreateText(_lobbyRoot.transform, string.Empty, new Vector2(0f, 126f), new Vector2(560f, 28f), 16, FontStyles.Normal, Color.white);
        CreateText(_lobbyRoot.transform, "PLAYER", new Vector2(-252f, 88f), new Vector2(92f, 24f), 14, FontStyles.Bold, new Color(0f, 1f, 0.88f));
        CreateText(_lobbyRoot.transform, "COMPANY NAME", new Vector2(-112f, 88f), new Vector2(150f, 24f), 14, FontStyles.Bold, new Color(0f, 1f, 0.88f));
        CreateText(_lobbyRoot.transform, "SLOT", new Vector2(70f, 88f), new Vector2(80f, 24f), 14, FontStyles.Bold, new Color(0f, 1f, 0.88f));
        CreateText(_lobbyRoot.transform, "STARTING CORPORATION", new Vector2(218f, 88f), new Vector2(210f, 24f), 14, FontStyles.Bold, new Color(0f, 1f, 0.88f));
        _lobbyRowsRoot = new GameObject("Lobby Rows");
        _lobbyRowsRoot.transform.SetParent(_lobbyRoot.transform, worldPositionStays: false);
        StretchToParent(_lobbyRowsRoot);
        _startGameButtonRoot = CreateButton(_lobbyRoot.transform, "START GAME", new Vector2(-160f, -164f), new Vector2(210f, 48f), StartHostedGame);
        CreateButton(_lobbyRoot.transform, "DISCONNECT", new Vector2(160f, -164f), new Vector2(230f, 48f), StopHosting);

        RefreshFromRuntime();
    }

    public void RefreshFromRuntime()
    {
        var runtime = SolarExpanseMultiplayerPlugin.Runtime;
        if (runtime == null)
        {
            return;
        }

        HandlePendingStartGame(runtime);
        SetInputText(_addressInput, runtime.AddressInput);
        SetInputText(_portInput, runtime.PortInput);
        SetInputText(_playerNameInput, runtime.PlayerNameInput);
        SetInputText(_maxCompaniesInput, runtime.MaxCompaniesInput);
        if (_statusText != null)
        {
            _statusText.text = runtime.Status;
        }

        if (_lobbyStatusText != null)
        {
            _lobbyStatusText.text = $"{runtime.Status}  |  Allowed companies: {runtime.MaxCompaniesInput}";
        }

        _startGameButtonRoot?.SetActive(runtime.IsHosting);
        RefreshLobbyRows(runtime);
    }

    private void Host()
    {
        var started = SolarExpanseMultiplayerPlugin.Runtime?.HostFromMenu(
            _playerNameInput?.text ?? string.Empty,
            _portInput?.text ?? string.Empty,
            "0",
            _maxCompaniesInput?.text ?? string.Empty,
            "New Company") == true;
        RefreshFromRuntime();
        if (started)
        {
            ShowLobby();
        }
    }

    private void Connect()
    {
        SolarExpanseMultiplayerPlugin.Runtime?.ConnectFromMenu(
            _addressInput?.text ?? string.Empty,
            _playerNameInput?.text ?? string.Empty,
            _portInput?.text ?? string.Empty,
            "0");
        ShowLobby();
        RefreshFromRuntime();
    }

    private void Disconnect()
    {
        SolarExpanseMultiplayerPlugin.Runtime?.DisconnectFromMenu();
        ShowSetup();
        RefreshFromRuntime();
    }

    private static void SetInputText(TMP_InputField? input, string value)
    {
        if (input != null && string.IsNullOrEmpty(input.text))
        {
            input.text = value;
        }
    }

    private void ShowLobby()
    {
        _setupRoot?.SetActive(false);
        _lobbyRoot?.SetActive(true);
        RefreshFromRuntime();
    }

    private void ShowSetup()
    {
        _lobbyRoot?.SetActive(false);
        _setupRoot?.SetActive(true);
    }

    private void StopHosting()
    {
        SolarExpanseMultiplayerPlugin.Runtime?.DisconnectFromMenu();
        ShowSetup();
        RefreshFromRuntime();
    }

    private void StartHostedGame()
    {
        var runtime = SolarExpanseMultiplayerPlugin.Runtime;
        var hostRow = _lobbyRows.FirstOrDefault(row => row.SessionId == null);
        var corporation = hostRow?.CorporationDropdown.Selected ?? string.Empty;
        if (runtime?.UpdateHostLobbySettings(hostRow?.CompanyNameInput.text ?? string.Empty, hostRow?.SlotDropdown.Selected ?? string.Empty, corporation) == true)
        {
            runtime.BroadcastStartGameFromMenu();
            StartNewGameFromLobby(corporation);
        }

        RefreshFromRuntime();
    }

    private void HandlePendingStartGame(MultiplayerRuntime runtime)
    {
        if (!runtime.TryConsumePendingStartGame(out var startGame) || startGame == null)
        {
            return;
        }

        var localPlayer = startGame.Players.FirstOrDefault(player => player.SessionId == runtime.ClientSessionId);
        var corporation = localPlayer?.StartingCorporation;
        StartNewGameFromLobby(corporation ?? startGame.HostStartingCorporation);
    }

    private void SaveLobbySelections()
    {
        var runtime = SolarExpanseMultiplayerPlugin.Runtime;
        if (runtime == null)
        {
            return;
        }

        foreach (var row in _lobbyRows)
        {
            if (runtime.IsHosting && row.IsHost)
            {
                runtime.UpdateHostLobbySettings(row.CompanyNameInput.text, row.SlotDropdown.Selected, row.CorporationDropdown.Selected);
            }
            else if (runtime.IsHosting && row.SessionId != null)
            {
                runtime.UpdatePeerLobbySettings(row.SessionId.Value, row.CompanyNameInput.text, row.SlotDropdown.Selected, row.CorporationDropdown.Selected);
            }
            else if (row.IsLocal)
            {
                runtime.UpdateClientLobbySettings(row.CompanyNameInput.text, row.SlotDropdown.Selected, row.CorporationDropdown.Selected);
            }
        }

        RefreshFromRuntime();
    }

    private void RefreshLobbyRows(MultiplayerRuntime runtime)
    {
        if (_lobbyRowsRoot == null)
        {
            return;
        }

        var rows = GetLobbyRows(runtime).ToArray();
        var key = $"{runtime.IsHosting}|{runtime.IsClientConnected}|{runtime.MaxCompaniesInput}|{string.Join(",", rows.Select(row => $"{row.SessionId}:{row.PlayerName}:{row.IsHost}:{row.IsLocal}"))}";
        if (key != _lobbyRowsKey)
        {
            RebuildLobbyRows(rows, key);
            return;
        }

        foreach (var row in _lobbyRows)
        {
            var rowData = rows.FirstOrDefault(item => item.RowId == row.RowId);
            if (rowData == null)
            {
                continue;
            }

            row.PlayerText.text = rowData.PlayerName;
            SetInputText(row.CompanyNameInput, rowData.CompanyName);
            row.SlotDropdown.SetOptions(GetCompanySlotOptions(), rowData.CompanySlot);
            row.CorporationDropdown.SetOptions(GetCorporationOptions(), rowData.StartingCorporation);
            ApplyRowPermissions(row, rowData);
        }
    }

    private void RebuildLobbyRows(IEnumerable<LobbyRowData> rows, string key)
    {
        if (_lobbyRowsRoot == null)
        {
            return;
        }

        for (var i = _lobbyRowsRoot.transform.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.Destroy(_lobbyRowsRoot.transform.GetChild(i).gameObject);
        }

        _lobbyRows.Clear();
        _lobbyRowsKey = key;

        var rowIndex = 0;
        foreach (var row in rows)
        {
            _lobbyRows.Add(CreateLobbyRow(row, rowIndex++));
        }
    }

    private LobbyRowControls CreateLobbyRow(LobbyRowData rowData, int rowIndex)
    {
        var row = new GameObject("Lobby Row");
        row.transform.SetParent(_lobbyRowsRoot!.transform, worldPositionStays: false);
        var rect = row.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 54f - rowIndex * 54f);
        rect.sizeDelta = new Vector2(560f, 44f);

        var playerText = CreateText(row.transform, rowData.PlayerName, new Vector2(-252f, 0f), new Vector2(92f, 38f), 14, FontStyles.Normal, Color.white);
        playerText.alignment = TextAlignmentOptions.MidlineLeft;
        var companyNameInput = CreateInput(row.transform, string.Empty, rowData.CompanyName, new Vector2(-112f, 0f), new Vector2(150f, 38f));
        var slotDropdown = new SimpleDropdown(row.transform, string.Empty, new Vector2(70f, 0f), new Vector2(80f, 38f), GetCompanySlotOptions(), rowData.CompanySlot, _ => SaveLobbySelections());
        var corporationDropdown = new SimpleDropdown(row.transform, string.Empty, new Vector2(218f, 0f), new Vector2(210f, 38f), GetCorporationOptions(), rowData.StartingCorporation, _ => SaveLobbySelections());
        companyNameInput.onEndEdit.AddListener(_ => SaveLobbySelections());

        var controls = new LobbyRowControls(rowData.RowId, rowData.SessionId, rowData.IsHost, rowData.IsLocal, playerText, companyNameInput, slotDropdown, corporationDropdown);
        ApplyRowPermissions(controls, rowData);
        return controls;
    }

    private IEnumerable<LobbyRowData> GetLobbyRows(MultiplayerRuntime runtime)
    {
        if (runtime.IsHosting)
        {
            yield return new LobbyRowData(
                "host",
                null,
                true,
                true,
                $"Host: {runtime.PlayerNameInput}",
                runtime.CompanyNameInput,
                runtime.CompanySlotInput,
                runtime.StartingCorporationInput,
                true,
                true,
                true);

            foreach (var peer in runtime.ConnectedPeers.OrderBy(peer => peer.AssignedCompanySlot))
            {
                yield return new LobbyRowData(
                    peer.SessionId.ToString(),
                    peer.SessionId,
                    false,
                    false,
                    peer.PlayerName,
                    peer.CompanyName,
                    peer.AssignedCompanySlot.ToString(),
                    peer.StartingCorporation,
                    false,
                    true,
                    true);
            }

            yield break;
        }

        var presence = runtime.LatestPresence;
        if (runtime.IsClientConnected || runtime.ClientSessionId != System.Guid.Empty)
        {
            yield return new LobbyRowData(
                "host",
                null,
                true,
                false,
                $"Host: {(string.IsNullOrWhiteSpace(presence.HostPlayerName) ? runtime.HostPlayerName : presence.HostPlayerName)}",
                string.IsNullOrWhiteSpace(presence.HostCompanyName) ? "New Company" : presence.HostCompanyName,
                presence.HostAssignedCompanySlot.ToString(),
                string.IsNullOrWhiteSpace(presence.HostStartingCorporation) ? "Solex" : presence.HostStartingCorporation,
                false,
                false,
                false);

            var localSeen = false;
            foreach (var player in runtime.VisiblePlayers.OrderBy(player => player.AssignedCompanySlot))
            {
                var isLocal = player.SessionId == runtime.ClientSessionId;
                localSeen |= isLocal;
                yield return new LobbyRowData(
                    player.SessionId.ToString(),
                    player.SessionId,
                    false,
                    isLocal,
                    isLocal ? $"You: {player.PlayerName}" : player.PlayerName,
                    player.CompanyName,
                    player.AssignedCompanySlot.ToString(),
                    player.StartingCorporation,
                    isLocal,
                    isLocal,
                    isLocal);
            }

            if (!localSeen)
            {
                yield return new LobbyRowData(
                    "local",
                    runtime.ClientSessionId == System.Guid.Empty ? null : runtime.ClientSessionId,
                    false,
                    true,
                    $"You: {runtime.PlayerNameInput}",
                    runtime.CompanyNameInput,
                    runtime.CompanySlotInput,
                    runtime.StartingCorporationInput,
                    true,
                    true,
                    true);
            }
        }
    }

    private static void ApplyRowPermissions(LobbyRowControls row, LobbyRowData rowData)
    {
        row.CompanyNameInput.interactable = rowData.CanEditCompanyName;
        row.SlotDropdown.SetInteractable(rowData.CanEditCompanySlot);
        row.CorporationDropdown.SetInteractable(rowData.CanEditStartingCorporation);
    }

    private void OpenNewGameFlow()
    {
        AccessTools.Method(_menuComponent.GetType(), "OnClickBtnSingle")?.Invoke(_menuComponent, null);
    }

    private void StartNewGameFromLobby(string corporation)
    {
        Hide();
        OpenNewGameFlow();

        if (_menuComponent is MonoBehaviour behaviour)
        {
            behaviour.StartCoroutine(CompleteNewGameFlow(corporation));
            return;
        }

        CompleteNewGameFlowNow(corporation);
    }

    private IEnumerator CompleteNewGameFlow(string corporation)
    {
        yield return null;
        yield return null;
        CompleteNewGameFlowNow(corporation);
    }

    private void CompleteNewGameFlowNow(string corporation)
    {
        ApplyStartingCorporationSelection(corporation);
        if (AdvanceThroughGameCustomization())
        {
            SolarExpanseMultiplayerPlugin.Log?.LogInfo($"Started multiplayer game with lobby corporation '{corporation}'.");
            return;
        }

        SolarExpanseMultiplayerPlugin.Log?.LogWarning("Could not find the game customization flow; falling back to normal solar system start.");
        AccessTools.Method(_menuComponent.GetType(), "OnClickBtnNormalSolarSystem")?.Invoke(_menuComponent, null);
    }

    private static bool AdvanceThroughGameCustomization()
    {
        var customizationType = AccessTools.TypeByName("Game.UI.Menu.GameCustomizationUI.GameCustomizationUI");
        if (customizationType == null)
        {
            return false;
        }

        foreach (var customization in Resources.FindObjectsOfTypeAll(customizationType))
        {
            if (customization is not Component component || !component.gameObject.scene.IsValid())
            {
                continue;
            }

            var pages = AccessTools.Field(customizationType, "pages")?.GetValue(customization) as CanvasGroup[];
            var currentPageField = AccessTools.Field(customizationType, "currentPage");
            var nextMethod = AccessTools.Method(customizationType, "OnNextButtonClick");
            if (pages == null || pages.Length == 0 || currentPageField == null || nextMethod == null)
            {
                continue;
            }

            for (var guard = 0; guard <= pages.Length; guard++)
            {
                var currentPage = currentPageField.GetValue(customization) as int? ?? 0;
                nextMethod.Invoke(customization, null);
                if (currentPage >= pages.Length - 1)
                {
                    return true;
                }
            }

            return true;
        }

        return false;
    }

    private IEnumerable<string> GetCompanySlotOptions()
    {
        var max = 4;
        if (int.TryParse(_maxCompaniesInput?.text, out var parsed) && parsed > 0)
        {
            max = parsed;
        }
        else if (int.TryParse(SolarExpanseMultiplayerPlugin.Runtime?.MaxCompaniesInput, out parsed) && parsed > 0)
        {
            max = parsed;
        }

        return Enumerable.Range(0, max).Select(slot => slot.ToString()).ToArray();
    }

    private static IEnumerable<string> GetCorporationOptions()
    {
        var options = new List<string>();
        foreach (var corporationSelect in Resources.FindObjectsOfTypeAll<CorporationSelectUI>())
        {
            var items = AccessTools.Field(typeof(CorporationSelectUI), "corporationItems")?.GetValue(corporationSelect) as IEnumerable;
            if (items == null)
            {
                continue;
            }

            foreach (var item in items)
            {
                var translationId = AccessTools.Field(item.GetType(), "translationId")?.GetValue(item) as string;
                var displayName = GetCorporationDisplayName(translationId);
                if (!string.IsNullOrWhiteSpace(displayName) && !options.Contains(displayName!))
                {
                    options.Add(displayName!);
                }
            }
        }

        if (options.Count == 0)
        {
            options.AddRange(new[] { "Solex", "NASA", "ESA", "CNSA" });
        }

        return options;
    }

    private static void ApplyStartingCorporationSelection(string selectedCorporation)
    {
        if (string.IsNullOrWhiteSpace(selectedCorporation))
        {
            return;
        }

        foreach (var corporationSelect in Resources.FindObjectsOfTypeAll<CorporationSelectUI>())
        {
            var items = AccessTools.Field(typeof(CorporationSelectUI), "corporationItems")?.GetValue(corporationSelect) as IEnumerable;
            if (items == null)
            {
                continue;
            }

            foreach (var item in items)
            {
                var translationId = AccessTools.Field(item.GetType(), "translationId")?.GetValue(item) as string;
                var displayName = GetCorporationDisplayName(translationId);
                if (!string.Equals(translationId, selectedCorporation, System.StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(displayName, selectedCorporation, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AccessTools.Method(typeof(CorporationSelectUI), "SetSelectedCorporation")?.Invoke(corporationSelect, new[] { item });
                var toggle = AccessTools.Field(item.GetType(), "toggle")?.GetValue(item) as Toggle;
                if (toggle != null)
                {
                    toggle.isOn = true;
                }

                return;
            }
        }
    }

    private static string? GetCorporationDisplayName(string? translationId)
    {
        if (string.IsNullOrWhiteSpace(translationId))
        {
            return translationId;
        }

        var key = $"Game.UI.CustomizationScreen.CorporationInfo.{translationId}.Name";
        var leManagerType = AccessTools.TypeByName("Language.LEManager");
        var getMethod = leManagerType == null ? null : AccessTools.Method(leManagerType, "Get", new[] { typeof(string), typeof(string) });
        var translated = getMethod?.Invoke(null, new object?[] { key, null }) as string;
        return string.IsNullOrWhiteSpace(translated) || translated == key ? translationId : translated;
    }

    private static void StretchToParent(GameObject root)
    {
        var rect = root.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    internal static TMP_InputField CreateInput(Transform parent, string label, string placeholder, Vector2 position, Vector2 size)
    {
        CreateText(parent, label, position + new Vector2(0f, 32f), new Vector2(size.x, 24f), 15, FontStyles.Bold, new Color(0f, 1f, 0.88f));

        var root = new GameObject(label + " Input");
        root.transform.SetParent(parent, worldPositionStays: false);
        var rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        var image = root.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.95f);

        var input = root.AddComponent<TMP_InputField>();
        input.textViewport = rect;

        var text = CreateText(root.transform, string.Empty, Vector2.zero, size - new Vector2(18f, 8f), 17, FontStyles.Normal, Color.black);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(9f, 4f);
        text.rectTransform.offsetMax = new Vector2(-9f, -4f);

        var placeholderText = CreateText(root.transform, placeholder, Vector2.zero, size - new Vector2(18f, 8f), 17, FontStyles.Italic, new Color(0.25f, 0.25f, 0.25f));
        placeholderText.alignment = TextAlignmentOptions.MidlineLeft;
        placeholderText.rectTransform.anchorMin = Vector2.zero;
        placeholderText.rectTransform.anchorMax = Vector2.one;
        placeholderText.rectTransform.offsetMin = new Vector2(9f, 4f);
        placeholderText.rectTransform.offsetMax = new Vector2(-9f, -4f);

        input.textComponent = text;
        input.placeholder = placeholderText;
        input.text = placeholder;
        return input;
    }

    internal static GameObject CreateButton(Transform parent, string label, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        var root = new GameObject(label + " Button");
        root.transform.SetParent(parent, worldPositionStays: false);
        var rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        var image = root.AddComponent<Image>();
        image.color = new Color(0f, 0.85f, 0.75f, 0.95f);

        var button = root.AddComponent<Button>();
        button.onClick.AddListener(onClick);

        var text = CreateText(root.transform, label, Vector2.zero, size, 18, FontStyles.Bold, Color.black);
        text.alignment = TextAlignmentOptions.Center;
        return root;
    }

    internal static TextMeshProUGUI CreateText(Transform parent, string text, Vector2 position, Vector2 size, int fontSize, FontStyles style, Color color)
    {
        var root = new GameObject("Text");
        root.transform.SetParent(parent, worldPositionStays: false);
        var rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        var tmp = root.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;
        return tmp;
    }
}

internal sealed class LobbyRowControls
{
    public LobbyRowControls(
        string rowId,
        System.Guid? sessionId,
        bool isHost,
        bool isLocal,
        TextMeshProUGUI playerText,
        TMP_InputField companyNameInput,
        SimpleDropdown slotDropdown,
        SimpleDropdown corporationDropdown)
    {
        RowId = rowId;
        SessionId = sessionId;
        IsHost = isHost;
        IsLocal = isLocal;
        PlayerText = playerText;
        CompanyNameInput = companyNameInput;
        SlotDropdown = slotDropdown;
        CorporationDropdown = corporationDropdown;
    }

    public string RowId { get; }
    public System.Guid? SessionId { get; }
    public bool IsHost { get; }
    public bool IsLocal { get; }
    public TextMeshProUGUI PlayerText { get; }
    public TMP_InputField CompanyNameInput { get; }
    public SimpleDropdown SlotDropdown { get; }
    public SimpleDropdown CorporationDropdown { get; }
}

internal sealed class LobbyRowData
{
    public LobbyRowData(
        string rowId,
        System.Guid? sessionId,
        bool isHost,
        bool isLocal,
        string playerName,
        string companyName,
        string companySlot,
        string startingCorporation,
        bool canEditCompanyName,
        bool canEditCompanySlot,
        bool canEditStartingCorporation)
    {
        RowId = rowId;
        SessionId = sessionId;
        IsHost = isHost;
        IsLocal = isLocal;
        PlayerName = playerName;
        CompanyName = companyName;
        CompanySlot = companySlot;
        StartingCorporation = startingCorporation;
        CanEditCompanyName = canEditCompanyName;
        CanEditCompanySlot = canEditCompanySlot;
        CanEditStartingCorporation = canEditStartingCorporation;
    }

    public string RowId { get; }
    public System.Guid? SessionId { get; }
    public bool IsHost { get; }
    public bool IsLocal { get; }
    public string PlayerName { get; }
    public string CompanyName { get; }
    public string CompanySlot { get; }
    public string StartingCorporation { get; }
    public bool CanEditCompanyName { get; }
    public bool CanEditCompanySlot { get; }
    public bool CanEditStartingCorporation { get; }
}

internal sealed class MainMenuMultiplayerPanelRefresher : MonoBehaviour
{
    private MainMenuMultiplayerPanel? _panel;
    private float _nextRefreshTime;

    public void Initialize(MainMenuMultiplayerPanel panel)
    {
        _panel = panel;
    }

    private void Update()
    {
        if (_panel == null || Time.unscaledTime < _nextRefreshTime)
        {
            return;
        }

        _nextRefreshTime = Time.unscaledTime + 0.5f;
        _panel.RefreshFromRuntime();
    }
}
