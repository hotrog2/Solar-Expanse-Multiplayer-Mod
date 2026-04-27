using System;
using HarmonyLib;
using Manager;
using UnityEngine;

namespace SolarExpanse.Multiplayer.Game.Time;

public sealed class TimeControllerFacade
{
    private readonly AccessTools.FieldRef<TimeController, float> _previousPhysicalTimeRef = AccessTools.FieldRefAccess<TimeController, float>("previousPhysicalTime");
    private readonly System.Reflection.MethodInfo? _setupFutureReferenceTimestamps = AccessTools.Method(typeof(TimeController), "SetupFutureReferenceTimestamps");

    private static readonly TimeSpan TimeCorrectionThreshold = TimeSpan.FromHours(12);

    public DateTime GetCurrentTime(TimeController controller)
    {
        return controller.CurrentTime;
    }

    public float GetCurrentTimeScale(TimeController controller)
    {
        return controller.CurrentTimeScale;
    }

    public void ApplySnapshot(TimeController controller, long gameTimeTicks, float timeScale)
    {
        var targetTime = new DateTime(gameTimeTicks, DateTimeKind.Unspecified);
        var currentTime = controller.CurrentTime;
        var drift = targetTime - currentTime;
        if (drift.Duration() > TimeCorrectionThreshold)
        {
            var elapsedWorldSeconds = (targetTime - controller.StartDateTime).TotalSeconds;
            if (elapsedWorldSeconds >= 0)
            {
                GravityEngine.instance.SetPhysicalTime(GravityScaler.WorldSecsToPhysTime(elapsedWorldSeconds));
                _previousPhysicalTimeRef(controller) = GravityEngine.instance.GetPhysicalTime();
                _setupFutureReferenceTimestamps?.Invoke(controller, null);
            }
        }

        if (!Mathf.Approximately(controller.CurrentTimeScale, timeScale))
        {
            controller.SetTimescale(timeScale, force: true, silent: true);
        }
    }
}
