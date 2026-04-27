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
    private int _lastQueuedSequence;
    private int _lastAppliedSequence;

    public bool IsApplyingRemoteSnapshot { get; private set; }

    public void QueueSnapshot(TimeSnapshotMessage snapshot)
    {
        if (snapshot.Sequence > 0 && snapshot.Sequence <= _lastQueuedSequence)
        {
            return;
        }

        _lastQueuedSequence = Math.Max(_lastQueuedSequence, snapshot.Sequence);
        _pendingSnapshot = snapshot;
    }

    public void HostTick(TimeController controller, DirectConnectHost host)
    {
        if (UnityEngine.Time.unscaledTime < _nextHostBroadcastAt)
        {
            return;
        }

        _nextHostBroadcastAt = UnityEngine.Time.unscaledTime + 0.5f;

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

        var snapshot = _pendingSnapshot;
        if (snapshot.Sequence > 0 && snapshot.Sequence <= _lastAppliedSequence)
        {
            _pendingSnapshot = null;
            return;
        }

        try
        {
            IsApplyingRemoteSnapshot = true;
            _facade.ApplySnapshot(controller, snapshot.GameTimeTicks, snapshot.TimeScale);
            _lastAppliedSequence = Math.Max(_lastAppliedSequence, snapshot.Sequence);
        }
        finally
        {
            IsApplyingRemoteSnapshot = false;
            _pendingSnapshot = null;
        }
    }
}
