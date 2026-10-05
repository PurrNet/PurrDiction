using System;
using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        static readonly bool PhysicsEventTraceEnabled =
            Array.IndexOf(Environment.GetCommandLineArgs(), "-physicsEventTrace") >= 0;
        int _physicsEventTraceCount;

        void TraceHistoryResync(string phase, PlayerID player, ulong failedTick, ulong coveringFullTick = 0)
        {
            if (!PhysicsEventTraceEnabled || _physicsEventTraceCount >= 512)
                return;
            _physicsEventTraceCount++;
            Debug.Log($"[HistoryResyncTrace] resync{phase} player={player} failedTick={failedTick} " +
                      $"current={localTick} coveringFullTick={coveringFullTick}");
        }

        void TracePhysicsFrameSend(PlayerID player, ulong tick, ulong baseline, bool full)
        {
            if (!PhysicsEventTraceEnabled || !full || _physicsEventTraceCount >= 512)
                return;
            _physicsEventTraceCount++;
            Debug.Log($"[PhysicsEventTrace] fullSend player={player} current={tick} baseline={baseline} " +
                      $"eventHistory={HasPhysicsEventHistory(baseline, tick)}");
        }

        void TracePhysicsEventReset(ulong tick)
        {
            if (!PhysicsEventTraceEnabled || _physicsEventTraceCount++ >= 512)
                return;
            Debug.Log($"[PhysicsEventTrace] fullReset previous={_verifiedServerTick} current={tick}");
        }

        void TracePhysicsEvents(string phase, ulong tick)
        {
            if (!PhysicsEventTraceEnabled || _physicsEventTraceCount >= 512)
                return;
#if UNITY_PHYSICS_3D
            if (physics3d && !physics3d.currentState.events.isDisposed)
            {
                var events = physics3d.currentState.events;
                for (int i = 0; i < events.Count && _physicsEventTraceCount < 512; i++)
                {
                    var ev = events[i];
                    if (ev.type == PhysicsEventType.Stay)
                        continue;
                    _physicsEventTraceCount++;
                    Debug.Log($"[PhysicsEventTrace] phase={phase} tick={tick} dimension=3 " +
                              $"type={ev.type} trigger={ev.isTrigger} me={ev.me} other={ev.other} " +
                              $"speed={ev.collision.relativeVelocity.magnitude:R} " +
                              $"resolved={ev.me.TryGetIdentity<IPredictedPhysicsCallbacks>(this, out _)}");
                }
            }
#endif
#if UNITY_PHYSICS_2D
            if (physics2d && !physics2d.currentState.events.isDisposed)
            {
                var events = physics2d.currentState.events;
                for (int i = 0; i < events.Count && _physicsEventTraceCount < 512; i++)
                {
                    var ev = events[i];
                    if (ev.type == PhysicsEventType.Stay)
                        continue;
                    _physicsEventTraceCount++;
                    Debug.Log($"[PhysicsEventTrace] phase={phase} tick={tick} dimension=2 " +
                              $"type={ev.type} trigger={ev.isTrigger} me={ev.me} other={ev.other}");
                }
            }
#endif
        }
    }
}
