using System;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class Predicted3DPhysics : PredictedIdentity<PredictedPhysicsData>
    {
        internal override bool isEventHandler => true;

        protected override PredictedPhysicsData GetInitialState()
        {
            return new PredictedPhysicsData
            {
#if UNITY_PHYSICS_3D
                events = DisposableList<PhysicsEvent>.Create(16)
#endif
            };
        }

#if UNITY_PHYSICS_3D
        public override void PostSimulate()
        {
            ref var state = ref currentState;

            if (predictionManager.isVerifiedAndReplaying)
            {
                for (var i = 0; i < state.events.Count; i++)
                {
                    var ev = state.events[i];
                    TriggerEvent(predictionManager, ev);
                    ev.Dispose();
                }
            }
            else
            {
                int count = state.events.Count;
                for (var i = 0; i < count; i++)
                    state.events[i].Dispose();
            }

            state.events.Clear();
        }

        private static void TriggerEvent(PredictionManager predictionManager, PhysicsEvent ev)
        {
            if (ev.me.TryGetIdentity<IPredictedPhysicsCallbacks>(predictionManager, out var me))
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

        public void RegisterEvent(PhysicsEventType type, PredictedIdentity caller, Collision other)
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

        public void RegisterEvent(PhysicsEventType type, PredictedIdentity caller, Collider other)
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

        /// <summary>
        /// Registers a physics event from custom/manual collision detection (e.g. raycasts, spherecasts).
        /// Use this when the caller does not use Unity's built-in collision callbacks.
        /// </summary>
        /// <param name="type">The event type (Enter, Exit, Stay).</param>
        /// <param name="caller">The predicted identity that detected the hit.</param>
        /// <param name="other">The GameObject that was hit.</param>
        /// <param name="isTrigger">Whether the hit was a trigger (no physics response) or solid collision.</param>
        /// <param name="contactPoint">Contact point in world space. Required for solid collisions.</param>
        /// <param name="contactNormal">Surface normal at contact. Required for solid collisions.</param>
        /// <param name="relativeVelocity">Relative velocity at impact. Required for solid collisions.</param>
        public void RegisterEvent(PhysicsEventType type, PredictedIdentity caller, GameObject other, bool isTrigger,
            Vector3 contactPoint = default, Vector3 contactNormal = default, Vector3 relativeVelocity = default)
        {
            if (!PredictionManager.TryGetClosestPredictedID(other, out var otherId))
                return;

            var state = currentState;
            PhysicsCollision collision = default;

            if (!isTrigger)
            {
                var contacts = DisposableList<PhysicsContactPoint>.Create(1);
                contacts.Add(new PhysicsContactPoint
                {
                    point = contactPoint,
                    normal = contactNormal,
                    separation = 0
                });
                collision = new PhysicsCollision
                {
                    contacts = contacts,
                    impulse = default,
                    relativeVelocity = relativeVelocity
                };
            }

            var ev = new PhysicsEvent
            {
                isTrigger = isTrigger,
                type = type,
                me = caller.id,
                other = otherId,
                collision = collision
            };

            state.events.Add(ev);

            if (!predictionManager.isVerifiedAndReplaying)
                TriggerEvent(predictionManager, ev);
            currentState = state;
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }

        protected override PredictedPhysicsData Interpolate(PredictedPhysicsData from, PredictedPhysicsData to, float t)
        {
            return from;
        }
#endif
    }
}
