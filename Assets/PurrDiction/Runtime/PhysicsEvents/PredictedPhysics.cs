using System;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PhysicsContactPoint : IPackedAuto
    {
        public Vector3 point;
        public Vector3 normal;
        public float separation;

        public PhysicsContactPoint(ContactPoint contact)
        {
            point = contact.point;
            normal = contact.normal;
            separation = contact.separation;
        }
    }

    public struct PhysicsEvent : IDisposable
    {
        public bool isTrigger;
        public PhysicsEventType type;

        public PredictedObjectID me;
        public PredictedObjectID other;
        public DisposableList<PhysicsContactPoint> contacts;

        public void Dispose() => contacts.Dispose();
    }

    public struct PredictedPhysicsData : IPredictedData<PredictedPhysicsData>
    {
        public DisposableList<PhysicsEvent> events;

        public void Dispose()
        {
            int count = events.Count;
            for (var i = 0; i < count; i++)
                events[i].Dispose();
            events.Dispose();
        }
    }

    public enum PhysicsEventType : byte
    {
        Enter,
        Exit,
        Stay
    }

    public class PredictedPhysics : PredictedIdentity<PredictedPhysicsData>
    {
        protected override PredictedPhysicsData GetInitialState()
        {
            return new PredictedPhysicsData
            {
                events = new DisposableList<PhysicsEvent>(16)
            };
        }

        public override void PostSimulate(ulong tick, float delta)
        {
            ModifyState((ref PredictedPhysicsData state) =>
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

        private static void TriggerEvent(PredictedHierarchy hierarchy, PhysicsEvent ev)
        {
            if (hierarchy.TryGetComponent<PredictedRigidbody>(ev.me, out var me) &&
                hierarchy.TryGetComponent<PredictedRigidbody>(ev.other, out var other))
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

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody caller, Collision other)
        {
            var hierarchy = predictionManager.hierarchy;
            if (hierarchy.TryGetId(caller.gameObject, out var me) &&
                hierarchy.TryGetId(other.gameObject, out var otherId))
            {
                ModifyState((ref PredictedPhysicsData state) =>
                {
                    var ev = new PhysicsEvent
                    {
                        isTrigger = false,
                        type = type,
                        me = me,
                        other = otherId
                    };

                    ev.contacts = new DisposableList<PhysicsContactPoint>(other.contactCount);
                    for (var i = 0; i < other.contactCount; i++)
                        ev.contacts.Add(new PhysicsContactPoint(other.GetContact(i)));
                    state.events.Add(ev);

                    if (!predictionManager.isReplaying)
                        TriggerEvent(hierarchy, ev);
                });
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody caller, Collider other)
        {
            var hierarchy = predictionManager.hierarchy;
            if (hierarchy.TryGetId(caller.gameObject, out var me) &&
                hierarchy.TryGetId(other.gameObject, out var otherId))
            {
                ModifyState((ref PredictedPhysicsData state) =>
                {
                    var ev = new PhysicsEvent
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
    }
}
