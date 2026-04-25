using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SolarExpanse.Multiplayer.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UiText = UnityEngine.UI.Text;

namespace SolarExpanse.Multiplayer.Patches;

[HarmonyPatch(typeof(global::MenuSceneUI), "Awake")]
internal static class MainMenuMultiplayerPatch
{
    private static readonly HashSet<int> PatchedMenus = new HashSet<int>();
    private static readonly Dictionary<Canvas, MainMenuMultiplayerPanel> Panels = new Dictionary<Canvas, MainMenuMultiplayerPanel>();
    private static int _lastMissingMenuLogFrame;

    private static void Postfix(global::MenuSceneUI __instance)
    {
        Install(__instance);
    }

    internal static void InstallOnActiveMenu()
    {
        var menuType = AccessTools.TypeByName("MenuSceneUI");
        if (menuType == null)
        {
            return;
        }

        var menu = Object.FindObjectOfType(menuType) as Component;
        if (menu == null)
        {
            menu = Resources.FindObjectsOfTypeAll(menuType)
                .OfType<Component>()
                .FirstOrDefault(component => component.gameObject.scene.IsValid());
        }

        if (menu != null)
        {
            Install(menu);
            return;
        }

        if (Time.frameCount - _lastMissingMenuLogFrame > 600)
        {
            _lastMissingMenuLogFrame = Time.frameCount;
            SolarExpanseMultiplayerPlugin.Log?.LogInfo("Waiting for MenuSceneUI before installing multiplayer menu.");
        }
    }

    private static void Install(object __instance)
    {
        if (__instance is not Component menuComponent)
        {
            return;
        }

        var instanceId = menuComponent.GetInstanceID();
        if (PatchedMenus.Contains(instanceId))
        {
            return;
        }

        var menuType = __instance.GetType();
        var multiplayerButton = GetButton(menuType, __instance, "btnMult");
        var loadButton = GetButton(menuType, __instance, "btnLoad");
        var settingsButton = GetButton(menuType, __instance, "btnSettings");

        if (multiplayerButton == null && loadButton != null)
        {
            multiplayerButton = Object.Instantiate(loadButton, loadButton.transform.parent);
        }

        if (multiplayerButton == null)
        {
            SolarExpanseMultiplayerPlugin.Log?.LogWarning("Could not find or create the main-menu multiplayer button.");
            return;
        }

        multiplayerButton.gameObject.SetActive(true);
        multiplayerButton.onClick.RemoveAllListeners();
        multiplayerButton.onClick.AddListener(() => ShowMultiplayerPanel(menuComponent));
        SetButtonLabel(multiplayerButton, "MULTIPLAYER");
        PositionMultiplayerButton(multiplayerButton, loadButton, settingsButton, menuType, __instance);
        PatchedMenus.Add(instanceId);

        SolarExpanseMultiplayerPlugin.Log?.LogInfo("Main-menu multiplayer button installed.");
    }

    private static Button? GetButton(System.Type menuType, object instance, string fieldName)
    {
        return AccessTools.Field(menuType, fieldName)?.GetValue(instance) as Button;
    }

    internal static void ShowMultiplayerPanel(Component menuComponent)
    {
        var canvas = menuComponent.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            SolarExpanseMultiplayerPlugin.Log?.LogWarning("Could not open multiplayer menu because no parent canvas was found.");
            return;
        }

        if (!Panels.TryGetValue(canvas, out var panel))
        {
            panel = new MainMenuMultiplayerPanel(canvas, menuComponent);
            Panels[canvas] = panel;
        }

        panel.Show();
    }

    private static void SetButtonLabel(Button button, string label)
    {
        foreach (var tmp in button.GetComponentsInChildren<TMP_Text>(includeInactive: true))
        {
            tmp.text = label;
        }

        foreach (var text in button.GetComponentsInChildren<UiText>(includeInactive: true))
        {
            text.text = label;
        }
    }

    private static void PositionMultiplayerButton(Button multiplayerButton, Button? loadButton, Button? settingsButton, System.Type menuType, object instance)
    {
        if (loadButton == null || settingsButton == null)
        {
            return;
        }

        if (multiplayerButton.transform.parent != settingsButton.transform.parent)
        {
            multiplayerButton.transform.SetParent(settingsButton.transform.parent, worldPositionStays: false);
        }

        var loadRect = loadButton.transform as RectTransform;
        var settingsRect = settingsButton.transform as RectTransform;
        var multiplayerRect = multiplayerButton.transform as RectTransform;
        if (loadRect == null || settingsRect == null || multiplayerRect == null)
        {
            return;
        }

        var delta = settingsRect.anchoredPosition - loadRect.anchoredPosition;
        if (delta.sqrMagnitude < 16f)
        {
            delta = new Vector2(0f, -48f);
        }

        multiplayerRect.anchoredPosition = settingsRect.anchoredPosition;
        multiplayerRect.sizeDelta = settingsRect.sizeDelta;
        multiplayerButton.transform.SetSiblingIndex(settingsButton.transform.GetSiblingIndex());

        ShiftButton(GetButton(menuType, instance, "btnSettings"), delta);
        ShiftButton(GetButton(menuType, instance, "btnCredits"), delta);
        ShiftButton(GetButton(menuType, instance, "btnRoadmap"), delta);
        ShiftButton(GetButton(menuType, instance, "btnExit"), delta);
    }

    private static void ShiftButton(Button? button, Vector2 delta)
    {
        if (button?.transform is RectTransform rect)
        {
            rect.anchoredPosition += delta;
        }
    }
}

[HarmonyPatch(typeof(global::MenuSceneUI), "OnClickBtnMult")]
internal static class MainMenuMultiplayerClickPatch
{
    private static bool Prefix(global::MenuSceneUI __instance)
    {
        MainMenuMultiplayerPatch.ShowMultiplayerPanel(__instance);
        return false;
    }
}
