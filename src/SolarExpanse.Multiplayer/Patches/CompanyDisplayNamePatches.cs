using HarmonyLib;

namespace SolarExpanse.Multiplayer.Patches;

[HarmonyPatch(typeof(global::Game.Company))]
internal static class CompanyDisplayNamePatches
{
    [HarmonyPrefix]
    [HarmonyPatch("GetTranslationName")]
    private static bool GetTranslationNamePrefix(global::Game.Company __instance, ref string __result)
    {
        if (SolarExpanseMultiplayerPlugin.Runtime?.TryGetCompanyDisplayName(__instance, out var displayName) == true &&
            !string.IsNullOrWhiteSpace(displayName))
        {
            __result = displayName;
            return false;
        }

        return true;
    }
}
