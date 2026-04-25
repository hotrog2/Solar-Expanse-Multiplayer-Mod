using System;
using Manager;
using SolarExpanse.Multiplayer.Networking.Host;
using SolarExpanse.Multiplayer.Networking.Protocol;
using UnityEngine;

namespace SolarExpanse.Multiplayer.Game.Time;

public sealed class TimeSyncService
{
    private readonly TimeControllerFacade _facade = new TimeControllerFacade();

    private TimeSnapshotMessage? _pendingSnapshot;
    private float _nextHostBroadcastAt;
    private int _sequence;

    public bool IsApplyingRemoteSnapshot { get; private set; }

    public void QueueSnapshot(TimeSnapshotMessage snapshot)
    {
        _pendingSnapshot = snapshot;
    }

    public void HostTick(TimeController controller, DirectConnectHost host)
    {
        if (UnityEngine.Time.unscaledTime < _nextHostBroadcastAt)
        {
            return;
        }

        _nextHostBroadcastAt = UnityEngine.Time.unscaledTime + 0.25f;

        host.Broadcast(new TimeSnapshotMessage
        {
            MessageType = nameof(TimeSnapshotMessage),
            Sequence = ++_sequence,
            GameTimeTicks = _facade.GetCurrentTime(controller).Ticks,
            TimeScale = _facade.GetCurrentTimeScale(controller)
        });
    }

    public void ApplyPendingSnapshot(TimeController controller)
    {
        if (_pendingSnapshot == null)
        {
            return;
        }

        try
        {
            IsApplyingRemoteSnapshot = true;
            _facade.ApplySnapshot(controller, _pendingSnapshot.GameTimeTicks, _pendingSnapshot.TimeScale);
        }
        finally
        {
            IsApplyingRemoteSnapshot = false;
            _pendingSnapshot = null;
        }
    }
}