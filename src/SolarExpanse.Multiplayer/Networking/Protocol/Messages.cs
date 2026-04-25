using System;
using System.Collections.Generic;

namespace SolarExpanse.Multiplayer.Networking.Protocol;

public sealed class JoinRequestMessage : NetMessage
{
    public string PlayerName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public int RequestedCompanySlot { get; set; }
    public string ModVersion { get; set; } = string.Empty;
}

public sealed class JoinAcceptedMessage : NetMessage
{
    public Guid SessionId { get; set; }
    public int AssignedCompanySlot { get; set; }
    public long HostGameTimeTicks { get; set; }
    public float HostTimeScale { get; set; }
    public string HostPlayerName { get; set; } = string.Empty;
}

public sealed class TimeSnapshotMessage : NetMessage
{
    public int Sequence { get; set; }
    public long GameTimeTicks { get; set; }
    public float TimeScale { get; set; }
}

public sealed class TimeScaleRequestMessage : NetMessage
{
    public float RequestedTimeScale { get; set; }
}

public sealed class PlayerPresenceMessage : NetMessage
{
    public string HostPlayerName { get; set; } = string.Empty;
    public string HostCompanyName { get; set; } = string.Empty;
    public string HostStartingCorporation { get; set; } = string.Empty;
    public int HostAssignedCompanySlot { get; set; }
    public List<PlayerPresenceDto> Players { get; set; } = new List<PlayerPresenceDto>();
}

public sealed class LobbySettingsRequestMessage : NetMessage
{
    public string CompanyName { get; set; } = string.Empty;
    public string StartingCorporation { get; set; } = string.Empty;
    public int RequestedCompanySlot { get; set; }
}

public sealed class StartGameMessage : NetMessage
{
    public Guid SessionId { get; set; }
    public int MaxCompanySlots { get; set; }
    public string HostPlayerName { get; set; } = string.Empty;
    public string HostCompanyName { get; set; } = string.Empty;
    public string HostStartingCorporation { get; set; } = string.Empty;
    public int HostAssignedCompanySlot { get; set; }
    public List<PlayerPresenceDto> Players { get; set; } = new List<PlayerPresenceDto>();
}

public sealed class GameReadyMessage : NetMessage
{
    public Guid StartSessionId { get; set; }
    public Guid ClientSessionId { get; set; }
    public int CompanySlot { get; set; }
    public string PlayerName { get; set; } = string.Empty;
}

public sealed class CompanyActionCommandMessage : NetMessage
{
    public Guid CommandId { get; set; }
    public Guid StartSessionId { get; set; }
    public int CompanySlot { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new Dictionary<string, string>();
}

public sealed class CompanyActionResultMessage : NetMessage
{
    public Guid CommandId { get; set; }
    public int CompanySlot { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class ChatMessage : NetMessage
{
    public Guid MessageId { get; set; }
    public Guid SenderSessionId { get; set; }
    public int CompanySlot { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public long SentUtcTicks { get; set; }
}

public sealed class PlayerPresenceDto
{
    public Guid SessionId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string StartingCorporation { get; set; } = string.Empty;
    public int AssignedCompanySlot { get; set; }
}

public sealed class CompanyStateSnapshotMessage : NetMessage
{
    public int CompanySlot { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string OwnerPlayerName { get; set; } = string.Empty;
    public double Money { get; set; }
    public double TotalProfit { get; set; }
    public int DiscoveredSystemsCount { get; set; }
    public int CompletedResearchCount { get; set; }
    public List<string> CompletedResearchIds { get; set; } = new List<string>();
    public List<ResearchProgressDto> ActiveResearch { get; set; } = new List<ResearchProgressDto>();
    public List<ObjectInventorySnapshotDto> OwnedInventories { get; set; } = new List<ObjectInventorySnapshotDto>();
}

public sealed class ResearchProgressDto
{
    public string ResearchId { get; set; } = string.Empty;
    public float Progress { get; set; }
    public bool Complete { get; set; }
}

public sealed class ObjectInventorySnapshotDto
{
    public int ObjectId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public long ConstructionEquipmentCount { get; set; }
    public List<ResourceStackDto> Resources { get; set; } = new List<ResourceStackDto>();
    public List<FacilitySnapshotDto> Facilities { get; set; } = new List<FacilitySnapshotDto>();
}

public sealed class ResourceStackDto
{
    public string ResourceId { get; set; } = string.Empty;
    public double Value { get; set; }
    public int ResourceState { get; set; }
    public bool ForcePrimary { get; set; }
    public float? MiningFactor { get; set; }
}

public sealed class FacilitySnapshotDto
{
    public string DescriptorId { get; set; } = string.Empty;
    public long Quantity { get; set; }
    public long Enabled { get; set; }
    public long HaveWorkers { get; set; }
    public bool ValidCanAddFacility { get; set; }
}

