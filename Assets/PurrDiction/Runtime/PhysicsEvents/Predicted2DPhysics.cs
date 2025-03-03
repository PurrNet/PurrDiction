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
        protected override PredictedPhysics2DData GetInitialState()
        {
            return new PredictedPhysics2DData
            {
                events = new DisposableList<Physics2DEvent>(16)
            };
        }

        public override void PostSimulate(ulong tick, float delta)
        {
            ModifyState((ref PredictedPhysics2DData state) =>
            {
                int count = state.events.Count;

                if (predictionManager.isVerified)
                {
                    var hierarchy = predictionManager.hierarchy;
                    for (var i = 0; i < count; i++)
                    {
                        var ev = state.events[i];
                        TriggerEvent(hierarchy, ev);
                        ev.Dispose();
                    }
                }
                else
                {
                    for (var i = 0; i < count; i++)
                        state.events[i].Dispose();
                }

                state.events.Clear();
            });
        }

        private static void TriggerEvent(PredictedHierarchy hierarchy, Physics2DEvent ev)
        {
            if (hierarchy.TryGetComponent<PredictedRigidbody2D>(ev.me, out var me) &&
                hierarchy.TryGetComponent<PredictedRigidbody2D>(ev.other, out var other))
            {
                if (ev.isTrigger)
                {
                    switch (ev.type)
                    {
                        case PhysicsEventType.Enter:
                            me.RaiseTriggerEnter(other);
                            break;
                        case PhysicsEventType.Exit:
                            me.RaiseTriggerExit(other);
                            break;
                        case PhysicsEventType.Stay:
                            me.RaiseTriggerStay(other);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    switch (ev.type)
                    {
                        case PhysicsEventType.Enter:
                            me.RaiseCollisionEnter(other, ev.contacts);
                            break;
                        case PhysicsEventType.Exit:
                            me.RaiseCollisionExit(other, ev.contacts);
                            break;
                        case PhysicsEventType.Stay:
                            me.RaiseCollisionStay(other, ev.contacts);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody2D caller, Collision2D other)
        {
            var hierarchy = predictionManager.hierarchy;
            if (hierarchy.TryGetId(caller.gameObject, out var me) &&
                hierarchy.TryGetId(other.gameObject, out var otherId))
            {
                ModifyState((ref PredictedPhysics2DData state) =>
                {
                    var ev = new Physics2DEvent
                    {
                        isTrigger = false,
                        type = type,
                        me = me,
                        other = otherId
                    };

                    ev.contacts = new DisposableList<Physics2DContactPoint>(other.contactCount);
                    for (var i = 0; i < other.contactCount; i++)
                        ev.contacts.Add(new Physics2DContactPoint(other.GetContact(i)));
                    state.events.Add(ev);

                    if (!predictionManager.isReplaying)
                        TriggerEvent(hierarchy, ev);
                });
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody2D caller, Collider2D other)
        {
            var hierarchy = predictionManager.hierarchy;
            if (hierarchy.TryGetId(caller.gameObject, out var me) &&
                hierarchy.TryGetId(other.gameObject, out var otherId))
            {
                ModifyState((ref PredictedPhysics2DData state) =>
                {
                    var ev = new Physics2DEvent
                    {
                        isTrigger = true,
                        type = type,
                        me = me,
                        other = otherId
                    };

                    state.events.Add(ev);

                    if (!predictionManager.isReplaying)
                        TriggerEvent(hierarchy, ev);
                });
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
