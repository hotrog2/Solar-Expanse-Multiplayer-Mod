using System;
using System.Reflection;
using UnityEngine;

namespace SolarExpanse.Multiplayer.UI;

public sealed class MainMenuMultiplayerImGui : MonoBehaviour
{
    private static MainMenuMultiplayerImGui? _instance;

    private Rect _windowRect = new Rect(0f, 0f, 560f, 330f);
    private bool _visible;
    private string _address = "127.0.0.1";
    private string _port = "27777";
    private string _playerName = "Player";
    private string _companyName = "New Company";
    private string _companySlot = "0";
    private string _maxCompanies = "4";

    public static void Show()
    {
        if (_instance == null)
        {
            var root = new GameObject("Solar Expanse Multiplayer Menu");
            DontDestroyOnLoad(root);
            _instance = root.AddComponent<MainMenuMultiplayerImGui>();
        }

        _instance.RefreshFromRuntime();
        _instance._visible = true;
        SolarExpanseMultiplayerPlugin.Log?.LogInfo("Opened main-menu multiplayer GUI.");
    }

    private void Awake()
    {
        _windowRect.x = (Screen.width - _windowRect.width) * 0.5f;
        _windowRect.y = (Screen.height - _windowRect.height) * 0.5f;
    }

    private void OnGUI()
    {
        if (!_visible)
        {
            return;
        }

        GUI.color = Color.white;
        _windowRect = GUILayout.Window(28473, _windowRect, DrawWindow, "Solar Expanse Multiplayer");
    }

    private void DrawWindow(int id)
    {
        var runtime = ResolveRuntime();

        GUILayout.Label(GetRuntimeString(runtime, "Status", "Multiplayer runtime unavailable."));
        GUILayout.Space(10f);

        GUILayout.Label("Player Name");
        _playerName = GUILayout.TextField(_playerName ?? string.Empty);

        GUILayout.Label("Company Name");
        _companyName = GUILayout.TextField(_companyName ?? string.Empty);

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        GUILayout.Label("Server Address");
        _address = GUILayout.TextField(_address ?? string.Empty);
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUILayout.Width(140f));
        GUILayout.Label("Port");
        _port = GUILayout.TextField(_port ?? string.Empty);
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUILayout.Width(140f));
        GUILayout.Label("Company Slot");
        _companySlot = GUILayout.TextField(_companySlot ?? string.Empty);
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUILayout.Width(160f));
        GUILayout.Label("Allowed Companies");
        _maxCompanies = GUILayout.TextField(_maxCompanies ?? string.Empty);
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.Space(14f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Host Server", GUILayout.Height(42f)))
        {
            InvokeRuntime(runtime, "HostFromMenu", _playerName, _port, _companySlot, _maxCompanies, _companyName);
            RefreshFromRuntime();
        }

        if (GUILayout.Button("Direct Connect", GUILayout.Height(42f)))
        {
            InvokeRuntime(runtime, "ConnectFromMenu", _address, _playerName, _port, _companySlot);
            RefreshFromRuntime();
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Disconnect", GUILayout.Height(34f)))
        {
            InvokeRuntime(runtime, "DisconnectFromMenu");
            RefreshFromRuntime();
        }

        if (GUILayout.Button("Back", GUILayout.Height(34f)))
        {
            _visible = false;
        }
        GUILayout.EndHorizontal();

        GUI.DragWindow();
    }

    private void RefreshFromRuntime()
    {
        var runtime = ResolveRuntime();
        if (runtime == null)
        {
            return;
        }

        _address = GetRuntimeString(runtime, "AddressInput", _address);
        _port = GetRuntimeString(runtime, "PortInput", _port);
        _playerName = GetRuntimeString(runtime, "PlayerNameInput", _playerName);
        _companyName = GetRuntimeString(runtime, "CompanyNameInput", _companyName);
        _companySlot = GetRuntimeString(runtime, "CompanySlotInput", _companySlot);
        _maxCompanies = GetRuntimeString(runtime, "MaxCompaniesInput", _maxCompanies);
    }

    private static object? ResolveRuntime()
    {
        if (SolarExpanseMultiplayerPlugin.Runtime != null)
        {
            return SolarExpanseMultiplayerPlugin.Runtime;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var pluginType = assembly.GetType("SolarExpanse.Multiplayer.SolarExpanseMultiplayerPlugin");
            var property = pluginType?.GetProperty("Runtime", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var runtime = property?.GetValue(null, null);
            if (runtime != null)
            {
                return runtime;
            }
        }

        return null;
    }

    private static string GetRuntimeString(object? runtime, string propertyName, string fallback)
    {
        return runtime?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(runtime, null) as string ?? fallback;
    }

    private static void InvokeRuntime(object? runtime, string methodName, params object[] args)
    {
        runtime?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(runtime, args);
    }

}
