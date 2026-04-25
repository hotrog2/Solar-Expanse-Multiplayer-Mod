using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Manager;
using SolarExpanse.Multiplayer.Configuration;
using SolarExpanse.Multiplayer.Game.Company;
using SolarExpanse.Multiplayer.Game.Time;
using SolarExpanse.Multiplayer.Networking.Client;
using SolarExpanse.Multiplayer.Networking.Host;
using SolarExpanse.Multiplayer.Networking.Protocol;
using UnityEngine;

namespace SolarExpanse.Multiplayer.Runtime;

public sealed class MultiplayerRuntime : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly MultiplayerConfig _config;
    private readonly DirectConnectHost _host;
    private readonly DirectConnectClient _client;
    private readonly TimeSyncService _timeSyncService;
    private readonly CompanyOwnershipService _companyOwnershipService;
    private readonly CompanyStateSyncService _companyStateSyncService;
    private readonly ConcurrentQueue<float> _pendingTimeScaleRequests = new ConcurrentQueue<float>();
    private readonly object _startGameLock = new object();
    private PlayerPresenceMessage _latestPresence = new PlayerPresenceMessage();
    private StartGameMessage? _pendingStartGame;
    private Guid _activeStartSessionId = Guid.Empty;
    private bool _sharedSessionStarted;
    private bool _gameReadySent;

    private readonly Rect _windowRect = new Rect(20f, 20f, 430f, 360f);

    private string _status = "Idle";
    private string _addressInput;
    private string _portInput;
    private string _playerNameInput;
    private string _companyNameInput;
    private string _companySlotInput;
    private string _maxCompaniesInput;
    private string _startingCorporationInput = "Solex";
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
    public bool IsHosting => _host.IsRunning;
    public bool IsClientConnected => _client.IsConnected;
    public Guid ClientSessionId => _clientSessionId;
    public string HostPlayerName => _hostPlayerName;
    public IReadOnlyCollection<HostPeerState> ConnectedPeers => _host.Peers;
    public IReadOnlyCollection<PlayerPresenceDto> VisiblePlayers => _latestPresence.Players;
    public PlayerPresenceMessage LatestPresence => _latestPresence;

    public bool IsStorylineDisabled => _config.DisableStorylineSystems.Value;

    public MultiplayerRuntime(ManualLogSource log, MultiplayerConfig config)
    {
        _log = log;
        _config = config;
        _host = new DirectConnectHost(log);
        _client = new DirectConnectClient(log);
        _timeSyncService = new TimeSyncService();
        _companyOwnershipService = new CompanyOwnershipService();
        _companyStateSyncService = new CompanyStateSyncService(_companyOwnershipService);

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
        _client.Joined += OnJoined;
        _client.TimeSnapshotReceived += snapshot => _timeSyncService.QueueSnapshot(snapshot);
        _client.CompanyStateReceived += snapshot => _companyStateSyncService.HandleIncomingSnapshot(snapshot);
        _client.CompanyActionResultReceived += OnCompanyActionResultReceived;
        _client.PresenceUpdated += presence =>
        {
            _latestPresence = presence;
            _hostPlayerName = presence.HostPlayerName;
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

        if (_host.IsRunning)
        {
            _companyStateSyncService.TickHost(_host, _companyOwnershipService.LocalCompanySlot, _playerNameInput);
        }
        else if (_client.IsConnected)
        {
            _companyStateSyncService.TickClient(_client, _companyOwnershipService.LocalCompanySlot, _playerNameInput);
        }

        _companyStateSyncService.ApplyRemoteSnapshots(_companyOwnershipService.LocalCompanySlot);
        TrySendGameReady();
    }

    public void DrawGui()
    {
        if (!_config.ShowDebugWindow.Value)
        {
            return;
        }

        GUILayout.Window(17291, _windowRect, _ => DrawWindowContents(), "Solar Expanse Multiplayer");
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

        _host.BroadcastStartGame();
        _activeStartSessionId = _host.CurrentStartSessionId;
        _sharedSessionStarted = true;
        _gameReadySent = false;
        _status = "Starting shared session.";
        return true;
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
        _companySlotInput = message.AssignedCompanySlot.ToString();
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

        var localPlayer = message.Players.FirstOrDefault(player => player.SessionId == _clientSessionId);
        if (localPlayer != null)
        {
            _companyNameInput = localPlayer.CompanyName;
            _companySlotInput = localPlayer.AssignedCompanySlot.ToString();
            _startingCorporationInput = localPlayer.StartingCorporation;
            _companyOwnershipService.SetLocalCompanySlot(localPlayer.AssignedCompanySlot);
        }

        _maxCompaniesInput = message.MaxCompanySlots.ToString();
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

    private void SendKnownSnapshotsToAllPeers()
    {
        foreach (var peer in _host.Peers)
        {
            SendKnownSnapshotsToPeer(peer);
        }
    }

    private void SendKnownSnapshotsToPeer(HostPeerState peer)
    {
        var localSnapshot = _companyStateSyncService.CaptureForcedLocalSnapshot(_companyOwnershipService.LocalCompanySlot, _playerNameInput);
        if (localSnapshot != null)
        {
            _companyStateSyncService.HandleIncomingSnapshot(localSnapshot);
            _host.Send(peer, CompanyStateSyncService.CreatePublicSnapshot(localSnapshot));
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

            _host.Send(peer, CompanyStateSyncService.CreatePublicSnapshot(snapshot));
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

        var normalized = new CompanyStateSnapshotMessage
        {
            MessageType = nameof(CompanyStateSnapshotMessage),
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

        var publicSnapshot = CompanyStateSyncService.CreatePublicSnapshot(normalized);
        _companyStateSyncService.HandleIncomingSnapshot(publicSnapshot);
        _host.BroadcastExcept(peer.SessionId, publicSnapshot);
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
