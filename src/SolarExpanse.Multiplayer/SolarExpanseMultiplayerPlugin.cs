using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SolarExpanse.Multiplayer.Configuration;
using SolarExpanse.Multiplayer.Patches;
using SolarExpanse.Multiplayer.Runtime;

namespace SolarExpanse.Multiplayer;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class SolarExpanseMultiplayerPlugin : BaseUnityPlugin
{
    private Harmony? _harmony;

    internal static MultiplayerRuntime? Runtime { get; private set; }
    internal static ManualLogSource? Log { get; private set; }

    private void Awake()
    {
        Log = Logger;
        Runtime = new MultiplayerRuntime(Logger, MultiplayerConfig.Bind(Config));
        Runtime.Initialize();

        _harmony = new Harmony(PluginInfo.Guid);
        _harmony.PatchAll(typeof(SolarExpanseMultiplayerPlugin).Assembly);
        LogMenuPatchState();

        Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded.");
    }

    private void LogMenuPatchState()
    {
        var awakeInfo = Harmony.GetPatchInfo(AccessTools.Method(typeof(global::MenuSceneUI), "Awake"));
        var clickInfo = Harmony.GetPatchInfo(AccessTools.Method(typeof(global::MenuSceneUI), "OnClickBtnMult"));
        Logger.LogInfo($"MenuSceneUI.Awake postfix patches: {awakeInfo?.Postfixes.Count ?? 0}; OnClickBtnMult prefix patches: {clickInfo?.Prefixes.Count ?? 0}.");
    }

    private void Start()
    {
        Logger.LogInfo("Solar Expanse Multiplayer scene installer started.");
        MainMenuMultiplayerPatch.InstallOnActiveMenu();
    }

    private void Update()
    {
        try
        {
            Runtime?.Update();
        }
        catch (System.Exception ex)
        {
            Logger.LogWarning($"Multiplayer runtime update failed: {ex}");
        }

        try
        {
            MainMenuMultiplayerPatch.InstallOnActiveMenu();
        }
        catch (System.Exception ex)
        {
            Logger.LogWarning($"Main-menu multiplayer installer failed: {ex}");
        }
    }

    private void LateUpdate()
    {
        MainMenuMultiplayerPatch.InstallOnActiveMenu();
    }

    private void OnGUI()
    {
        try
        {
            Runtime?.DrawGui();
        }
        catch (System.Exception ex)
        {
            Logger.LogWarning($"Multiplayer GUI draw failed: {ex}");
        }
    }

    private void OnDestroy()
    {
        Logger.LogInfo("Solar Expanse Multiplayer plugin object was destroyed; keeping static runtime available for menu integration.");
    }
}
