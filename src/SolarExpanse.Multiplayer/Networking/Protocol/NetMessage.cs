namespace SolarExpanse.Multiplayer.Networking.Protocol;

public abstract class NetMessage
{
    public string MessageType { get; set; } = string.Empty;
}