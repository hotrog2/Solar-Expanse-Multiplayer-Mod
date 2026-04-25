using HarmonyLib;
using Manager;

namespace SolarExpanse.Multiplayer.Patches;

[HarmonyPatch(typeof(TimeController))]
internal static class TimeControllerPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("Update")]
    private static void UpdatePostfix(TimeController __instance)
    {
        SolarExpanseMultiplayerPlugin.Runtime?.OnTimeControllerUpdated(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TimeController.SetTimescale))]
    private static bool SetTimescalePrefix(TimeController __instance, ref float timescale)
    {
        return SolarExpanseMultiplayerPlugin.Runtime?.OnTimeScaleChangeRequested(__instance, ref timescale) ?? true;
    }
}
