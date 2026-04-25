using HarmonyLib;
using Manager;
using System.Collections.Generic;
using UnityEngine;

namespace SolarExpanse.Multiplayer.Patches;

internal static class StorylineSuppressionState
{
    public static bool Enabled => SolarExpanseMultiplayerPlugin.Runtime?.IsStorylineDisabled ?? false;
}

[HarmonyPatch(typeof(ContractManager))]
internal static class ContractManagerStorylinePatches
{
    [HarmonyPrefix]
    [HarmonyPatch("Start")]
    private static bool StartPrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ONEachDayChange")]
    private static bool OnEachDayChangePrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("UpdateStateChangePublic")]
    private static bool UpdateStateChangePublicPrefix(ContractManager __instance)
    {
        if (!StorylineSuppressionState.Enabled)
        {
            return true;
        }

        ResetContractState(__instance);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("UpdateStateChange")]
    private static bool UpdateStateChangePrefix(ContractManager __instance)
    {
        if (!StorylineSuppressionState.Enabled)
        {
            return true;
        }

        ResetContractState(__instance);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("ActivateContractForCompany", new[] { typeof(global::Game.Company), typeof(global::Game.ContractsObjectives.Contract) })]
    private static bool ActivateContractRuntimePrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ActivateContractForCompany", new[] { typeof(global::Game.Company), typeof(global::Data.ScriptableObject.ContractDefinition) })]
    private static bool ActivateContractDefinitionPrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPostfix]
    [HarmonyPatch("Awake")]
    private static void AwakePostfix(ContractManager __instance)
    {
        if (!StorylineSuppressionState.Enabled)
        {
            return;
        }

        ResetContractState(__instance);
    }

    private static void ResetContractState(ContractManager instance)
    {
        ResetList(instance, "allContracts");
        ResetList(instance, "availableContractsToShow");
        ResetList(instance, "availableContracts");
        ResetList(instance, "activeContracts");
        ResetList(instance, "completedContracts");

        var contractsInstancesField = AccessTools.Field(typeof(ContractManager), "contractsInstances");
        var contractsInstances = contractsInstancesField?.GetValue(instance) as Dictionary<global::Data.ScriptableObject.ContractDefinition, global::Game.ContractsObjectives.Contract>;
        if (contractsInstances == null)
        {
            contractsInstancesField?.SetValue(instance, new Dictionary<global::Data.ScriptableObject.ContractDefinition, global::Game.ContractsObjectives.Contract>());
            return;
        }

        contractsInstances.Clear();
    }

    private static void ResetList(ContractManager instance, string fieldName)
    {
        var field = AccessTools.Field(typeof(ContractManager), fieldName);
        var list = field?.GetValue(instance) as List<global::Game.ContractsObjectives.Contract>;
        if (list == null)
        {
            field?.SetValue(instance, new List<global::Game.ContractsObjectives.Contract>());
            return;
        }

        list.Clear();
    }
}

[HarmonyPatch(typeof(global::CurrentContractListMainUI))]
internal static class CurrentContractListMainUIStorylinePatches
{
    [HarmonyPrefix]
    [HarmonyPatch("InstanceOnChange")]
    private static bool InstanceOnChangePrefix(global::CurrentContractListMainUI __instance)
    {
        if (!StorylineSuppressionState.Enabled)
        {
            return true;
        }

        ClearRows(__instance);
        return false;
    }

    private static void ClearRows(global::CurrentContractListMainUI instance)
    {
        var rows = AccessTools.Field(typeof(global::CurrentContractListMainUI), "rows")?.GetValue(instance) as List<global::ContractRowMainUI>;
        if (rows == null)
        {
            return;
        }

        foreach (var row in rows)
        {
            if (row != null)
            {
                Object.Destroy(row.gameObject);
            }
        }

        rows.Clear();
    }
}

[HarmonyPatch(typeof(Tutorial.TutorialManager))]
internal static class TutorialManagerStorylinePatches
{
    [HarmonyPrefix]
    [HarmonyPatch("Start")]
    private static bool StartPrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("Update")]
    private static bool UpdatePrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("StartTutorialFromObjective")]
    private static bool StartTutorialFromObjectivePrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ShowWindows")]
    private static bool ShowWindowsPrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ShowWindows2")]
    private static bool ShowWindows2Prefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ShowTutorialObjective")]
    private static bool ShowTutorialObjectivePrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ShowTutorialPlayerClick", new System.Type[] { })]
    private static bool ShowTutorialPlayerClickPrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ShowTutorialPlayerClick", new[] { typeof(bool) })]
    private static bool ShowTutorialPlayerClickBoolPrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPostfix]
    [HarmonyPatch("Start")]
    private static void StartPostfix(Tutorial.TutorialManager __instance)
    {
        if (!StorylineSuppressionState.Enabled)
        {
            return;
        }

        __instance.HideWindows();
    }
}

[HarmonyPatch(typeof(global::Game.UI.Windows.Windows.ContractWindow))]
internal static class ContractWindowStorylinePatches
{
    [HarmonyPrefix]
    [HarmonyPatch("RefreshWindow")]
    private static bool RefreshWindowPrefix(global::Game.UI.Windows.Windows.ContractWindow __instance)
    {
        if (!StorylineSuppressionState.Enabled)
        {
            return true;
        }

        __instance.gameObject.SetActive(false);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("SetData")]
    private static bool SetDataPrefix(global::Game.UI.Windows.Windows.ContractWindow __instance)
    {
        if (!StorylineSuppressionState.Enabled)
        {
            return true;
        }

        __instance.gameObject.SetActive(false);
        return false;
    }
}

[HarmonyPatch(typeof(global::Game.UI.Windows.Windows.MissionsWindow))]
internal static class MissionsWindowStorylinePatches
{
    [HarmonyPrefix]
    [HarmonyPatch("OnClickNecCyclicalMission")]
    private static bool OnClickNewCyclicalMissionPrefix() => !StorylineSuppressionState.Enabled;

    [HarmonyPrefix]
    [HarmonyPatch("ShowCyclicalMission")]
    private static bool ShowCyclicalMissionPrefix() => !StorylineSuppressionState.Enabled;
}

