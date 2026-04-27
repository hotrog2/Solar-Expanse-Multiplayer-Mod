using BepInEx.Configuration;

namespace SolarExpanse.Multiplayer.Configuration;

public sealed class MultiplayerConfig
{
    public ConfigEntry<MultiplayerMode> Mode { get; }
    public ConfigEntry<string> PlayerName { get; }
    public ConfigEntry<string> CompanyName { get; }
    public ConfigEntry<string> HostAddress { get; }
    public ConfigEntry<int> HostPort { get; }
    public ConfigEntry<int> RequestedCompanySlot { get; }
    public ConfigEntry<int> MaxCompanies { get; }
    public ConfigEntry<bool> DisableStorylineSystems { get; }
    public ConfigEntry<bool> AutoStart { get; }
    public ConfigEntry<bool> ShowDebugWindow { get; }
    public ConfigEntry<bool> ShowInGameOverlay { get; }

    private MultiplayerConfig(
        ConfigEntry<MultiplayerMode> mode,
        ConfigEntry<string> playerName,
        ConfigEntry<string> companyName,
        ConfigEntry<string> hostAddress,
        ConfigEntry<int> hostPort,
        ConfigEntry<int> requestedCompanySlot,
        ConfigEntry<int> maxCompanies,
        ConfigEntry<bool> disableStorylineSystems,
        ConfigEntry<bool> autoStart,
        ConfigEntry<bool> showDebugWindow,
        ConfigEntry<bool> showInGameOverlay)
    {
        Mode = mode;
        PlayerName = playerName;
        CompanyName = companyName;
        HostAddress = hostAddress;
        HostPort = hostPort;
        RequestedCompanySlot = requestedCompanySlot;
        MaxCompanies = maxCompanies;
        DisableStorylineSystems = disableStorylineSystems;
        AutoStart = autoStart;
        ShowDebugWindow = showDebugWindow;
        ShowInGameOverlay = showInGameOverlay;
    }

    public static MultiplayerConfig Bind(ConfigFile config)
    {
        return new MultiplayerConfig(
            config.Bind("General", "Mode", MultiplayerMode.Disabled, "Disabled, Host, or Client."),
            config.Bind("General", "PlayerName", "Player", "Name shown to other players."),
            config.Bind("General", "CompanyName", "New Company", "Company name shown in multiplayer lobbies."),
            config.Bind("Connection", "HostAddress", "127.0.0.1", "Direct-connect target IP or hostname."),
            config.Bind("Connection", "HostPort", 27777, "TCP port used for the multiplayer session."),
            config.Bind("Gameplay", "RequestedCompanySlot", 0, "Preferred company slot when joining."),
            config.Bind("Gameplay", "MaxCompanies", 4, "Maximum number of company slots allowed in hosted multiplayer sessions."),
            config.Bind("Gameplay", "DisableStorylineSystems", true, "Disable contracts, tutorials, and default storyline-oriented mission prompts for multiplayer sandbox play."),
            config.Bind("General", "AutoStart", false, "Automatically start host/client mode on load."),
            config.Bind("Debug", "ShowDebugWindow", false, "Show the legacy IMGUI debug/control window."),
            config.Bind("UI", "ShowInGameOverlay", true, "Show the in-game multiplayer status/chat overlay and F8 toggle button."));
    }
}
