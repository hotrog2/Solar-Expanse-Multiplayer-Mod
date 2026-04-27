using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using SolarExpanse.Multiplayer.Networking.Protocol;

namespace SolarExpanse.Multiplayer.Networking.Host;

public sealed class DirectConnectHost : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly ConcurrentDictionary<Guid, HostPeerState> _peers = new ConcurrentDictionary<Guid, HostPeerState>();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private string _hostPlayerName = "Host";
    private string _hostCompanyName = "New Company";
    private string _hostStartingCorporation = "Solex";
    private int _hostCompanySlot;
    private int _maxCompanySlots = 4;
    private Guid _currentStartSessionId = Guid.Empty;

    public event Action<HostPeerState, TimeScaleRequestMessage>? TimeScaleRequested;
    public event Action<HostPeerState, CompanyStateSnapshotMessage>? CompanyStateSnapshotReceived;
    public event Action<HostPeerState, GameReadyMessage>? GameReadyReceived;
    public event Action<HostPeerState, CompanyActionCommandMessage>? CompanyActionCommandReceived;
    public event Action<HostPeerState, ChatMessage>? ChatMessageReceived;
    public event Action<HostPeerState, SyncResyncRequestMessage>? ResyncRequested;

    public bool IsRunning => _listener != null;
    public int BoundPort { get; private set; }
    public Guid CurrentStartSessionId => _currentStartSessionId;

    public DirectConnectHost(ManualLogSource log)
    {
        _log = log;
    }

    public IReadOnlyCollection<HostPeerState> Peers => _peers.Values.ToArray();

    public void UpdateHostLobbySettings(string companyName, int assignedCompanySlot, string startingCorporation)
    {
        _hostCompanyName = string.IsNullOrWhiteSpace(companyName) ? _hostCompanyName : companyName.Trim();
        _hostCompanySlot = assignedCompanySlot;
        _hostStartingCorporation = string.IsNullOrWhiteSpace(startingCorporation) ? _hostStartingCorporation : startingCorporation.Trim();
        BroadcastPlayerPresence();
    }

    public bool UpdatePeerLobbySettings(Guid sessionId, string companyName, int assignedCompanySlot, string startingCorporation)
    {
        if (!_peers.TryGetValue(sessionId, out var peer))
        {
            return false;
        }

        if (!IsCompanySlotAvailable(assignedCompanySlot, sessionId))
        {
            return false;
        }

        peer.CompanyName = string.IsNullOrWhiteSpace(companyName) ? peer.CompanyName : companyName.Trim();
        peer.AssignedCompanySlot = assignedCompanySlot;
        peer.StartingCorporation = string.IsNullOrWhiteSpace(startingCorporation) ? peer.StartingCorporation : startingCorporation.Trim();
        BroadcastPlayerPresence();
        return true;
    }

    public StartGameMessage CreateStartGameMessage()
    {
        _currentStartSessionId = Guid.NewGuid();
        return new StartGameMessage
        {
            MessageType = nameof(StartGameMessage),
            SessionId = _currentStartSessionId,
            MaxCompanySlots = _maxCompanySlots,
            HostPlayerName = _hostPlayerName,
            HostCompanyName = _hostCompanyName,
            HostStartingCorporation = _hostStartingCorporation,
            HostAssignedCompanySlot = _hostCompanySlot,
            Players = _peers.Values
                .OrderBy(x => x.AssignedCompanySlot)
                .Select(x => new PlayerPresenceDto
                {
                    SessionId = x.SessionId,
                    PlayerName = x.PlayerName,
                    CompanyName = x.CompanyName,
                    StartingCorporation = x.StartingCorporation,
                    AssignedCompanySlot = x.AssignedCompanySlot
                })
                .ToList()
        };
    }

    public void BroadcastStartGame()
    {
        foreach (var peer in _peers.Values)
        {
            peer.IsGameReady = false;
            peer.LastReadyStartSessionId = Guid.Empty;
        }

        Broadcast(CreateStartGameMessage());
    }

    public void Start(int port, string hostPlayerName, string hostCompanyName, int hostCompanySlot, string hostStartingCorporation, int maxCompanySlots, Func<string, int, IReadOnlyCollection<int>, int> assignCompanySlot, Func<JoinAcceptedMessage> createJoinAccepted)
    {
        if (IsRunning)
        {
            return;
        }

        _hostPlayerName = hostPlayerName;
        _hostCompanyName = hostCompanyName;
        _hostCompanySlot = hostCompanySlot;
        _hostStartingCorporation = hostStartingCorporation;
        _maxCompanySlots = Math.Max(1, maxCompanySlots);
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(assignCompanySlot, createJoinAccepted, _cts.Token));
        _log.LogInfo($"Direct-connect host listening on port {BoundPort}.");
    }

    public void Broadcast(NetMessage message)
    {
        foreach (var peer in _peers.Values.ToArray())
        {
            Send(peer, message);
        }
    }

    public void BroadcastExcept(Guid excludedSessionId, NetMessage message)
    {
        foreach (var peer in _peers.Values.ToArray())
        {
            if (peer.SessionId == excludedSessionId)
            {
                continue;
            }

            Send(peer, message);
        }
    }

    public void Send(HostPeerState peer, NetMessage message)
    {
        try
        {
            var json = JsonMessageSerializer.Serialize(message);
            lock (peer.WriteLock)
            {
                peer.Writer.WriteLine(json);
                peer.Writer.Flush();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Failed to send {message.MessageType} to {peer.PlayerName}: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // ignored
        }

        foreach (var peer in _peers.Values.ToArray())
        {
            peer.Dispose();
        }

        _peers.Clear();
        _acceptLoopTask = null;
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(Func<string, int, IReadOnlyCollection<int>, int> assignCompanySlot, Func<JoinAcceptedMessage> createJoinAccepted, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            try
            {
                var tcpClient = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(tcpClient, assignCompanySlot, createJoinAccepted, cancellationToken), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Accept loop error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, Func<string, int, IReadOnlyCollection<int>, int> assignCompanySlot, Func<JoinAcceptedMessage> createJoinAccepted, CancellationToken cancellationToken)
    {
        HostPeerState? peer = null;

        try
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                var firstLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(firstLine))
                {
                    return;
                }

                var firstMessage = JsonMessageSerializer.Deserialize(firstLine);
                if (!(firstMessage is JoinRequestMessage joinRequest))
                {
                    _log.LogWarning("First packet from a new client was not JoinRequestMessage.");
                    return;
                }

                var sessionId = Guid.NewGuid();
                var assignedSlot = assignCompanySlot(joinRequest.PlayerName, joinRequest.RequestedCompanySlot, _peers.Values.Select(x => x.AssignedCompanySlot).ToArray());
                if (assignedSlot < 0)
                {
                    _log.LogWarning($"Rejected player '{joinRequest.PlayerName}' because all allowed company slots are in use.");
                    return;
                }

                var companyName = string.IsNullOrWhiteSpace(joinRequest.CompanyName)
                    ? $"Company {assignedSlot}"
                    : joinRequest.CompanyName.Trim();

                peer = new HostPeerState(sessionId, joinRequest.PlayerName, companyName, assignedSlot, tcpClient, reader, writer);
                _peers[sessionId] = peer;

                var accepted = createJoinAccepted();
                accepted.MessageType = nameof(JoinAcceptedMessage);
                accepted.SessionId = sessionId;
                accepted.AssignedCompanySlot = assignedSlot;
                accepted.HostPlayerName = _hostPlayerName;
                Send(peer, accepted);
                BroadcastPlayerPresence();

                _log.LogInfo($"Player '{joinRequest.PlayerName}' connected and was assigned company slot {assignedSlot}.");

                while (!cancellationToken.IsCancellationRequested && tcpClient.Connected)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        break;
                    }

                    var message = JsonMessageSerializer.Deserialize(line);
                    if (message is TimeScaleRequestMessage timeScaleRequest)
                    {
                        TimeScaleRequested?.Invoke(peer, timeScaleRequest);
                    }
                    else if (message is LobbySettingsRequestMessage lobbySettings)
                    {
                        ApplyClientLobbySettings(peer, lobbySettings);
                    }
                    else if (message is GameReadyMessage gameReady)
                    {
                        GameReadyReceived?.Invoke(peer, gameReady);
                    }
                    else if (message is CompanyActionCommandMessage actionCommand)
                    {
                        CompanyActionCommandReceived?.Invoke(peer, actionCommand);
                    }
                    else if (message is ChatMessage chatMessage)
                    {
                        ChatMessageReceived?.Invoke(peer, chatMessage);
                    }
                    else if (message is SyncResyncRequestMessage resyncRequest)
                    {
                        ResyncRequested?.Invoke(peer, resyncRequest);
                    }
                    else if (message is CompanyStateSnapshotMessage companyStateSnapshot)
                    {
                        CompanyStateSnapshotReceived?.Invoke(peer, companyStateSnapshot);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Client handler error: {ex.Message}");
        }
        finally
        {
            if (peer != null)
            {
                _peers.TryRemove(peer.SessionId, out _);
                peer.Dispose();
                BroadcastPlayerPresence();
                _log.LogInfo($"Player '{peer.PlayerName}' disconnected.");
            }
        }
    }

    private void BroadcastPlayerPresence()
    {
        var presence = new PlayerPresenceMessage
        {
            MessageType = nameof(PlayerPresenceMessage),
            HostPlayerName = _hostPlayerName,
            HostCompanyName = _hostCompanyName,
            HostStartingCorporation = _hostStartingCorporation,
            HostAssignedCompanySlot = _hostCompanySlot,
            Players = _peers.Values
                .OrderBy(x => x.AssignedCompanySlot)
                .Select(x => new PlayerPresenceDto
                {
                    SessionId = x.SessionId,
                    PlayerName = x.PlayerName,
                    CompanyName = x.CompanyName,
                    StartingCorporation = x.StartingCorporation,
                    AssignedCompanySlot = x.AssignedCompanySlot
                })
                .ToList()
        };

        Broadcast(presence);
    }

    private void ApplyClientLobbySettings(HostPeerState peer, LobbySettingsRequestMessage settings)
    {
        var requestedSlot = settings.RequestedCompanySlot;
        if (!IsCompanySlotAvailable(requestedSlot, peer.SessionId))
        {
            requestedSlot = peer.AssignedCompanySlot;
        }

        peer.CompanyName = string.IsNullOrWhiteSpace(settings.CompanyName) ? peer.CompanyName : settings.CompanyName.Trim();
        peer.AssignedCompanySlot = requestedSlot;
        peer.StartingCorporation = string.IsNullOrWhiteSpace(settings.StartingCorporation) ? peer.StartingCorporation : settings.StartingCorporation.Trim();
        BroadcastPlayerPresence();
    }

    private bool IsCompanySlotAvailable(int requestedSlot, Guid? currentPeerSessionId)
    {
        if (requestedSlot < 0 || requestedSlot >= _maxCompanySlots)
        {
            return false;
        }

        if (requestedSlot == _hostCompanySlot)
        {
            return false;
        }

        return _peers.Values.All(peer => peer.SessionId == currentPeerSessionId || peer.AssignedCompanySlot != requestedSlot);
    }

    public void Dispose()
    {
        Stop();
    }
}

public sealed class HostPeerState : IDisposable
{
    public HostPeerState(Guid sessionId, string playerName, string companyName, int assignedCompanySlot, TcpClient client, StreamReader reader, StreamWriter writer)
    {
        SessionId = sessionId;
        PlayerName = playerName;
        CompanyName = companyName;
        StartingCorporation = "SoleX";
        AssignedCompanySlot = assignedCompanySlot;
        Client = client;
        Reader = reader;
        Writer = writer;
    }

    public Guid SessionId { get; }
    public string PlayerName { get; }
    public string CompanyName { get; set; }
    public string StartingCorporation { get; set; }
    public int AssignedCompanySlot { get; set; }
    public bool IsGameReady { get; set; }
    public Guid LastReadyStartSessionId { get; set; }
    public TcpClient Client { get; }
    public StreamReader Reader { get; }
    public StreamWriter Writer { get; }
    public object WriteLock { get; } = new object();

    public void Dispose()
    {
        try
        {
            Client.Dispose();
        }
        catch
        {
            // ignored
        }
    }
}
