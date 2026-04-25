using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace SolarExpanse.Multiplayer.Patches;

[HarmonyPatch(typeof(global::Game.ObjectInfoDataScripts.ProductionItem))]
internal static class ProductionItemActionPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("StartBuilding")]
    private static void StartBuildingPostfix(global::Game.ObjectInfoDataScripts.ProductionItem __instance)
    {
        RecordProductionAction(__instance, "StartProduction");
    }

    [HarmonyPostfix]
    [HarmonyPatch("CancelBuild")]
    private static void CancelBuildPostfix(global::Game.ObjectInfoDataScripts.ProductionItem __instance)
    {
        RecordProductionAction(__instance, "CancelProduction");
    }

    [HarmonyPostfix]
    [HarmonyPatch("Build")]
    private static void BuildPostfix(global::Game.ObjectInfoDataScripts.ProductionItem __instance)
    {
        RecordProductionAction(__instance, "CompleteProduction");
    }

    private static void RecordProductionAction(global::Game.ObjectInfoDataScripts.ProductionItem productionItem, string actionType)
    {
        try
        {
            var arguments = new Dictionary<string, string>
            {
                ["ProductionItemId"] = productionItem.ID.ToString(),
                ["ProductionItemTypeId"] = productionItem.ProductionItemType?.ID ?? string.Empty,
                ["BuildProgress"] = productionItem.BuildProgress.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };

            SolarExpanseMultiplayerPlugin.Runtime?.RecordPrivateCompanyActionForCompany(productionItem.Company, actionType, arguments);
        }
        catch (System.Exception ex)
        {
            SolarExpanseMultiplayerPlugin.Log?.LogWarning($"Failed to record production action '{actionType}': {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(global::Manager.ResearchManager))]
internal static class ResearchActionPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("StartResearch", new[] { typeof(global::ScriptableObjectScripts.ResearchDefinition), typeof(bool), typeof(bool) })]
    private static void StartResearchPostfix(global::Manager.ResearchManager __instance, global::ScriptableObjectScripts.ResearchDefinition __0)
    {
        RecordResearchAction(__instance, "StartResearch", __0);
    }

    [HarmonyPostfix]
    [HarmonyPatch("StopResearch")]
    private static void StopResearchPostfix(global::Manager.ResearchManager __instance)
    {
        RecordResearchAction(__instance, "StopResearch");
    }

    [HarmonyPostfix]
    [HarmonyPatch("ResearchComplete", new[] { typeof(global::ScriptableObjectScripts.ResearchDefinition), typeof(global::Game.Company) })]
    private static void ResearchCompletePostfix(global::Manager.ResearchManager __instance, global::ScriptableObjectScripts.ResearchDefinition __0)
    {
        RecordResearchAction(__instance, "CompleteResearch", __0);
    }

    private static void RecordResearchAction(global::Manager.ResearchManager researchManager, string actionType, global::ScriptableObjectScripts.ResearchDefinition? targetResearch = null)
    {
        try
        {
            var company = AccessTools.Field(typeof(global::Manager.ResearchManager), "company")?.GetValue(researchManager) as global::Game.Company;
            var data = researchManager.GetDataToSave();
            var activeIds = new List<string>();
            AddActiveResearchId(activeIds, data?.slot1);
            AddActiveResearchId(activeIds, data?.slot2);
            AddActiveResearchId(activeIds, data?.slot3);

            if (data?.startNotFinish != null)
            {
                foreach (var item in data.startNotFinish)
                {
                    AddActiveResearchId(activeIds, item);
                }
            }

            var completedIds = data?.completeResearch?
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.id))
                .Select(x => x.id)
                .Distinct()
                .OrderBy(x => x)
                .ToArray() ?? new string[0];

            var arguments = new Dictionary<string, string>
            {
                ["ResearchId"] = targetResearch?.ID ?? string.Empty,
                ["ActiveResearchIds"] = string.Join(",", activeIds.Distinct().OrderBy(x => x)),
                ["CompletedResearchIds"] = string.Join(",", completedIds)
            };

            SolarExpanseMultiplayerPlugin.Runtime?.RecordPrivateCompanyActionForCompany(company, actionType, arguments);
        }
        catch (System.Exception ex)
        {
            SolarExpanseMultiplayerPlugin.Log?.LogWarning($"Failed to record research action '{actionType}': {ex.Message}");
        }
    }

    private static void AddActiveResearchId(List<string> activeIds, global::ScriptableObjectScripts.ResearchDataProgress? progress)
    {
        var id = progress?.ResearchDefinition?.ID;
        if (!string.IsNullOrWhiteSpace(id))
        {
            activeIds.Add(id!);
        }
    }
}
