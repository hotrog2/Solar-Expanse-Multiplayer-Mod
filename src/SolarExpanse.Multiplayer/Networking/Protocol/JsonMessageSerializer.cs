using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SolarExpanse.Multiplayer.Networking.Protocol;

public static class JsonMessageSerializer
{
    public static string Serialize(NetMessage message)
    {
        return JsonConvert.SerializeObject(message);
    }

    public static NetMessage Deserialize(string payload)
    {
        var obj = JObject.Parse(payload);
        var type = obj.Value<string>(nameof(NetMessage.MessageType));

        return type switch
        {
            nameof(JoinRequestMessage) => obj.ToObject<JoinRequestMessage>() ?? throw new InvalidOperationException("Invalid JoinRequestMessage payload."),
            nameof(JoinAcceptedMessage) => obj.ToObject<JoinAcceptedMessage>() ?? throw new InvalidOperationException("Invalid JoinAcceptedMessage payload."),
            nameof(TimeSnapshotMessage) => obj.ToObject<TimeSnapshotMessage>() ?? throw new InvalidOperationException("Invalid TimeSnapshotMessage payload."),
            nameof(TimeScaleRequestMessage) => obj.ToObject<TimeScaleRequestMessage>() ?? throw new InvalidOperationException("Invalid TimeScaleRequestMessage payload."),
            nameof(PlayerPresenceMessage) => obj.ToObject<PlayerPresenceMessage>() ?? throw new InvalidOperationException("Invalid PlayerPresenceMessage payload."),
            nameof(LobbySettingsRequestMessage) => obj.ToObject<LobbySettingsRequestMessage>() ?? throw new InvalidOperationException("Invalid LobbySettingsRequestMessage payload."),
            nameof(StartGameMessage) => obj.ToObject<StartGameMessage>() ?? throw new InvalidOperationException("Invalid StartGameMessage payload."),
            nameof(GameReadyMessage) => obj.ToObject<GameReadyMessage>() ?? throw new InvalidOperationException("Invalid GameReadyMessage payload."),
            nameof(CompanyActionCommandMessage) => obj.ToObject<CompanyActionCommandMessage>() ?? throw new InvalidOperationException("Invalid CompanyActionCommandMessage payload."),
            nameof(CompanyActionResultMessage) => obj.ToObject<CompanyActionResultMessage>() ?? throw new InvalidOperationException("Invalid CompanyActionResultMessage payload."),
            nameof(ChatMessage) => obj.ToObject<ChatMessage>() ?? throw new InvalidOperationException("Invalid ChatMessage payload."),
            nameof(CompanyStateSnapshotMessage) => obj.ToObject<CompanyStateSnapshotMessage>() ?? throw new InvalidOperationException("Invalid CompanyStateSnapshotMessage payload."),
            _ => throw new InvalidOperationException($"Unknown message type '{type}'.")
        };
    }
}
