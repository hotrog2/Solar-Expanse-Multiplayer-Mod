using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using SolarExpanse.Multiplayer.Networking.Protocol;

namespace SolarExpanse.Multiplayer.Networking.Client;

public sealed class DirectConnectClient : IDisposable
{
    private readonly ManualLogSource _log;

    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;

    public event Action<JoinAcceptedMessage>? Joined;
    public event Action<TimeSnapshotMessage>? TimeSnapshotReceived;
    public event Action<PlayerPresenceMessage>? PresenceUpdated;
    public event Action<StartGameMessage>? StartGameReceived;
    public event Action<CompanyActionResultMessage>? CompanyActionResultReceived;
    public event Action<CompanyStateSnapshotMessage>? CompanyStateReceived;
    public event Action? Disconnected;

    public bool IsConnected => _client?.Connected == true;

    public DirectConnectClient(ManualLogSource log)
    {
        _log = log;
    }

    public async Task ConnectAsync(string hostAddress, int port, string playerName, string companyName, int requestedCompanySlot, string modVersion)
    {
        if (IsConnected)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _client = new TcpClient();
        await _client.ConnectAsync(hostAddress, port).ConfigureAwait(false);

        var stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        Send(new JoinRequestMessage
        {
            MessageType = nameof(JoinRequestMessage),
            PlayerName = playerName,
            CompanyName = companyName,
            RequestedCompanySlot = requestedCompanySlot,
            ModVersion = modVersion
        });

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    public void Send(NetMessage message)
    {
        if (_writer == null)
        {
            return;
        }

        try
        {
            var json = JsonMessageSerializer.Serialize(message);
            lock (_writer)
            {
                _writer.WriteLine(json);
                _writer.Flush();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Failed to send {message.MessageType}: {ex.Message}");
        }
    }

    public void SendLobbySettings(string companyName, int requestedCompanySlot, string startingCorporation)
    {
        Send(new LobbySettingsRequestMessage
        {
            MessageType = nameof(LobbySettingsRequestMessage),
            CompanyName = companyName,
            RequestedCompanySlot = requestedCompanySlot,
            StartingCorporation = startingCorporation
        });
    }

    public void Disconnect()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // ignored
        }

        _reader = null;
        _writer = null;
        _client = null;
        _cts?.Dispose();
        _cts = null;
        Disconnected?.Invoke();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    break;
                }

                var message = JsonMessageSerializer.Deserialize(line);
                switch (message)
                {
                    case JoinAcceptedMessage joinAccepted:
                        Joined?.Invoke(joinAccepted);
                        break;
                    case TimeSnapshotMessage timeSnapshot:
                        TimeSnapshotReceived?.Invoke(timeSnapshot);
                        break;
                    case PlayerPresenceMessage presence:
                        PresenceUpdated?.Invoke(presence);
                        break;
                    case StartGameMessage startGame:
                        StartGameReceived?.Invoke(startGame);
                        break;
                    case CompanyActionResultMessage actionResult:
                        CompanyActionResultReceived?.Invoke(actionResult);
                        break;
                    case CompanyStateSnapshotMessage companyState:
                        CompanyStateReceived?.Invoke(companyState);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Client receive loop ended: {ex.Message}");
        }
        finally
        {
            Disconnect();
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
