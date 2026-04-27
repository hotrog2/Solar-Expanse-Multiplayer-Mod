using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Logging;
using Manager;
using SolarExpanse.Multiplayer.Configuration;
using SolarExpanse.Multiplayer.Game.Company;
using SolarExpanse.Multiplayer.Game.Time;
using SolarExpanse.Multiplayer.Game.Trade;
using SolarExpanse.Multiplayer.Networking.Client;
using SolarExpanse.Multiplayer.Networking.Host;
using SolarExpanse.Multiplayer.Networking.Protocol;
using UnityEngine;

namespace SolarExpanse.Multiplayer.Runtime;

public sealed class MultiplayerRuntime : IDisposable
{
    public const string NewGameStartMode = "NewGame";
    public const string SaveGameStartMode = "SaveGame";
    public const string NewGameSourceLabel = "New Game";
    public const string SaveGameSourceLabel = "Load Save";

    private readonly ManualLogSource _log;
    private readonly MultiplayerConfig _config;
    private readonly DirectConnectHost _host;
    private readonly DirectConnectClient _client;
    private readonly TimeSyncService _timeSyncService;
    private readonly CompanyOwnershipService _companyOwnershipService;
    private readonly CompanyStateSyncService _companyStateSyncService;
    private readonly TradeSyncService _tradeSyncService;
    private readonly ConcurrentQueue<float> _pendingTimeScaleRequests = new ConcurrentQueue<float>();
    private readonly object _startGameLock = new object();
    private readonly object _chatLock = new object();
    private readonly object _publicEventsLock = new object();
    private readonly List<ChatMessage> _chatMessages = new List<ChatMessage>();
    private readonly List<PublicCompanyEventMessage> _publicEvents = new List<PublicCompanyEventMessage>();
    private PlayerPresenceMessage _latestPresence = new PlayerPresenceMessage();
    private StartGameMessage? _pendingStartGame;
    private Guid _activeStartSessionId = Guid.Empty;
    private bool _sharedSessionStarted;
    private bool _gameReadySent;
    private float _nextAutoResyncAt;

    private readonly Rect _windowRect = new Rect(20f, 20f, 430f, 360f);
    private Rect _multiplayerWindowRect = new Rect(20f, 390f, 640f, 560f);
    private Vector2 _playersScroll;
    private Vector2 _chatScroll;

    private string _status = "Idle";
    private string _addressInput;
    private string _portInput;
    private string _playerNameInput;
    private string _companyNameInput;
    private string _companySlotInput;
    private string _maxCompaniesInput;
    private string _startingCorporationInput = "Solex";
    private string _hostGameSourceInput = NewGameSourceLabel;
    private string _hostSaveNameInput = string.Empty;
    private string _chatInput = string.Empty;
    private Guid _clientSessionId = Guid.Empty;
    private string _hostPlayerName = string.Empty;

    public string Status => _status;
    public string AddressInput => _addressInput;
    public string PortInput => _portInput;
    public string PlayerNameInput => _playerNameInput;
    public string CompanyNameInput => _companyNameInput;
    public string CompanySlotInput => _companySlotInput;
    public string MaxCompaniesInput => _maxCompaniesInput;
    public string StartingCorporationInput => _startingCorporationInput;
    public string HostGameSourceInput => _hostGameSourceInput;
    public string HostSaveNameInput => _hostSaveNameInput;
    public bool IsHostSaveGameSelected => string.Equals(_hostGameSourceInput, SaveGameSourceLabel, StringComparison.OrdinalIgnoreCase);
    public bool IsHosting => _host.IsRunning;
    public bool IsClientConnected => _client.IsConnected;
    public Guid ClientSessionId => _clientSessionId;
    public string HostPlayerName => _hostPlayerName;
    public IReadOnlyCollection<HostPeerState> ConnectedPeers => _host.Peers;
    public IReadOnlyCollection<PlayerPresenceDto> VisiblePlayers => _latestPresence.Players;
    public PlayerPresenceMessage LatestPresence => _latestPresence;

    public bool IsStorylineDisabled => _config.DisableStorylineSystems.Value;

    public bool TryGetCompanyDisplayName(global::Game.Company? company, out string displayName)
    {
        return _companyOwnershipService.TryGetCompanyDisplayName(company, out displayName);
    }

    public MultiplayerRuntime(ManualLogSource log, MultiplayerConfig config)
    {
        _log = log;
        _config = config;
        _host = new DirectConnectHost(log);
        _client = new DirectConnectClient(log);
        _timeSyncService = new TimeSyncService();
        _companyOwnershipService = new CompanyOwnershipService();
        _companyStateSyncService = new CompanyStateSyncService(_companyOwnershipService);
        _tradeSyncService = new TradeSyncService(_companyOwnershipService, log);

        _addressInput = _config.HostAddress.Value;
        _portInput = _config.HostPort.Value.ToString();
        _playerNameInput = _config.PlayerName.Value;
        _companyNameInput = _config.CompanyName.Value;
        _companySlotInput = _config.RequestedCompanySlot.Value.ToString();
        _maxCompaniesInput = _config.MaxCompanies.Value.ToString();
    }

    public void Initialize()
    {
        _host.TimeScaleRequested += (_, request) => _pendingTimeScaleRequests.Enqueue(request.RequestedTimeScale);
        _host.CompanyStateSnapshotReceived += OnCompanyStateSnapshotReceived;
        _host.GameReadyReceived += OnGameReadyReceived;
        _host.CompanyActionCommandReceived += OnCompanyActionCommandReceived;
        _host.ChatMessageReceived += OnHostChatMessageReceived;
        _host.ResyncRequested += OnResyncRequested;
        _host.TradeOfferSyncReceived += OnHostTradeOfferSyncReceived;
        _host.TradeOfferFulfillReceived += OnHostTradeOfferFulfillReceived;
        _client.Joined += OnJoined;
        _client.TimeSnapshotReceived += snapshot => _timeSyncService.QueueSnapshot(snapshot);
        _client.CompanyStateReceived += snapshot =>
        {
            if (!snapshot.HostAuthoritative && snapshot.CompanySlot != _companyOwnershipService.LocalCompanySlot)
            {
                RequestResync(snapshot.CompanySlot, "Ignored non-authoritative remote snapshot.");
                return;
            }

            _companyStateSyncService.HandleIncomingSnapshot(snapshot);
            if (!string.IsNullOrWhiteSpace(snapshot.CompanyName))
            {
                _companyOwnershipService.SetCompanyDisplayName(snapshot.CompanySlot, snapshot.CompanyName);
            }
        };
        _client.CompanyActionResultReceived += OnCompanyActionResultReceived;
        _client.ChatMessageReceived += AddChatMessage;
        _client.PublicCompanyEventReceived += AddPublicCompanyEvent;
        _client.TradeOfferSyncReceived += OnTradeOfferSyncReceived;
        _client.TradeOfferFulfillReceived += OnTradeOfferFulfillReceived;
        _client.PresenceUpdated += presence =>
        {
            _latestPresence = presence;
            _hostPlayerName = presence.HostPlayerName;
            RefreshCompanyDisplayNames();
            _status = $"Connected: {presence.Players.Count} remote player(s) visible.";
        };
        _client.StartGameReceived += OnStartGameReceived;
        _client.Disconnected += () => _status = "Disconnected";

        if (_config.AutoStart.Value)
        {
            if (_config.Mode.Value == MultiplayerMode.Host)
            {
                StartHost();
            }
            else if (_config.Mode.Value == MultiplayerMode.Client)
            {
                StartClient();
            }
        }
    }

    public void Update()
    {
        while (_pendingTimeScaleRequests.TryDequeue(out var requestedTimeScale))
        {
            var controller = UnityEngine.Object.FindObjectOfType<TimeController>();
            if (controller != null)
            {
                _log.LogInfo($"Applying remote time-scale request: {requestedTimeScale}");
                controller.SetTimescale(requestedTimeScale, false, false);
            }
        }

        RefreshCompanyDisplayNames();

        if (_host.IsRunning)
        {
            _companyStateSyncService.TickHost(_host, _companyOwnershipService.LocalCompanySlot, _playerNameInput);
        }
        else if (_client.IsConnected)
        {
            _companyStateSyncService.TickClient(_client, _companyOwnershipService.LocalCompanySlot, _playerNameInput);
        }

        _companyStateSyncService.ApplyRemoteSnapshots(_companyOwnershipService.LocalCompanySlot);
        TryAutoRequestResync();
        TrySendGameReady();
    }

    public void DrawGui()
    {
        if (_config.ShowDebugWindow.Value)
        {
            GUILayout.Window(17291, _windowRect, _ => DrawWindowContents(), "Solar Expanse Multiplayer");
        }

        if (ShouldDrawMultiplayerWindow())
        {
            _multiplayerWindowRect = GUILayout.Window(17292, _multiplayerWindowRect, _ => DrawMultiplayerWindowContents(), "Multiplayer Status");
        }
    }

    public void OnTimeControllerUpdated(TimeController controller)
    {
        if (_host.IsRunning)
        {
            _timeSyncService.HostTick(controller, _host);
            return;
        }

        if (_client.IsConnected)
        {
            _timeSyncService.ApplyPendingSnapshot(controller);
        }
    }

    public bool OnTimeScaleChangeRequested(TimeController controller, ref float requestedTimeScale)
    {
        if (_timeSyncService.IsApplyingRemoteSnapshot)
        {
            return true;
        }

        if (_client.IsConnected)
        {
            _client.Send(new TimeScaleRequestMessage
            {
                MessageType = nameof(TimeScaleRequestMessage),
                RequestedTimeScale = requestedTimeScale
            });

            _status = $"Requested host time scale {requestedTimeScale:0.##}";
            return false;
        }

        return true;
    }

    public bool HostFromMenu(string playerName, string port, string companySlot, string maxCompanies, string companyName)
    {
        _playerNameInput = string.IsNullOrWhiteSpace(playerName) ? "Host" : playerName.Trim();
        _companyNameInput = string.IsNullOrWhiteSpace(companyName) ? _config.CompanyName.Value : companyName.Trim();
        _portInput = string.IsNullOrWhiteSpace(port) ? _config.HostPort.Value.ToString() : port.Trim();
        _companySlotInput = string.IsNullOrWhiteSpace(companySlot) ? _config.RequestedCompanySlot.Value.ToString() : companySlot.Trim();
        _maxCompaniesInput = string.IsNullOrWhiteSpace(maxCompanies) ? _config.MaxCompanies.Value.ToString() : maxCompanies.Trim();

        _config.PlayerName.Value = _playerNameInput;
        _config.CompanyName.Value = _companyNameInput;
        if (int.TryParse(_portInput, out var parsedPort))
        {
            _config.HostPort.Value = parsedPort;
        }

        if (int.TryParse(_companySlotInput, out var parsedSlot))
        {
            _config.RequestedCompanySlot.Value = parsedSlot;
        }

        if (int.TryParse(_maxCompaniesInput, out var parsedMaxCompanies))
        {
            _config.MaxCompanies.Value = parsedMaxCompanies;
        }

        return StartHost();
    }

    public void ConnectFromMenu(string address, string playerName, string port, string companySlot)
    {
        _addressInput = string.IsNullOrWhiteSpace(address) ? _config.HostAddress.Value : address.Trim();
        _playerNameInput = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        _portInput = string.IsNullOrWhiteSpace(port) ? _config.HostPort.Value.ToString() : port.Trim();
        _companySlotInput = string.IsNullOrWhiteSpace(companySlot) ? _config.RequestedCompanySlot.Value.ToString() : companySlot.Trim();

        _config.HostAddress.Value = _addressInput;
        _config.PlayerName.Value = _playerNameInput;
        if (int.TryParse(_portInput, out var parsedPort))
        {
            _config.HostPort.Value = parsedPort;
        }

        if (int.TryParse(_companySlotInput, out var parsedSlot))
        {
            _config.RequestedCompanySlot.Value = parsedSlot;
        }

        StartClient();
    }

    public void DisconnectFromMenu()
    {
        Disconnect();
    }

    public bool BroadcastStartGameFromMenu()
    {
        if (!_host.IsRunning)
        {
            _status = "Only the host can start the shared session.";
            return false;
        }

        RefreshCompanyDisplayNames();
        var startGame = _host.CreateStartGameMessage();
        if (!ConfigureStartGameMessage(startGame))
        {
            return false;
        }

        _host.BroadcastStartGame(startGame);
        _activeStartSessionId = _host.CurrentStartSessionId;
        _sharedSessionStarted = true;
        _gameReadySent = false;
        _status = startGame.StartMode == SaveGameStartMode
            ? $"Starting shared session from save '{startGame.HostSaveName}'."
            : "Starting shared session from a new game.";
        return true;
    }

    public IReadOnlyList<string> GetHostSaveGameNames()
    {
        try
        {
            return GetSaveInfos()
                .Keys
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Could not enumerate save games: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public bool UpdateHostGameSource(string gameSource, string saveName)
    {
        _hostGameSourceInput = string.Equals(gameSource, SaveGameSourceLabel, StringComparison.OrdinalIgnoreCase)
            ? SaveGameSourceLabel
            : NewGameSourceLabel;

        if (!IsHostSaveGameSelected)
        {
            _status = _host.IsRunning ? $"Hosting on port {_host.BoundPort}." : _status;
            return true;
        }

        var saveNames = GetHostSaveGameNames();
        if (saveNames.Count == 0)
        {
            _hostSaveNameInput = string.Empty;
            _status = "No save games found.";
            return false;
        }

        _hostSaveNameInput = saveNames.Contains(saveName)
            ? saveName
            : saveNames[0];
        _status = $"Selected host save '{_hostSaveNameInput}'.";
        return true;
    }

    public string GetHostSaveSelectionSummary()
    {
        if (!IsHostSaveGameSelected)
        {
            return "Host will start a new multiplayer game.";
        }

        if (string.IsNullOrWhiteSpace(_hostSaveNameInput))
        {
            return "Select a save before starting.";
        }

        if (!TryGetSaveInfo(_hostSaveNameInput, out var saveInfo) || saveInfo == null)
        {
            return $"Save '{_hostSaveNameInput}' was not found.";
        }

        var systemName = saveInfo.startGameConfiguration?.PlanetarySystemDescriptor?.Name ??
                         saveInfo.startGameConfiguration?.PlanetarySystemDescriptor?.ID ??
                         "Unknown system";
        var date = saveInfo.gameDate == default
            ? "unknown date"
            : saveInfo.gameDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"Host will load '{_hostSaveNameInput}' ({systemName}, {date}).";
    }

    public bool TryConsumePendingStartGame(out StartGameMessage? message)
    {
        lock (_startGameLock)
        {
            message = _pendingStartGame;
            _pendingStartGame = null;
            return message != null;
        }
    }

    public bool SendPrivateCompanyAction(string actionType, IReadOnlyDictionary<string, string> arguments)
    {
        if (!_sharedSessionStarted || _activeStartSessionId == Guid.Empty)
        {
            _status = "Cannot send company action before the shared session starts.";
            return false;
        }

        var command = new CompanyActionCommandMessage
        {
            MessageType = nameof(CompanyActionCommandMessage),
            CommandId = Guid.NewGuid(),
            StartSessionId = _activeStartSessionId,
            CompanySlot = _companyOwnershipService.LocalCompanySlot,
            ActionType = actionType,
            Arguments = arguments.ToDictionary(x => x.Key, x => x.Value)
        };

        if (_host.IsRunning)
        {
            _log.LogInfo($"Host local company action recorded: {actionType}.");
            TryBroadcastPublicCompanyEventForAction(
                _companyOwnershipService.LocalCompanySlot,
                _playerNameInput,
                _companyNameInput,
                actionType,
                arguments);
            return true;
        }

        if (!_client.IsConnected)
        {
            _status = "Cannot send company action while disconnected.";
            return false;
        }

        _client.Send(command);
        _status = $"Sent private company action: {actionType}.";
        return true;
    }

    public bool RecordPrivateCompanyActionForCompany(global::Game.Company? company, string actionType, IReadOnlyDictionary<string, string> arguments)
    {
        if (!_companyOwnershipService.TryGetSlotForCompany(company, out var companySlot) ||
            companySlot != _companyOwnershipService.LocalCompanySlot)
        {
            return false;
        }

        return SendPrivateCompanyAction(actionType, arguments);
    }

    public bool RecordPublicCompanyEventForCompany(global::Game.Company? company, string actionType, IReadOnlyDictionary<string, string> arguments)
    {
        if (!_companyOwnershipService.TryGetSlotForCompany(company, out var companySlot) ||
            companySlot != _companyOwnershipService.LocalCompanySlot)
        {
            return false;
        }

        return SendPrivateCompanyAction(actionType, arguments);
    }

    public void RecordMarketOfferChanged(global::Game.ObjectInfoDataScripts.Offer? offer, string operation)
    {
        if (TradeSyncService.SuppressLocalHooks || !IsTradeSyncActive())
        {
            return;
        }

        var message = _tradeSyncService.CreateOfferSync(
            offer,
            operation,
            _playerNameInput,
            _companyNameInput,
            _companyOwnershipService.LocalCompanySlot);

        if (message == null)
        {
            return;
        }

        NormalizeTradeOfferIdentity(message);
        if (_host.IsRunning)
        {
            _host.Broadcast(message);
            return;
        }

        if (_client.IsConnected)
        {
            _client.Send(message);
        }
    }

    public void RecordMarketOfferFulfilled(global::Game.ObjectInfoDataScripts.Offer? offer, global::Game.Company? takerCompany, double count, bool accepted)
    {
        if (!accepted || TradeSyncService.SuppressLocalHooks || !IsTradeSyncActive())
        {
            return;
        }

        if (!_companyOwnershipService.TryGetSlotForCompany(takerCompany, out var takerSlot) ||
            takerSlot != _companyOwnershipService.LocalCompanySlot)
        {
            return;
        }

        var message = _tradeSyncService.CreateFulfillMessage(
            offer,
            takerCompany,
            count,
            _playerNameInput,
            _companyNameInput);

        if (message == null)
        {
            return;
        }

        if (_host.IsRunning)
        {
            _host.Broadcast(message);
            AddTradePublicEvent(message);
            return;
        }

        if (_client.IsConnected)
        {
            AddTradePublicEvent(message);
            _client.Send(message);
        }
    }

    public bool UpdateHostLobbySettings(string companyName, string companySlot, string startingCorporation)
    {
        _companyNameInput = string.IsNullOrWhiteSpace(companyName) ? _companyNameInput : companyName.Trim();
        _config.CompanyName.Value = _companyNameInput;
        _startingCorporationInput = string.IsNullOrWhiteSpace(startingCorporation) ? _startingCorporationInput : startingCorporation.Trim();

        if (!int.TryParse(companySlot, out var requestedSlot))
        {
            _status = "Invalid company slot.";
            return false;
        }

        if (!int.TryParse(_maxCompaniesInput, out var maxCompanies) || maxCompanies <= 0)
        {
            _status = "Invalid allowed company count.";
            return false;
        }

        if (requestedSlot < 0 || requestedSlot >= maxCompanies)
        {
            _status = $"Company slot must be between 0 and {maxCompanies - 1}.";
            return false;
        }

        if (_host.IsRunning && _host.Peers.Any(peer => peer.AssignedCompanySlot == requestedSlot))
        {
            _status = $"Company slot {requestedSlot} is already assigned.";
            return false;
        }

        _companySlotInput = requestedSlot.ToString();
        _config.RequestedCompanySlot.Value = requestedSlot;
        _companyOwnershipService.SetLocalCompanySlot(requestedSlot);
        _companyOwnershipService.SetCompanyDisplayName(requestedSlot, _companyNameInput);
        RefreshCompanyDisplayNames();
        if (_host.IsRunning)
        {
            _status = $"Hosting on port {_host.BoundPort}.";
            _host.UpdateHostLobbySettings(_companyNameInput, requestedSlot, _startingCorporationInput);
        }

        return true;
    }

    public bool UpdateClientLobbySettings(string companyName, string companySlot, string startingCorporation)
    {
        _companyNameInput = string.IsNullOrWhiteSpace(companyName) ? _companyNameInput : companyName.Trim();
        _startingCorporationInput = string.IsNullOrWhiteSpace(startingCorporation) ? _startingCorporationInput : startingCorporation.Trim();

        if (!int.TryParse(companySlot, out var requestedSlot))
        {
            _status = "Invalid company slot.";
            return false;
        }

        _companySlotInput = requestedSlot.ToString();
        _config.CompanyName.Value = _companyNameInput;
        _config.RequestedCompanySlot.Value = requestedSlot;
        _companyOwnershipService.SetLocalCompanySlot(requestedSlot);
        _companyOwnershipService.SetCompanyDisplayName(requestedSlot, _companyNameInput);
        RefreshCompanyDisplayNames();

        if (_client.IsConnected)
        {
            _client.SendLobbySettings(_companyNameInput, requestedSlot, _startingCorporationInput);
            _status = "Sent lobby settings to host.";
        }

        return true;
    }

    public bool UpdatePeerLobbySettings(Guid sessionId, string companyName, string companySlot, string startingCorporation)
    {
        if (!int.TryParse(companySlot, out var requestedSlot))
        {
            _status = "Invalid company slot.";
            return false;
        }

        if (!int.TryParse(_maxCompaniesInput, out var maxCompanies) || maxCompanies <= 0)
        {
            _status = "Invalid allowed company count.";
            return false;
        }

        if (requestedSlot < 0 || requestedSlot >= maxCompanies)
        {
            _status = $"Company slot must be between 0 and {maxCompanies - 1}.";
            return false;
        }

        if (!_host.UpdatePeerLobbySettings(sessionId, companyName, requestedSlot, startingCorporation))
        {
            _status = "Could not update player lobby settings.";
            return false;
        }

        _companyOwnershipService.SetCompanyDisplayName(requestedSlot, companyName);
        RefreshCompanyDisplayNames();
        _status = $"Hosting on port {_host.BoundPort}.";
        return true;
    }

    private bool StartHost()
    {
        if (!int.TryParse(_portInput, out var port))
        {
            _status = "Invalid host port.";
            return false;
        }

        if (!int.TryParse(_companySlotInput, out var requestedSlot))
        {
            requestedSlot = 0;
        }

        if (!int.TryParse(_maxCompaniesInput, out var maxCompanies) || maxCompanies <= 0)
        {
            _status = "Invalid allowed company count.";
            return false;
        }

        if (requestedSlot < 0 || requestedSlot >= maxCompanies)
        {
            _status = $"Company slot must be between 0 and {maxCompanies - 1}.";
            return false;
        }

        _companyOwnershipService.SetLocalCompanySlot(requestedSlot);
        _companyOwnershipService.SetCompanyDisplayName(requestedSlot, _companyNameInput);
        RefreshCompanyDisplayNames();

        _host.Start(
            port,
            _playerNameInput,
            _companyNameInput,
            requestedSlot,
            _startingCorporationInput,
            maxCompanies,
            (playerName, requestedSlot, inUseSlots) =>
            {
                var reservedSlots = inUseSlots.Concat(new[] { _companyOwnershipService.LocalCompanySlot }).ToArray();
                return _companyOwnershipService.AssignCompanySlot(Guid.NewGuid(), requestedSlot, reservedSlots, _config.MaxCompanies.Value);
            },
            () =>
            {
                var controller = UnityEngine.Object.FindObjectOfType<TimeController>();
                return new JoinAcceptedMessage
                {
                    HostGameTimeTicks = controller != null ? controller.CurrentTime.Ticks : DateTime.MinValue.Ticks,
                    HostTimeScale = controller != null ? controller.CurrentTimeScale : 1f
                };
            });

        _status = $"Hosting on port {_host.BoundPort}.";
        _config.Mode.Value = MultiplayerMode.Host;
        return true;
    }

    private async void StartClient()
    {
        if (!int.TryParse(_portInput, out var port))
        {
            _status = "Invalid host port.";
            return;
        }

        if (!int.TryParse(_companySlotInput, out var requestedSlot))
        {
            requestedSlot = 0;
        }

        _status = $"Connecting to {_addressInput}:{port}...";
        _config.Mode.Value = MultiplayerMode.Client;

        try
        {
            await _client.ConnectAsync(_addressInput, port, _playerNameInput, _companyNameInput, requestedSlot, PluginInfo.Version);
        }
        catch (Exception ex)
        {
            _status = $"Connect failed: {ex.Message}";
            _log.LogWarning(_status);
        }
    }

    private void Disconnect()
    {
        _client.Disconnect();
        _host.Stop();
        _latestPresence = new PlayerPresenceMessage();
        _clientSessionId = Guid.Empty;
        _hostPlayerName = string.Empty;
        _activeStartSessionId = Guid.Empty;
        _sharedSessionStarted = false;
        _gameReadySent = false;
        lock (_startGameLock)
        {
            _pendingStartGame = null;
        }

        _status = "Disconnected";
        _config.Mode.Value = MultiplayerMode.Disabled;
    }

    private void OnJoined(JoinAcceptedMessage message)
    {
        _clientSessionId = message.SessionId;
        _hostPlayerName = message.HostPlayerName;
        _companyOwnershipService.SetLocalCompanySlot(message.AssignedCompanySlot);
        _companyOwnershipService.SetCompanyDisplayName(message.AssignedCompanySlot, _companyNameInput);
        _companySlotInput = message.AssignedCompanySlot.ToString();
        RefreshCompanyDisplayNames();
        _timeSyncService.QueueSnapshot(new TimeSnapshotMessage
        {
            MessageType = nameof(TimeSnapshotMessage),
            GameTimeTicks = message.HostGameTimeTicks,
            TimeScale = message.HostTimeScale,
            Sequence = 0
        });

        _status = $"Joined host '{message.HostPlayerName}' as company slot {message.AssignedCompanySlot}.";
        _log.LogInfo(_status);
    }

    private void OnStartGameReceived(StartGameMessage message)
    {
        _activeStartSessionId = message.SessionId;
        _sharedSessionStarted = true;
        _gameReadySent = false;

        _companyOwnershipService.SetCompanyDisplayName(message.HostAssignedCompanySlot, message.HostCompanyName);
        foreach (var player in message.Players)
        {
            _companyOwnershipService.SetCompanyDisplayName(player.AssignedCompanySlot, player.CompanyName);
        }

        var localPlayer = message.Players.FirstOrDefault(player => player.SessionId == _clientSessionId);
        if (localPlayer != null)
        {
            _companyNameInput = localPlayer.CompanyName;
            _companySlotInput = localPlayer.AssignedCompanySlot.ToString();
            _startingCorporationInput = localPlayer.StartingCorporation;
            _companyOwnershipService.SetLocalCompanySlot(localPlayer.AssignedCompanySlot);
            _companyOwnershipService.SetCompanyDisplayName(localPlayer.AssignedCompanySlot, localPlayer.CompanyName);
        }

        _maxCompaniesInput = message.MaxCompanySlots.ToString();
        RefreshCompanyDisplayNames();
        _status = "Host started the shared session.";
        lock (_startGameLock)
        {
            _pendingStartGame = message;
        }
    }

    private void TrySendGameReady()
    {
        if (!_sharedSessionStarted || _gameReadySent || _activeStartSessionId == Guid.Empty)
        {
            return;
        }

        if (!IsGameWorldReady())
        {
            return;
        }

        RefreshCompanyDisplayNames();
        _gameReadySent = true;
        if (_host.IsRunning)
        {
            SendKnownSnapshotsToAllPeers();
            _status = "Shared session ready on host.";
            return;
        }

        if (_client.IsConnected)
        {
            _client.Send(new GameReadyMessage
            {
                MessageType = nameof(GameReadyMessage),
                StartSessionId = _activeStartSessionId,
                ClientSessionId = _clientSessionId,
                CompanySlot = _companyOwnershipService.LocalCompanySlot,
                PlayerName = _playerNameInput
            });
            _status = "Joined shared session; waiting for host state.";
        }
    }

    private static bool IsGameWorldReady()
    {
        return UnityEngine.Object.FindObjectOfType<GameManager>() != null &&
               UnityEngine.Object.FindObjectOfType<ObjectInfoManager>() != null;
    }

    private void OnGameReadyReceived(HostPeerState peer, GameReadyMessage message)
    {
        if (message.StartSessionId != _activeStartSessionId)
        {
            _log.LogWarning($"Ignoring stale ready message from {peer.PlayerName}.");
            return;
        }

        peer.IsGameReady = true;
        peer.LastReadyStartSessionId = message.StartSessionId;
        _log.LogInfo($"Player '{peer.PlayerName}' is ready in shared session as company slot {peer.AssignedCompanySlot}.");
        SendKnownSnapshotsToPeer(peer);
    }

    private void OnCompanyActionCommandReceived(HostPeerState peer, CompanyActionCommandMessage command)
    {
        var result = ValidateCompanyAction(peer, command);
        _host.Send(peer, result);

        if (!result.Accepted)
        {
            _log.LogWarning($"Rejected company action '{command.ActionType}' from {peer.PlayerName}: {result.Reason}");
            return;
        }

        _log.LogInfo($"Accepted private company action '{command.ActionType}' from {peer.PlayerName} for company slot {peer.AssignedCompanySlot}.");
        TryBroadcastPublicCompanyEventForAction(
            peer.AssignedCompanySlot,
            peer.PlayerName,
            peer.CompanyName,
            command.ActionType,
            command.Arguments);
    }

    private CompanyActionResultMessage ValidateCompanyAction(HostPeerState peer, CompanyActionCommandMessage command)
    {
        if (command.StartSessionId != _activeStartSessionId)
        {
            return CreateActionResult(command, peer.AssignedCompanySlot, accepted: false, "Stale shared-session id.");
        }

        if (!peer.IsGameReady)
        {
            return CreateActionResult(command, peer.AssignedCompanySlot, accepted: false, "Player is not ready in the shared session.");
        }

        if (command.CompanySlot != peer.AssignedCompanySlot)
        {
            return CreateActionResult(command, peer.AssignedCompanySlot, accepted: false, "Company slot does not match the assigned slot.");
        }

        if (string.IsNullOrWhiteSpace(command.ActionType))
        {
            return CreateActionResult(command, peer.AssignedCompanySlot, accepted: false, "Missing action type.");
        }

        return CreateActionResult(command, peer.AssignedCompanySlot, accepted: true, "Accepted.");
    }

    private static CompanyActionResultMessage CreateActionResult(CompanyActionCommandMessage command, int companySlot, bool accepted, string reason)
    {
        return new CompanyActionResultMessage
        {
            MessageType = nameof(CompanyActionResultMessage),
            CommandId = command.CommandId,
            CompanySlot = companySlot,
            ActionType = command.ActionType,
            Accepted = accepted,
            Reason = reason
        };
    }

    private void OnCompanyActionResultReceived(CompanyActionResultMessage result)
    {
        if (result.Accepted)
        {
            _status = $"Host accepted private action: {result.ActionType}.";
            return;
        }

        _status = $"Host rejected {result.ActionType}: {result.Reason}";
        _log.LogWarning(_status);
    }

    private void OnHostChatMessageReceived(HostPeerState peer, ChatMessage message)
    {
        var normalized = NormalizeChatMessage(message, peer.SessionId, peer.PlayerName, peer.CompanyName, peer.AssignedCompanySlot);
        AddChatMessage(normalized);
        _host.Broadcast(normalized);
    }

    private void OnHostTradeOfferSyncReceived(HostPeerState peer, TradeOfferSyncMessage message)
    {
        message.OwnerCompanySlot = peer.AssignedCompanySlot;
        message.OwnerPlayerName = peer.PlayerName;
        message.OwnerCompanyName = peer.CompanyName;
        message.SentUtcTicks = DateTime.UtcNow.Ticks;
        NormalizeTradeOfferIdentity(message);
        _tradeSyncService.ApplyOfferSync(message);
        _host.BroadcastExcept(peer.SessionId, message);
    }

    private void OnTradeOfferSyncReceived(TradeOfferSyncMessage message)
    {
        _tradeSyncService.ApplyOfferSync(message);
    }

    private void OnHostTradeOfferFulfillReceived(HostPeerState peer, TradeOfferFulfillMessage message)
    {
        message.TakerCompanySlot = peer.AssignedCompanySlot;
        message.TakerPlayerName = peer.PlayerName;
        message.TakerCompanyName = peer.CompanyName;
        message.SentUtcTicks = DateTime.UtcNow.Ticks;
        _tradeSyncService.ApplyFulfillment(message);
        AddTradePublicEvent(message);
        _host.BroadcastExcept(peer.SessionId, message);
    }

    private void OnTradeOfferFulfillReceived(TradeOfferFulfillMessage message)
    {
        _tradeSyncService.ApplyFulfillment(message);
        AddTradePublicEvent(message);
    }

    private void SendChatMessage()
    {
        var text = (_chatInput ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        _chatInput = string.Empty;
        var message = CreateLocalChatMessage(text);
        if (_host.IsRunning)
        {
            AddChatMessage(message);
            _host.Broadcast(message);
            return;
        }

        if (_client.IsConnected)
        {
            _client.Send(message);
            return;
        }

        AddChatMessage(message);
    }

    private ChatMessage CreateLocalChatMessage(string text)
    {
        return new ChatMessage
        {
            MessageType = nameof(ChatMessage),
            MessageId = Guid.NewGuid(),
            SenderSessionId = _clientSessionId,
            CompanySlot = _companyOwnershipService.LocalCompanySlot,
            PlayerName = string.IsNullOrWhiteSpace(_playerNameInput) ? "Player" : _playerNameInput.Trim(),
            CompanyName = string.IsNullOrWhiteSpace(_companyNameInput) ? "Company" : _companyNameInput.Trim(),
            Text = Truncate(text, 240),
            SentUtcTicks = DateTime.UtcNow.Ticks
        };
    }

    private static ChatMessage NormalizeChatMessage(ChatMessage message, Guid senderSessionId, string playerName, string companyName, int companySlot)
    {
        return new ChatMessage
        {
            MessageType = nameof(ChatMessage),
            MessageId = message.MessageId == Guid.Empty ? Guid.NewGuid() : message.MessageId,
            SenderSessionId = senderSessionId,
            CompanySlot = companySlot,
            PlayerName = Truncate(string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim(), 48),
            CompanyName = Truncate(string.IsNullOrWhiteSpace(companyName) ? $"Company {companySlot}" : companyName.Trim(), 64),
            Text = Truncate((message.Text ?? string.Empty).Trim(), 240),
            SentUtcTicks = message.SentUtcTicks > 0 ? message.SentUtcTicks : DateTime.UtcNow.Ticks
        };
    }

    private void AddChatMessage(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        lock (_chatLock)
        {
            if (_chatMessages.Any(x => x.MessageId == message.MessageId && message.MessageId != Guid.Empty))
            {
                return;
            }

            _chatMessages.Add(message);
            if (_chatMessages.Count > 100)
            {
                _chatMessages.RemoveRange(0, _chatMessages.Count - 100);
            }
        }
    }

    private void OnResyncRequested(HostPeerState peer, SyncResyncRequestMessage request)
    {
        SendKnownSnapshotsToPeer(peer, request.RequestedCompanySlot, forceFull: true);
        _status = $"Sent full resync to {peer.PlayerName}: {request.Reason}";
    }

    private void RequestResync(int requestedCompanySlot, string reason)
    {
        if (_host.IsRunning)
        {
            foreach (var peer in _host.Peers)
            {
                SendKnownSnapshotsToPeer(peer, requestedCompanySlot, forceFull: true);
            }

            _status = "Host resent full public state.";
            return;
        }

        if (!_client.IsConnected)
        {
            _status = "Cannot request resync while disconnected.";
            return;
        }

        _client.Send(new SyncResyncRequestMessage
        {
            MessageType = nameof(SyncResyncRequestMessage),
            RequestId = Guid.NewGuid(),
            RequestedCompanySlot = requestedCompanySlot,
            Reason = reason,
            SentUtcTicks = DateTime.UtcNow.Ticks
        });

        if (requestedCompanySlot >= 0)
        {
            _companyStateSyncService.MarkResyncRequested(requestedCompanySlot, reason);
        }

        _status = "Requested full public-state resync from host.";
    }

    private void TryAutoRequestResync()
    {
        if (!_client.IsConnected || UnityEngine.Time.unscaledTime < _nextAutoResyncAt)
        {
            return;
        }

        var slots = _companyStateSyncService.GetSlotsNeedingResync(_companyOwnershipService.LocalCompanySlot);
        if (slots.Count == 0)
        {
            return;
        }

        _nextAutoResyncAt = UnityEngine.Time.unscaledTime + 5f;
        RequestResync(slots[0], "Automatic repair after sequence/fingerprint mismatch.");
    }

    private void TryBroadcastPublicCompanyEventForAction(int companySlot, string playerName, string companyName, string actionType, IReadOnlyDictionary<string, string> arguments)
    {
        if (!IsPublicCompanyEvent(actionType))
        {
            return;
        }

        var eventMessage = new PublicCompanyEventMessage
        {
            MessageType = nameof(PublicCompanyEventMessage),
            EventId = Guid.NewGuid(),
            CompanySlot = companySlot,
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName,
            CompanyName = string.IsNullOrWhiteSpace(companyName) ? $"Company {companySlot}" : companyName,
            EventType = actionType,
            Summary = BuildPublicEventSummary(companyName, actionType, arguments),
            SentUtcTicks = DateTime.UtcNow.Ticks,
            Data = arguments.ToDictionary(x => x.Key, x => x.Value)
        };

        AddPublicCompanyEvent(eventMessage);
        if (_host.IsRunning)
        {
            _host.Broadcast(eventMessage);
        }
    }

    private static bool IsPublicCompanyEvent(string actionType)
    {
        return string.Equals(actionType, "CompleteProduction", StringComparison.OrdinalIgnoreCase) ||
               actionType.IndexOf("Launch", StringComparison.OrdinalIgnoreCase) >= 0 ||
               actionType.IndexOf("Arrive", StringComparison.OrdinalIgnoreCase) >= 0 ||
               actionType.IndexOf("Visible", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildPublicEventSummary(string companyName, string actionType, IReadOnlyDictionary<string, string> arguments)
    {
        var name = string.IsNullOrWhiteSpace(companyName) ? "A company" : companyName.Trim();
        arguments.TryGetValue("ProductionItemTypeId", out var itemType);
        arguments.TryGetValue("ObjectName", out var objectName);

        if (string.Equals(actionType, "CompleteProduction", StringComparison.OrdinalIgnoreCase))
        {
            var item = string.IsNullOrWhiteSpace(itemType) ? "a build" : itemType;
            return string.IsNullOrWhiteSpace(objectName)
                ? $"{name} completed {item}."
                : $"{name} completed {item} at {objectName}.";
        }

        arguments.TryGetValue("SpacecraftName", out var spacecraftName);
        arguments.TryGetValue("MissionTarget", out var missionTarget);
        if (string.Equals(actionType, "SpacecraftLaunch", StringComparison.OrdinalIgnoreCase))
        {
            var craft = string.IsNullOrWhiteSpace(spacecraftName) ? "a spacecraft" : spacecraftName;
            return string.IsNullOrWhiteSpace(missionTarget)
                ? $"{name} launched {craft}."
                : $"{name} launched {craft} toward {missionTarget}.";
        }

        if (string.Equals(actionType, "SpacecraftArrive", StringComparison.OrdinalIgnoreCase))
        {
            var craft = string.IsNullOrWhiteSpace(spacecraftName) ? "a spacecraft" : spacecraftName;
            return string.IsNullOrWhiteSpace(missionTarget)
                ? $"{name} has a spacecraft arriving."
                : $"{name}'s {craft} is arriving at {missionTarget}.";
        }

        return $"{name}: {actionType}.";
    }

    private void AddPublicCompanyEvent(PublicCompanyEventMessage eventMessage)
    {
        if (eventMessage.EventId == Guid.Empty)
        {
            eventMessage.EventId = Guid.NewGuid();
        }

        if (eventMessage.SentUtcTicks <= 0)
        {
            eventMessage.SentUtcTicks = DateTime.UtcNow.Ticks;
        }

        lock (_publicEventsLock)
        {
            if (_publicEvents.Any(x => x.EventId == eventMessage.EventId))
            {
                return;
            }

            _publicEvents.Add(eventMessage);
            if (_publicEvents.Count > 100)
            {
                _publicEvents.RemoveRange(0, _publicEvents.Count - 100);
            }
        }
    }

    private void AddTradePublicEvent(TradeOfferFulfillMessage message)
    {
        if (!_companyOwnershipService.TryGetCompanyDisplayName(message.OwnerCompanySlot, out var ownerCompanyName))
        {
            ownerCompanyName = $"Company {message.OwnerCompanySlot}";
        }

        var takerCompanyName = string.IsNullOrWhiteSpace(message.TakerCompanyName)
            ? $"Company {message.TakerCompanySlot}"
            : message.TakerCompanyName;

        AddPublicCompanyEvent(new PublicCompanyEventMessage
        {
            MessageType = nameof(PublicCompanyEventMessage),
            EventId = message.RequestId,
            CompanySlot = message.TakerCompanySlot,
            PlayerName = message.TakerPlayerName,
            CompanyName = takerCompanyName,
            EventType = "TradeFulfilled",
            Summary = $"{takerCompanyName} traded with {ownerCompanyName}.",
            SentUtcTicks = DateTime.UtcNow.Ticks,
            Data = new Dictionary<string, string>
            {
                ["OfferId"] = message.OfferId.ToString(CultureInfo.InvariantCulture),
                ["OwnerCompanySlot"] = message.OwnerCompanySlot.ToString(CultureInfo.InvariantCulture),
                ["TakerCompanySlot"] = message.TakerCompanySlot.ToString(CultureInfo.InvariantCulture),
                ["Count"] = message.Count.ToString(CultureInfo.InvariantCulture)
            }
        });
    }

    private PublicCompanyEventMessage[] GetPublicEventsSnapshot()
    {
        lock (_publicEventsLock)
        {
            return _publicEvents.ToArray();
        }
    }

    private void SendKnownSnapshotsToAllPeers()
    {
        foreach (var peer in _host.Peers)
        {
            SendKnownSnapshotsToPeer(peer, requestedCompanySlot: -1, forceFull: true);
        }
    }

    private void SendKnownSnapshotsToPeer(HostPeerState peer, int requestedCompanySlot = -1, bool forceFull = true)
    {
        var localSnapshot = _companyStateSyncService.CaptureForcedPublicSnapshot(_companyOwnershipService.LocalCompanySlot, _playerNameInput, hostAuthoritative: true, fullSnapshot: forceFull);
        if (localSnapshot != null)
        {
            _companyStateSyncService.HandleIncomingSnapshot(localSnapshot);
            if (requestedCompanySlot < 0 || requestedCompanySlot == localSnapshot.CompanySlot)
            {
                _host.Send(peer, localSnapshot);
            }
        }

        foreach (var snapshot in _companyStateSyncService.LatestSnapshots.Values.OrderBy(x => x.CompanySlot))
        {
            if (localSnapshot != null && snapshot.CompanySlot == localSnapshot.CompanySlot)
            {
                continue;
            }

            if (snapshot.CompanySlot == peer.AssignedCompanySlot)
            {
                continue;
            }

            if (requestedCompanySlot >= 0 && requestedCompanySlot != snapshot.CompanySlot)
            {
                continue;
            }

            _host.Send(peer, _companyStateSyncService.CreateOutgoingPublicSnapshot(snapshot, hostAuthoritative: true, fullSnapshot: forceFull));
        }

        SendKnownTradeOffersToPeer(peer);
    }

    private void SendKnownTradeOffersToPeer(HostPeerState peer)
    {
        foreach (var offerSync in _tradeSyncService.CreateFullOfferSync(_playerNameInput, _companyNameInput))
        {
            if (offerSync.OwnerCompanySlot == peer.AssignedCompanySlot)
            {
                continue;
            }

            NormalizeTradeOfferIdentity(offerSync);
            _host.Send(peer, offerSync);
        }
    }

    private void OnCompanyStateSnapshotReceived(HostPeerState peer, CompanyStateSnapshotMessage snapshot)
    {
        var normalizedCompanyId = snapshot.CompanyId;
        var normalizedCompanyName = snapshot.CompanyName;

        if (_companyOwnershipService.TryGetCompanyBySlot(peer.AssignedCompanySlot, out var assignedCompany) && assignedCompany != null)
        {
            normalizedCompanyId = assignedCompany.ID ?? normalizedCompanyId;
            normalizedCompanyName = assignedCompany.name ?? assignedCompany.ID ?? normalizedCompanyName;
        }

        if (!string.IsNullOrWhiteSpace(peer.CompanyName))
        {
            normalizedCompanyName = peer.CompanyName;
        }

        var normalized = new CompanyStateSnapshotMessage
        {
            MessageType = nameof(CompanyStateSnapshotMessage),
            Sequence = 0,
            SentUtcTicks = snapshot.SentUtcTicks,
            FullSnapshot = snapshot.FullSnapshot,
            HostAuthoritative = false,
            CompanySlot = peer.AssignedCompanySlot,
            CompanyId = normalizedCompanyId,
            CompanyName = normalizedCompanyName,
            OwnerPlayerName = peer.PlayerName,
            Money = snapshot.Money,
            TotalProfit = snapshot.TotalProfit,
            DiscoveredSystemsCount = snapshot.DiscoveredSystemsCount,
            CompletedResearchCount = snapshot.CompletedResearchCount,
            CompletedResearchIds = snapshot.CompletedResearchIds ?? new System.Collections.Generic.List<string>(),
            ActiveResearch = snapshot.ActiveResearch ?? new System.Collections.Generic.List<ResearchProgressDto>(),
            OwnedInventories = snapshot.OwnedInventories ?? new System.Collections.Generic.List<ObjectInventorySnapshotDto>()
        };

        _companyStateSyncService.HandleIncomingSnapshot(normalized);
        var latest = _companyStateSyncService.LatestSnapshots.TryGetValue(peer.AssignedCompanySlot, out var latestSnapshot)
            ? latestSnapshot
            : normalized;
        var publicSnapshot = _companyStateSyncService.CreateOutgoingPublicSnapshot(latest, hostAuthoritative: true, fullSnapshot: snapshot.FullSnapshot);
        _companyOwnershipService.SetCompanyDisplayName(peer.AssignedCompanySlot, normalizedCompanyName);
        RefreshCompanyDisplayNames();
        _companyStateSyncService.HandleIncomingSnapshot(publicSnapshot);
        _host.Broadcast(publicSnapshot);
    }

    private bool ConfigureStartGameMessage(StartGameMessage startGame)
    {
        if (!IsHostSaveGameSelected)
        {
            startGame.StartMode = NewGameStartMode;
            startGame.HostSaveName = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(_hostSaveNameInput))
        {
            var saveNames = GetHostSaveGameNames();
            if (saveNames.Count > 0)
            {
                _hostSaveNameInput = saveNames[0];
            }
        }

        if (string.IsNullOrWhiteSpace(_hostSaveNameInput) ||
            !TryGetSaveInfo(_hostSaveNameInput, out var saveInfo) ||
            saveInfo == null)
        {
            _status = "Select a valid save game before starting.";
            return false;
        }

        var config = saveInfo.startGameConfiguration;
        if (saveInfo.saveVersionCode < LoadSaveManager.SaveVersionCode)
        {
            _status = $"Save '{_hostSaveNameInput}' is from an unsupported older save format.";
            return false;
        }

        startGame.StartMode = SaveGameStartMode;
        startGame.HostSaveName = _hostSaveNameInput;
        startGame.HostSaveGameDateTicks = saveInfo.gameDate.Ticks;
        startGame.PlanetarySystemId = config?.PlanetarySystemDescriptor?.ID ?? string.Empty;
        startGame.PlanetarySystemSceneName = config?.PlanetarySystemDescriptor?.SceneName ?? string.Empty;
        startGame.PlanetarySystemCustomJsonFileName = config?.PlanetarySystemDescriptor?.CustomJSONFileName ?? string.Empty;
        startGame.StartGameEpochId = config?.StartGameEpoch?.ID ?? string.Empty;
        startGame.GameDifficulty = config?.GameDifficulty.ToString() ?? string.Empty;
        startGame.SetupAIs = config?.setupAIs ?? false;
        startGame.EnabledRivalCorporationIds = config?.enabledRivalCorporations?
            .Where(company => company != null && !string.IsNullOrWhiteSpace(company.ID))
            .Select(company => company.ID)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        return true;
    }

    private bool TryGetSaveInfo(string saveName, out SaveInfo? saveInfo)
    {
        saveInfo = null;
        if (string.IsNullOrWhiteSpace(saveName))
        {
            return false;
        }

        try
        {
            var infos = GetSaveInfos();
            return infos.TryGetValue(saveName, out saveInfo);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Could not read save info for '{saveName}': {ex.Message}");
            return false;
        }
    }

    private static Dictionary<string, SaveInfo> GetSaveInfos()
    {
        var manager = FindLoadSaveManager();
        return manager?.GetAllSaveFilesInfos() ?? new Dictionary<string, SaveInfo>();
    }

    private static LoadSaveManager? FindLoadSaveManager()
    {
        var manager = UnityEngine.Object.FindObjectOfType<LoadSaveManager>();
        if (manager != null)
        {
            return manager;
        }

        return Resources.FindObjectsOfTypeAll<LoadSaveManager>().FirstOrDefault();
    }

    private void RefreshCompanyDisplayNames()
    {
        if (_companyOwnershipService.LocalCompanySlot >= 0)
        {
            _companyOwnershipService.SetCompanyDisplayName(_companyOwnershipService.LocalCompanySlot, _companyNameInput);
        }

        if (_host.IsRunning)
        {
            foreach (var peer in _host.Peers)
            {
                _companyOwnershipService.SetCompanyDisplayName(peer.AssignedCompanySlot, peer.CompanyName);
            }
        }
        else
        {
            _companyOwnershipService.SetCompanyDisplayName(_latestPresence.HostAssignedCompanySlot, _latestPresence.HostCompanyName);
            foreach (var player in _latestPresence.Players)
            {
                _companyOwnershipService.SetCompanyDisplayName(player.AssignedCompanySlot, player.CompanyName);
            }
        }

        _companyOwnershipService.ApplyDisplayNamesToGame();
    }

    private bool IsTradeSyncActive()
    {
        return _sharedSessionStarted || _host.IsRunning || _client.IsConnected;
    }

    private void NormalizeTradeOfferIdentity(TradeOfferSyncMessage message)
    {
        if (message.OwnerCompanySlot == _companyOwnershipService.LocalCompanySlot)
        {
            message.OwnerPlayerName = _playerNameInput;
            message.OwnerCompanyName = _companyNameInput;
            return;
        }

        var peer = _host.Peers.FirstOrDefault(x => x.AssignedCompanySlot == message.OwnerCompanySlot);
        if (peer != null)
        {
            message.OwnerPlayerName = peer.PlayerName;
            message.OwnerCompanyName = peer.CompanyName;
            return;
        }

        var player = _latestPresence.Players.FirstOrDefault(x => x.AssignedCompanySlot == message.OwnerCompanySlot);
        if (player != null)
        {
            message.OwnerPlayerName = player.PlayerName;
            message.OwnerCompanyName = player.CompanyName;
            return;
        }

        if (_latestPresence.HostAssignedCompanySlot == message.OwnerCompanySlot)
        {
            message.OwnerPlayerName = _latestPresence.HostPlayerName;
            message.OwnerCompanyName = _latestPresence.HostCompanyName;
        }
    }

    private bool ShouldDrawMultiplayerWindow()
    {
        return _host.IsRunning ||
               _client.IsConnected ||
               _sharedSessionStarted ||
               _latestPresence.Players.Count > 0;
    }

    private void DrawMultiplayerWindowContents()
    {
        var rows = BuildPlayerStatusRows();
        GUILayout.Label(GetConnectionStatusText());
        GUILayout.Label(GetSyncStatusText(rows));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Request Resync", GUILayout.Width(130f)))
        {
            RequestResync(-1, "Manual resync requested from multiplayer window.");
        }

        GUILayout.Label("Full public-state catch-up from host", GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        GUILayout.Label("Connected Players");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Player", GUILayout.Width(115f));
        GUILayout.Label("Company", GUILayout.Width(145f));
        GUILayout.Label("Money", GUILayout.Width(95f));
        GUILayout.Label("Sync", GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();

        _playersScroll = GUILayout.BeginScrollView(_playersScroll, GUILayout.Height(95f));
        foreach (var row in rows)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(row.PlayerName, GUILayout.Width(115f));
            GUILayout.Label(row.CompanyName, GUILayout.Width(145f));
            GUILayout.Label(row.Money, GUILayout.Width(95f));
            GUILayout.Label(row.SyncStatus, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        GUILayout.Space(6f);
        GUILayout.Label("Sync Diagnostics");
        foreach (var diagnostics in _companyStateSyncService.SnapshotDiagnostics.Values.OrderBy(x => x.CompanySlot).Take(5))
        {
            GUILayout.Label($"Company {diagnostics.CompanySlot}: seq {diagnostics.LastSequence}, age {FormatAge(diagnostics.AgeSeconds)}, missed {diagnostics.MissedSnapshots}, stale {diagnostics.StaleSnapshots}, checksum {diagnostics.ChecksumMismatches}, {(diagnostics.HostAuthoritative ? "host" : "local")}, {(diagnostics.NeedsResync ? "needs resync" : "caught up")}");
        }

        GUILayout.Space(6f);
        GUILayout.Label("Public Events");
        foreach (var publicEvent in GetPublicEventsSnapshot().Reverse().Take(4).Reverse())
        {
            var sent = publicEvent.SentUtcTicks > 0
                ? new DateTime(publicEvent.SentUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : "--:--";
            GUILayout.Label($"[{sent}] {publicEvent.Summary}");
        }

        GUILayout.Space(6f);
        GUILayout.Label("Chat");
        _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(130f));
        foreach (var message in GetChatMessagesSnapshot())
        {
            var sent = message.SentUtcTicks > 0
                ? new DateTime(message.SentUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : "--:--";
            var ownMessage = _host.IsRunning
                ? message.SenderSessionId == Guid.Empty
                : message.SenderSessionId == _clientSessionId;
            var senderName = ownMessage ? "You" : Truncate(message.PlayerName, 24);
            var companyName = Truncate(message.CompanyName, 28);
            GUILayout.Label($"[{sent}] {senderName} / {companyName}: {message.Text}");
        }
        GUILayout.EndScrollView();

        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("MultiplayerChatInput");
        _chatInput = GUILayout.TextField(_chatInput ?? string.Empty, GUILayout.ExpandWidth(true));
        var sendClicked = GUILayout.Button("Send", GUILayout.Width(70f));
        GUILayout.EndHorizontal();

        var enterPressed = Event.current.type == EventType.KeyDown &&
                           Event.current.keyCode == KeyCode.Return &&
                           GUI.GetNameOfFocusedControl() == "MultiplayerChatInput";
        if (sendClicked || enterPressed)
        {
            SendChatMessage();
            if (enterPressed)
            {
                Event.current.Use();
            }
        }

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }

    private List<PlayerStatusRow> BuildPlayerStatusRows()
    {
        var rows = new List<PlayerStatusRow>();
        if (_host.IsRunning)
        {
            rows.Add(CreatePlayerStatusRow(
                Guid.Empty,
                _playerNameInput,
                _companyNameInput,
                _companyOwnershipService.LocalCompanySlot,
                isLocal: true,
                ready: true));

            rows.AddRange(_host.Peers
                .OrderBy(peer => peer.AssignedCompanySlot)
                .Select(peer => CreatePlayerStatusRow(
                    peer.SessionId,
                    peer.PlayerName,
                    peer.CompanyName,
                    peer.AssignedCompanySlot,
                    isLocal: false,
                    ready: peer.IsGameReady)));

            return rows;
        }

        if (_client.IsConnected || _latestPresence.Players.Count > 0)
        {
            rows.Add(CreatePlayerStatusRow(
                Guid.Empty,
                _latestPresence.HostPlayerName,
                _latestPresence.HostCompanyName,
                _latestPresence.HostAssignedCompanySlot,
                isLocal: false,
                ready: true));

            rows.AddRange(_latestPresence.Players
                .OrderBy(player => player.AssignedCompanySlot)
                .Select(player => CreatePlayerStatusRow(
                    player.SessionId,
                    player.PlayerName,
                    player.CompanyName,
                    player.AssignedCompanySlot,
                    isLocal: player.SessionId == _clientSessionId,
                    ready: true)));
        }

        if (!rows.Any(row => row.IsLocal) && _companyOwnershipService.LocalCompanySlot >= 0)
        {
            rows.Add(CreatePlayerStatusRow(
                _clientSessionId,
                _playerNameInput,
                _companyNameInput,
                _companyOwnershipService.LocalCompanySlot,
                isLocal: true,
                ready: _gameReadySent));
        }

        return rows;
    }

    private PlayerStatusRow CreatePlayerStatusRow(Guid sessionId, string playerName, string companyName, int companySlot, bool isLocal, bool ready)
    {
        var syncStatus = GetPlayerSyncStatus(companySlot, isLocal, ready);
        return new PlayerStatusRow(
            sessionId,
            Truncate(string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim(), 18),
            Truncate(string.IsNullOrWhiteSpace(companyName) ? $"Company {companySlot}" : companyName.Trim(), 24),
            companySlot,
            GetMoneyText(companySlot, isLocal),
            syncStatus,
            isLocal);
    }

    private string GetConnectionStatusText()
    {
        if (_host.IsRunning)
        {
            return $"Hosting on port {_host.BoundPort} | Clients: {_host.Peers.Count}";
        }

        if (_client.IsConnected)
        {
            return $"Connected to {_hostPlayerName}";
        }

        return _sharedSessionStarted ? "Shared session active" : "Multiplayer inactive";
    }

    private string GetSyncStatusText(IReadOnlyCollection<PlayerStatusRow> rows)
    {
        var snapshotCount = _companyStateSyncService.LatestSnapshots.Count;
        var facilityCount = _companyStateSyncService.LatestSnapshots.Values.Sum(snapshot => snapshot.OwnedInventories.Sum(inventory => inventory.Facilities.Count));
        if (_host.IsRunning)
        {
            return $"Sync: {snapshotCount} company state(s), {facilityCount} completed facility type(s) | Ready: {_host.Peers.Count(peer => peer.IsGameReady)} / {_host.Peers.Count}";
        }

        return $"Sync: {snapshotCount} company state(s), {facilityCount} completed facility type(s) | Players: {rows.Count}";
    }

    private string GetPlayerSyncStatus(int companySlot, bool isLocal, bool ready)
    {
        if (isLocal)
        {
            return _sharedSessionStarted ? "Local" : "Lobby";
        }

        if (!_sharedSessionStarted)
        {
            return "Lobby";
        }

        if (_companyStateSyncService.LatestSnapshots.ContainsKey(companySlot))
        {
            return "Synced";
        }

        return ready ? "Waiting for state" : "Loading";
    }

    private string GetMoneyText(int companySlot, bool isLocal)
    {
        if (isLocal && TryGetCurrentMoney(companySlot, out var localMoney))
        {
            return FormatMoney(localMoney);
        }

        if (_companyStateSyncService.LatestSnapshots.TryGetValue(companySlot, out var snapshot))
        {
            return FormatMoney(snapshot.Money);
        }

        return "Waiting";
    }

    private bool TryGetCurrentMoney(int companySlot, out double money)
    {
        money = 0;
        if (!_companyOwnershipService.TryGetCompanyBySlot(companySlot, out var company) || company?.MoneyController == null)
        {
            return false;
        }

        money = company.MoneyController.CurrentMoney;
        return true;
    }

    private static string FormatMoney(double money)
    {
        if (double.IsNaN(money) || double.IsInfinity(money))
        {
            return "$0";
        }

        return "$" + money.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatAge(double seconds)
    {
        if (double.IsInfinity(seconds) || double.IsNaN(seconds))
        {
            return "never";
        }

        return seconds < 60
            ? $"{seconds:0.0}s"
            : $"{seconds / 60:0.0}m";
    }

    private ChatMessage[] GetChatMessagesSnapshot()
    {
        lock (_chatLock)
        {
            return _chatMessages.ToArray();
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        if (maxLength <= 3)
        {
            return value.Substring(0, maxLength);
        }

        return value.Substring(0, maxLength - 3) + "...";
    }

    private void DrawWindowContents()
    {
        GUILayout.Label("Session Status");
        GUILayout.Label(_status);
        GUILayout.Space(6f);

        GUILayout.Label("Player Name");
        _playerNameInput = GUILayout.TextField(_playerNameInput ?? string.Empty);

        GUILayout.Label("Company Name");
        _companyNameInput = GUILayout.TextField(_companyNameInput ?? string.Empty);

        GUILayout.Label("Host Address");
        _addressInput = GUILayout.TextField(_addressInput ?? string.Empty);

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        GUILayout.Label("Port");
        _portInput = GUILayout.TextField(_portInput ?? string.Empty);
        GUILayout.EndVertical();

        GUILayout.BeginVertical();
        GUILayout.Label("Requested Company Slot");
        _companySlotInput = GUILayout.TextField(_companySlotInput ?? string.Empty);
        GUILayout.EndVertical();

        GUILayout.BeginVertical();
        GUILayout.Label("Allowed Companies");
        _maxCompaniesInput = GUILayout.TextField(_maxCompaniesInput ?? string.Empty);
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Host Session"))
        {
            StartHost();
        }

        if (GUILayout.Button("Join Session"))
        {
            StartClient();
        }

        if (GUILayout.Button("Disconnect"))
        {
            Disconnect();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("Known Companies");
        foreach (var company in _companyOwnershipService.GetKnownCompanies().Take(8))
        {
            var marker = company.Slot == _companyOwnershipService.LocalCompanySlot ? "(LOCAL)" : string.Empty;
            GUILayout.Label($"[{company.Slot}] {company.DisplayName} {marker}");
        }

        GUILayout.Space(8f);
        GUILayout.Label($"Connected Clients: {_host.Peers.Count}");
        if (_host.IsRunning)
        {
            GUILayout.Label($"Ready Clients: {_host.Peers.Count(peer => peer.IsGameReady)} / {_host.Peers.Count}");
        }

        GUILayout.Label($"Storyline Disabled: {IsStorylineDisabled}");
        GUILayout.Label("Build Ownership: Per-player company only");

        GUILayout.Space(8f);
        GUILayout.Label("Company Snapshots");
        foreach (var snapshot in _companyStateSyncService.LatestSnapshots.Values.OrderBy(x => x.CompanySlot).Take(8))
        {
            GUILayout.Label($"[{snapshot.CompanySlot}] {snapshot.CompanyName} / {snapshot.OwnerPlayerName}");
            GUILayout.Label($"Money: {snapshot.Money:0.##} | Profit: {snapshot.TotalProfit:0.##} | Research Done: {snapshot.CompletedResearchCount}");
            GUILayout.Label($"Inventories: {snapshot.OwnedInventories.Count}");
            var facilityCount = snapshot.OwnedInventories.Sum(x => x.Facilities.Count);
            GUILayout.Label($"Completed Facilities: {facilityCount}");
            if (snapshot.ActiveResearch.Count > 0)
            {
                var progress = string.Join(", ", snapshot.ActiveResearch.Take(3).Select(x => $"{x.ResearchId} {(x.Progress * 100f):0}%"));
                GUILayout.Label($"Active: {progress}");
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}

internal sealed class PlayerStatusRow
{
    public PlayerStatusRow(Guid sessionId, string playerName, string companyName, int companySlot, string money, string syncStatus, bool isLocal)
    {
        SessionId = sessionId;
        PlayerName = playerName;
        CompanyName = companyName;
        CompanySlot = companySlot;
        Money = money;
        SyncStatus = syncStatus;
        IsLocal = isLocal;
    }

    public Guid SessionId { get; }
    public string PlayerName { get; }
    public string CompanyName { get; }
    public int CompanySlot { get; }
    public string Money { get; }
    public string SyncStatus { get; }
    public bool IsLocal { get; }
}
