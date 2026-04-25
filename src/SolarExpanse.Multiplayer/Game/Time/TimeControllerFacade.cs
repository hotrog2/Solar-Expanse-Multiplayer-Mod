using System;
using HarmonyLib;
using Manager;
using UnityEngine;

namespace SolarExpanse.Multiplayer.Game.Time;

public sealed class TimeControllerFacade
{
    private readonly AccessTools.FieldRef<TimeController, DateTime> _startDateTimeRef = AccessTools.FieldRefAccess<TimeController, DateTime>("startDateTime");
    private readonly AccessTools.FieldRef<TimeController, float> _previousPhysicalTimeRef = AccessTools.FieldRefAccess<TimeController, float>("previousPhysicalTime");

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
        _startDateTimeRef(controller) = new DateTime(gameTimeTicks, DateTimeKind.Unspecified);
        _previousPhysicalTimeRef(controller) = UnityEngine.Time.realtimeSinceStartup;
        controller.SetTimescale(timeScale, false, false);
    }
}