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

    public struct PhysicsCollision : IDisposable
    {
        public DisposableList<PhysicsContactPoint> contacts;
        public Vector3 impulse;
        public Vector3 relativeVelocity;

        public void Dispose() => contacts.Dispose();
    }

    public struct PhysicsEvent : IPackedAuto, IDisposable
    {
        public bool isTrigger;
        public PhysicsEventType type;
        public PredictedComponentID me;
        public PredictedComponentID other;
        public PhysicsCollision collision;

        public void Dispose() => collision.Dispose();
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

    public class Predicted3DPhysics : PredictedIdentity<PredictedPhysicsData>
    {
        internal override bool isEventHandler => true;

        protected override PredictedPhysicsData GetInitialState()
        {
            return new PredictedPhysicsData
            {
                events = DisposableList<PhysicsEvent>.Create(16)
            };
        }

        public override void PostSimulate(ulong tick, float delta)
        {
            int count = currentState.events.Count;

            if (predictionManager.isVerifiedAndReplaying)
            {
                for (var i = 0; i < count; i++)
                {
                    var ev = currentState.events[i];
                    TriggerEvent(predictionManager, ev);
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

        private static void TriggerEvent(PredictionManager predictionManager, PhysicsEvent ev)
        {
            if (ev.me.TryGetIdentity<PredictedRigidbody>(predictionManager, out var me))
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
                            me.RaiseCollisionEnter(otherGo, ev.collision);
                            break;
                        case PhysicsEventType.Exit:
                            me.RaiseCollisionExit(otherGo, ev.collision);
                            break;
                        case PhysicsEventType.Stay:
                            me.RaiseCollisionStay(otherGo, ev.collision);
                            break;
                        default: throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody caller, Collision other)
        {
            if (PredictionManager.TryGetClosestPredictedID(other.gameObject, out var otherId))
            {
                var state = currentState;
                var ev = new PhysicsEvent
                {
                    isTrigger = false,
                    type = type,
                    me = caller.id,
                    other = otherId,
                    collision = new PhysicsCollision
                    {
                        impulse = other.impulse,
                        relativeVelocity = other.relativeVelocity,
                        contacts = DisposableList<PhysicsContactPoint>.Create(other.contactCount)
                    }
                };

                for (var i = 0; i < other.contactCount; i++)
                    ev.collision.contacts.Add(new PhysicsContactPoint(other.GetContact(i)));
                state.events.Add(ev);

                if (!predictionManager.isVerifiedAndReplaying)
                    TriggerEvent(predictionManager, ev);
                currentState = state;
            }
        }

        public void RegisterEvent(PhysicsEventType type, PredictedRigidbody caller, Collider other)
        {
            if (PredictionManager.TryGetClosestPredictedID(other.gameObject, out var otherId))
            {
                var state = currentState;
                var ev = new PhysicsEvent
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

        protected override PredictedPhysicsData Interpolate(PredictedPhysicsData from, PredictedPhysicsData to, float t)
        {
            return from;
        }
    }
}
