using System;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct Physics2DContactPoint : IPackedAuto
    {
        public Vector2 point;
        public Vector2 normal;
        public float separation;
#if UNITY_PHYSICS_2D
        public Physics2DContactPoint(ContactPoint2D contact)
        {
            point = contact.point;
            normal = contact.normal;
            separation = contact.separation;
        }
#endif
    }

    public struct Physics2DEvent : IDisposable, IDuplicate<Physics2DEvent>
    {
        public bool isTrigger;
        public PhysicsEventType type;

        public PredictedComponentID me;
        public PredictedComponentID other;
        public DisposableList<Physics2DContactPoint> contacts;

        public void Dispose()
        {
            contacts.Dispose();
        }

        public Physics2DEvent Duplicate()
        {
            return new Physics2DEvent
            {
                isTrigger = isTrigger,
                type = type,
                me = me,
                other = other,
                contacts = contacts.Duplicate()
            };
        }
    }

    public struct PredictedPhysics2DData : IPredictedData<PredictedPhysics2DData>, IDuplicate<PredictedPhysics2DData>
    {
#if UNITY_PHYSICS_2D
        public DisposableList<Physics2DEvent> events;

        public void Dispose()
        {
            if (events.isDisposed)
                return;

            int count = events.Count;
            for (var i = 0; i < count; i++)
                events[i].Dispose();
            events.Dispose();
        }

        public PredictedPhysics2DData Duplicate()
        {
            return new PredictedPhysics2DData
            {
                events = events.Duplicate()
            };
        }
#else
        public void Dispose() {}

        public PredictedPhysics2DData Duplicate() => default;
#endif
    }

    public class Predicted2DPhysics : PredictedIdentity<PredictedPhysics2DData>
    {
        internal override bool isEventHandler => true;

        protected override PredictedPhysics2DData GetInitialState()
        {
            return new PredictedPhysics2DData
            {
#if UNITY_PHYSICS_2D
                events = DisposableList<Physics2DEvent>.Create(16)
#endif
            };
        }

#if UNITY_PHYSICS_2D
        public override void PostSimulate()
        {
            ref var state = ref currentState;
            var pm = predictionManager;

            if (pm.isVerifiedAndReplaying)
            {
                for (var i = 0; i < state.events.Count; i++)
                {
                    var ev = state.events[i];
                    TriggerEvent(pm, ev);
                    ev.Dispose();
                }
            }
            else
            {
                int c = state.events.Count;
                for (var i = 0; i < c; i++)
                    state.events[i].Dispose();
            }

            state.events.Clear();
        }

        private static void TriggerEvent(PredictionManager predictionManager, Physics2DEvent ev)
        {
            if (ev.me.TryGetIdentity<PredictedRigidbody2D>(predictionManager, out var me))
            {
                var otherGo = ev.other.GetGameObject(predictionManager);
                if (ev.isTrigger)
                {
                    switch (ev.type)
                    {
                        case PhysicsEventType.Enter:
                            me.RaiseTriggerEnter(otherGo);
                            break;
                        case PhysicsEventType.Exit:
                            me.RaiseTriggerExit(otherGo);
                            break;
                        case PhysicsEventType.Stay:
                            me.RaiseTriggerStay(otherGo);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    switch (ev.type)
                    {
                        case PhysicsEventType.Enter:
                            me.RaiseCollisionEnter(otherGo, ev.contacts);
                            break;
                        case PhysicsEventType.Exit:
                            me.RaiseCollisionExit(otherGo, ev.contacts);
                            break;
                        case PhysicsEventType.Stay:
                            me.RaiseCollisionStay(otherGo, ev.contacts);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody2D caller, Collision2D other)
        {
            if (PredictionManager.TryGetClosestPredictedID(other.gameObject, out var otherId))
            {
                var state = currentState;
                var ev = new Physics2DEvent
                {
                    isTrigger = false,
                    type = type,
                    me = caller.id,
                    other = otherId
                };

                ev.contacts = DisposableList<Physics2DContactPoint>.Create(other.contactCount);
                for (var i = 0; i < other.contactCount; i++)
                    ev.contacts.Add(new Physics2DContactPoint(other.GetContact(i)));
                state.events.Add(ev);

                if (!predictionManager.isVerifiedAndReplaying)
                    TriggerEvent(predictionManager, ev);
                currentState = state;
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody2D caller, Collider2D other)
        {
            if (PredictionManager.TryGetClosestPredictedID(other.gameObject, out var otherId))
            {
                var state = currentState;
                var ev = new Physics2DEvent
                {
                    isTrigger = true,
                    type = type,
                    me = caller.id,
                    other = otherId
                };

                state.events.Add(ev);

                if (!predictionManager.isVerifiedAndReplaying)
                    TriggerEvent(predictionManager, ev);
                currentState = state;
            }
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }

        protected override PredictedPhysics2DData Interpolate(PredictedPhysics2DData from, PredictedPhysics2DData to,
            float t)
        {
            return to;
        }
#endif
    }
}
