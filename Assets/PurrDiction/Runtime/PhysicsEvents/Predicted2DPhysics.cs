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

        public Physics2DContactPoint(ContactPoint2D contact)
        {
            point = contact.point;
            normal = contact.normal;
            separation = contact.separation;
        }
    }

    public struct Physics2DEvent : IDisposable
    {
        public bool isTrigger;
        public PhysicsEventType type;

        public PredictedObjectID me;
        public PredictedObjectID other;
        public DisposableList<Physics2DContactPoint> contacts;

        public void Dispose() => contacts.Dispose();
    }

    public struct PredictedPhysics2DData : IPredictedData<PredictedPhysics2DData>
    {
        public DisposableList<Physics2DEvent> events;

        public void Dispose()
        {
            int count = events.Count;
            for (var i = 0; i < count; i++)
                events[i].Dispose();
            events.Dispose();
        }
    }

    public class Predicted2DPhysics : PredictedIdentity<PredictedPhysics2DData>
    {
        internal override bool isEventHandler => true;

        protected override PredictedPhysics2DData GetInitialState()
        {
            return new PredictedPhysics2DData
            {
                events = DisposableList<Physics2DEvent>.Create(16)
            };
        }

        public override void PostSimulate(ulong tick, float delta)
        {
            int count = currentState.events.Count;

            if (predictionManager.isVerifiedAndReplaying)
            {
                var h = predictionManager.hierarchy;
                for (var i = 0; i < count; i++)
                {
                    var ev = currentState.events[i];
                    TriggerEvent(h, ev);
                    ev.Dispose();
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                    currentState.events[i].Dispose();
            }

            currentState.events.Clear();
        }

        private static void TriggerEvent(PredictedHierarchy hierarchy, Physics2DEvent ev)
        {
            if (hierarchy.TryGetComponent<PredictedRigidbody2D>(ev.me, out var me))
            {
                if (ev.isTrigger)
                {
                    switch (ev.type)
                    {
                        case PhysicsEventType.Enter:
                            me.RaiseTriggerEnter(ev.other);
                            break;
                        case PhysicsEventType.Exit:
                            me.RaiseTriggerExit(ev.other);
                            break;
                        case PhysicsEventType.Stay:
                            me.RaiseTriggerStay(ev.other);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    switch (ev.type)
                    {
                        case PhysicsEventType.Enter:
                            me.RaiseCollisionEnter(ev.other, ev.contacts);
                            break;
                        case PhysicsEventType.Exit:
                            me.RaiseCollisionExit(ev.other, ev.contacts);
                            break;
                        case PhysicsEventType.Stay:
                            me.RaiseCollisionStay(ev.other, ev.contacts);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody2D caller, Collision2D other)
        {
            var h = predictionManager.hierarchy;
            if (h.TryGetId(caller.gameObject, out var me) &&
                h.TryGetId(other.gameObject, out var otherId))
            {
                var state = currentState;
                var ev = new Physics2DEvent
                {
                    isTrigger = false,
                    type = type,
                    me = me,
                    other = otherId
                };

                ev.contacts = DisposableList<Physics2DContactPoint>.Create(other.contactCount);
                for (var i = 0; i < other.contactCount; i++)
                    ev.contacts.Add(new Physics2DContactPoint(other.GetContact(i)));
                state.events.Add(ev);

                if (!predictionManager.isVerifiedAndReplaying)
                    TriggerEvent(h, ev);
                currentState = state;
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody2D caller, Collider2D other)
        {
            var h = predictionManager.hierarchy;
            if (h.TryGetId(caller.gameObject, out var me) &&
                h.TryGetId(other.gameObject, out var otherId))
            {
                var state = currentState;
                var ev = new Physics2DEvent
                {
                    isTrigger = true,
                    type = type,
                    me = me,
                    other = otherId
                };

                state.events.Add(ev);

                if (!predictionManager.isVerifiedAndReplaying)
                    TriggerEvent(h, ev);
                currentState = state;
            }
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }

        protected override PredictedPhysics2DData Interpolate(PredictedPhysics2DData from, PredictedPhysics2DData to,
            float t)
        {
            return from;
        }
    }
}
