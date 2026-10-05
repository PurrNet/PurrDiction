using System;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
#if UNITY_PHYSICS_3D
    public sealed class PredictedPhysicsCallbacksTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
        }

        [Test]
        public void ControllerColliderHitRaiseForwardsTargetAndSnapshot()
        {
            var callbacksObject = new GameObject("Predicted physics callbacks");
            var target = new GameObject("Hit target");

            try
            {
                var callbacks = callbacksObject.AddComponent<PredictedPhysicsCallbacks>();
                var expected = new PhysicsControllerHit
                {
                    point = new Vector3(1f, 2f, 3f),
                    normal = Vector3.left,
                    moveDirection = new Vector3(0.25f, -0.5f, 0.75f),
                    moveLength = 1.5f
                };

                GameObject receivedTarget = null;
                PhysicsControllerHit receivedHit = default;
                var calls = 0;
                callbacks.onControllerColliderHit += (other, hit) =>
                {
                    calls++;
                    receivedTarget = other;
                    receivedHit = hit;
                };

                callbacks.RaiseControllerColliderHit(target, expected);

                Assert.That(calls, Is.EqualTo(1));
                Assert.That(receivedTarget, Is.SameAs(target));
                Assert.That(receivedHit.point, Is.EqualTo(expected.point));
                Assert.That(receivedHit.normal, Is.EqualTo(expected.normal));
                Assert.That(receivedHit.moveDirection, Is.EqualTo(expected.moveDirection));
                Assert.That(receivedHit.moveLength, Is.EqualTo(expected.moveLength));
            }
            finally
            {
                Object.DestroyImmediate(callbacksObject);
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void UnityControllerColliderHitCallbackRecordsAndRaisesSnapshot()
        {
            var networkObject = new GameObject("Controller message network manager");
            var managerObject = new GameObject("Controller message prediction manager");
            var callbacksObject = new GameObject("Controller message callbacks");
            var targetObject = new GameObject("Controller message target");

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var physics = manager.RegisterSystem<Predicted3DPhysics>();
                SetField(typeof(PredictionManager), manager, "<physics3d>k__BackingField", physics);

                var controller = callbacksObject.AddComponent<CharacterController>();
                var callbacks = callbacksObject.AddComponent<PredictedPhysicsCallbacks>();
                var targetCollider = targetObject.AddComponent<BoxCollider>();
                var target = targetObject.AddComponent<PredictedPhysicsCallbacks>();
                manager.RegisterInstance(callbacksObject, new PredictedObjectID(10), null, false, false);
                manager.RegisterInstance(targetObject, new PredictedObjectID(20), null, false, false);

                var expected = new PhysicsControllerHit
                {
                    point = new Vector3(1f, 2f, 3f),
                    normal = Vector3.left,
                    moveDirection = Vector3.right,
                    moveLength = 2.5f
                };
                var unityHit = new ControllerColliderHit();
                SetField(typeof(ControllerColliderHit), unityHit, "m_Controller", controller);
                SetField(typeof(ControllerColliderHit), unityHit, "m_Collider", targetCollider);
                SetField(typeof(ControllerColliderHit), unityHit, "m_Point", expected.point);
                SetField(typeof(ControllerColliderHit), unityHit, "m_Normal", expected.normal);
                SetField(typeof(ControllerColliderHit), unityHit, "m_MoveDirection", expected.moveDirection);
                SetField(typeof(ControllerColliderHit), unityHit, "m_MoveLength", expected.moveLength);

                var calls = 0;
                GameObject receivedTarget = null;
                PhysicsControllerHit receivedHit = default;
                callbacks.onControllerColliderHit += (other, hit) =>
                {
                    calls++;
                    receivedTarget = other;
                    receivedHit = hit;
                };

                var unityCallback = typeof(PredictedPhysicsCallbacks).GetMethod(
                    "OnControllerColliderHit", InstanceFields);
                Assert.That(unityCallback, Is.Not.Null);
                SetField(typeof(PredictionManager), manager, "<isSimulating>k__BackingField", true);
                unityCallback.Invoke(callbacks, new object[] { unityHit });

                Assert.That(calls, Is.EqualTo(1));
                Assert.That(physics.currentState.events.Count, Is.EqualTo(1));
                var recorded = physics.currentState.events[0];
                Assert.That(recorded.me, Is.EqualTo(callbacks.id));
                Assert.That(recorded.other, Is.EqualTo(target.id));
                Assert.That(recorded.controllerHit.HasValue, Is.True);
                Assert.That(recorded.controllerHit.Value.point, Is.EqualTo(expected.point));
                Assert.That(recorded.controllerHit.Value.normal, Is.EqualTo(expected.normal));
                Assert.That(recorded.controllerHit.Value.moveDirection, Is.EqualTo(expected.moveDirection));
                Assert.That(recorded.controllerHit.Value.moveLength, Is.EqualTo(expected.moveLength));

                physics.PostSimulate();

                Assert.That(calls, Is.EqualTo(1));
                Assert.That(receivedTarget, Is.SameAs(targetObject));
                Assert.That(receivedHit.point, Is.EqualTo(expected.point));
                Assert.That(receivedHit.normal, Is.EqualTo(expected.normal));
                Assert.That(receivedHit.moveDirection, Is.EqualTo(expected.moveDirection));
                Assert.That(receivedHit.moveLength, Is.EqualTo(expected.moveLength));
                Assert.That(physics.currentState.events.Count, Is.Zero);

                SetField(typeof(PredictedPhysicsCallbacks), callbacks, "_eventMask", PhysicsEventMask.None);
                unityCallback.Invoke(callbacks, new object[] { unityHit });
                Assert.That(physics.currentState.events.Count, Is.Zero,
                    "the event mask must suppress controller-hit recording");

                SetField(typeof(PredictedPhysicsCallbacks), callbacks, "_eventMask", (PhysicsEventMask)0x7F);
                SetField(typeof(PredictionManager), manager, "<isVerified>k__BackingField", true);
                SetField(typeof(PredictionManager), manager, "<isReplaying>k__BackingField", true);
                unityCallback.Invoke(callbacks, new object[] { unityHit });
                Assert.That(physics.currentState.events.Count, Is.Zero,
                    "Unity must not regenerate authoritative controller hits during verified replay");
            }
            finally
            {
                Object.DestroyImmediate(callbacksObject);
                Object.DestroyImmediate(targetObject);
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void PhysicsEventDuplicatePreservesControllerHitSnapshot()
        {
            var expected = new PhysicsControllerHit
            {
                point = new Vector3(4f, 5f, 6f),
                normal = Vector3.up,
                moveDirection = Vector3.forward,
                moveLength = 2.25f
            };
            var original = new PhysicsEvent
            {
                controllerHit = expected
            };

            var duplicate = original.Duplicate();

            Assert.That(duplicate.controllerHit.HasValue, Is.True);
            Assert.That(duplicate.controllerHit.Value.point, Is.EqualTo(expected.point));
            Assert.That(duplicate.controllerHit.Value.normal, Is.EqualTo(expected.normal));
            Assert.That(duplicate.controllerHit.Value.moveDirection, Is.EqualTo(expected.moveDirection));
            Assert.That(duplicate.controllerHit.Value.moveLength, Is.EqualTo(expected.moveLength));
        }

        [Test]
        public void ControllerHitSnapshotSurvivesPhysicsEventPacking()
        {
            var expected = new PhysicsEvent
            {
                me = new PredictedComponentID(new PredictedObjectID(10), 2),
                other = new PredictedComponentID(new PredictedObjectID(20), 3),
                controllerHit = new PhysicsControllerHit
                {
                    point = new Vector3(1.25f, -2.5f, 3.75f),
                    normal = new Vector3(-0.5f, 0.25f, 0.75f),
                    moveDirection = new Vector3(0.125f, 0.5f, -0.25f),
                    moveLength = 4.5f
                }
            };
            PhysicsEvent received = default;

            try
            {
                using var packer = BitPackerPool.Get();
                Packer<PhysicsEvent>.Write(packer, expected);
                var payloadBits = packer.positionInBits;
                packer.ResetPositionAndMode(true);
                Packer<PhysicsEvent>.Read(packer, ref received);

                Assert.That(received.type, Is.EqualTo(expected.type));
                Assert.That(received.me, Is.EqualTo(expected.me));
                Assert.That(received.other, Is.EqualTo(expected.other));
                Assert.That(received.controllerHit.HasValue, Is.True);
                Assert.That(received.controllerHit.Value.point, Is.EqualTo(expected.controllerHit.Value.point));
                Assert.That(received.controllerHit.Value.normal, Is.EqualTo(expected.controllerHit.Value.normal));
                Assert.That(received.controllerHit.Value.moveDirection,
                    Is.EqualTo(expected.controllerHit.Value.moveDirection));
                Assert.That(received.controllerHit.Value.moveLength,
                    Is.EqualTo(expected.controllerHit.Value.moveLength));
                Assert.That(packer.positionInBits, Is.EqualTo(payloadBits),
                    "the reader must consume the entire controller-hit payload");

                var withoutControllerPayload = expected;
                withoutControllerPayload.controllerHit = null;
                var controllerBits = SerializedBits(expected);
                var withoutControllerBits = SerializedBits(withoutControllerPayload);
                var hitBits = SerializedBits(expected.controllerHit.Value);
                Assert.That(controllerBits, Is.EqualTo(withoutControllerBits + hitBits),
                    "existing events should only pay the nullable presence bit; the snapshot is conditional");
            }
            finally
            {
                received.Dispose();
                expected.Dispose();
            }
        }

        [Test]
        public void ExistingCollisionAndTriggerEventsSurviveConditionalPacking()
        {
            var contacts = DisposableList<PhysicsContactPoint>.Create(1);
            contacts.Add(new PhysicsContactPoint
            {
                point = new Vector3(1f, 2f, 3f),
                normal = Vector3.up,
                separation = -0.25f
            });
            var expectedCollision = new PhysicsEvent
            {
                type = PhysicsEventType.Stay,
                me = new PredictedComponentID(new PredictedObjectID(30), 4),
                other = new PredictedComponentID(new PredictedObjectID(40), 5),
                collision = new PhysicsCollision
                {
                    contacts = contacts,
                    impulse = new Vector3(4f, 5f, 6f),
                    relativeVelocity = new Vector3(-1f, -2f, -3f)
                }
            };
            var expectedTrigger = new PhysicsEvent
            {
                type = PhysicsEventType.Exit,
                isTrigger = true,
                me = expectedCollision.me,
                other = expectedCollision.other
            };
            PhysicsEvent receivedCollision = default;
            PhysicsEvent receivedTrigger = default;

            try
            {
                using var packer = BitPackerPool.Get();
                Packer<PhysicsEvent>.Write(packer, expectedCollision);
                Packer<PhysicsEvent>.Write(packer, expectedTrigger);
                var payloadBits = packer.positionInBits;
                packer.ResetPositionAndMode(true);

                Packer<PhysicsEvent>.Read(packer, ref receivedCollision);
                Packer<PhysicsEvent>.Read(packer, ref receivedTrigger);

                Assert.That(receivedCollision.type, Is.EqualTo(expectedCollision.type));
                Assert.That(receivedCollision.isTrigger, Is.False);
                Assert.That(receivedCollision.collision.contacts.Count, Is.EqualTo(1));
                Assert.That(receivedCollision.collision.contacts[0].point,
                    Is.EqualTo(expectedCollision.collision.contacts[0].point));
                Assert.That(receivedCollision.collision.contacts[0].normal,
                    Is.EqualTo(expectedCollision.collision.contacts[0].normal));
                Assert.That(receivedCollision.collision.contacts[0].separation,
                    Is.EqualTo(expectedCollision.collision.contacts[0].separation));
                Assert.That(receivedCollision.collision.impulse, Is.EqualTo(expectedCollision.collision.impulse));
                Assert.That(receivedCollision.collision.relativeVelocity,
                    Is.EqualTo(expectedCollision.collision.relativeVelocity));

                Assert.That(receivedTrigger.type, Is.EqualTo(expectedTrigger.type));
                Assert.That(receivedTrigger.isTrigger, Is.True);
                Assert.That(receivedTrigger.me, Is.EqualTo(expectedTrigger.me));
                Assert.That(receivedTrigger.other, Is.EqualTo(expectedTrigger.other));
                Assert.That(receivedTrigger.controllerHit, Is.Null);
                Assert.That(packer.positionInBits, Is.EqualTo(payloadBits));
            }
            finally
            {
                receivedTrigger.Dispose();
                receivedCollision.Dispose();
                expectedTrigger.Dispose();
                expectedCollision.Dispose();
            }
        }

        [Test]
        public void ControllerHitSurvivesPhysicsEventDeltaTransition()
        {
            var baseline = new PhysicsEvent
            {
                type = PhysicsEventType.Enter,
                isTrigger = true,
                me = new PredictedComponentID(new PredictedObjectID(50), 6),
                other = new PredictedComponentID(new PredictedObjectID(60), 7)
            };
            var expected = new PhysicsEvent
            {
                type = baseline.type,
                me = baseline.me,
                other = baseline.other,
                controllerHit = new PhysicsControllerHit
                {
                    point = new Vector3(7f, 8f, 9f),
                    normal = Vector3.back,
                    moveDirection = Vector3.forward,
                    moveLength = 3.5f
                }
            };
            PhysicsEvent received = default;

            try
            {
                using var packer = BitPackerPool.Get();
                Assert.That(DeltaPacker<PhysicsEvent>.Write(packer, baseline, expected), Is.True);
                var payloadBits = packer.positionInBits;
                packer.ResetPositionAndMode(true);
                DeltaPacker<PhysicsEvent>.Read(packer, baseline, ref received);

                Assert.That(received.type, Is.EqualTo(expected.type));
                Assert.That(received.me, Is.EqualTo(expected.me));
                Assert.That(received.other, Is.EqualTo(expected.other));
                Assert.That(received.controllerHit.HasValue, Is.True);
                Assert.That(received.controllerHit.Value.point, Is.EqualTo(expected.controllerHit.Value.point));
                Assert.That(received.controllerHit.Value.normal, Is.EqualTo(expected.controllerHit.Value.normal));
                Assert.That(received.controllerHit.Value.moveDirection,
                    Is.EqualTo(expected.controllerHit.Value.moveDirection));
                Assert.That(received.controllerHit.Value.moveLength,
                    Is.EqualTo(expected.controllerHit.Value.moveLength));
                Assert.That(packer.positionInBits, Is.EqualTo(payloadBits));
            }
            finally
            {
                received.Dispose();
                expected.Dispose();
                baseline.Dispose();
            }
        }

        [Test]
        public void ControllerHitNullablePayloadSupportsDeltaUpdatesAndRemoval()
        {
            var first = new PhysicsEvent
            {
                me = new PredictedComponentID(new PredictedObjectID(70), 8),
                other = new PredictedComponentID(new PredictedObjectID(80), 9),
                controllerHit = new PhysicsControllerHit
                {
                    point = Vector3.one,
                    normal = Vector3.up,
                    moveDirection = Vector3.forward,
                    moveLength = 1f
                }
            };
            var updated = first;
            updated.controllerHit = new PhysicsControllerHit
            {
                point = new Vector3(2f, 3f, 4f),
                normal = Vector3.left,
                moveDirection = Vector3.back,
                moveLength = 5f
            };
            var removed = new PhysicsEvent
            {
                type = PhysicsEventType.Exit,
                isTrigger = true,
                me = first.me,
                other = first.other
            };

            var receivedUpdate = DeltaRoundTrip(first, updated);
            var receivedRemoval = DeltaRoundTrip(updated, removed);

            try
            {
                Assert.That(receivedUpdate.controllerHit.HasValue, Is.True);
                Assert.That(receivedUpdate.controllerHit.Value.point,
                    Is.EqualTo(updated.controllerHit.Value.point));
                Assert.That(receivedUpdate.controllerHit.Value.normal,
                    Is.EqualTo(updated.controllerHit.Value.normal));
                Assert.That(receivedUpdate.controllerHit.Value.moveDirection,
                    Is.EqualTo(updated.controllerHit.Value.moveDirection));
                Assert.That(receivedUpdate.controllerHit.Value.moveLength,
                    Is.EqualTo(updated.controllerHit.Value.moveLength));

                Assert.That(receivedRemoval.type, Is.EqualTo(removed.type));
                Assert.That(receivedRemoval.isTrigger, Is.True);
                Assert.That(receivedRemoval.controllerHit, Is.Null);
            }
            finally
            {
                receivedRemoval.Dispose();
                receivedUpdate.Dispose();
                removed.Dispose();
                updated.Dispose();
                first.Dispose();
            }
        }

        [Test]
        public void RecordedControllerHitIsDeliveredDuringVerifiedReplay()
        {
            var networkObject = new GameObject("Controller hit network manager");
            var managerObject = new GameObject("Controller hit prediction manager");
            var callbacksObject = new GameObject("Controller hit callbacks");
            var targetObject = new GameObject("Controller hit target");

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var physics = manager.RegisterSystem<Predicted3DPhysics>();
                SetField(typeof(PredictionManager), manager, "<physics3d>k__BackingField", physics);

                var callbacks = callbacksObject.AddComponent<PredictedPhysicsCallbacks>();
                var target = targetObject.AddComponent<PredictedPhysicsCallbacks>();
                manager.RegisterInstance(callbacksObject, new PredictedObjectID(10), null, false, false);
                manager.RegisterInstance(targetObject, new PredictedObjectID(20), null, false, false);

                var expectedHit = new PhysicsControllerHit
                {
                    point = new Vector3(1f, 2f, 3f),
                    normal = Vector3.left,
                    moveDirection = Vector3.right,
                    moveLength = 2.5f
                };
                physics.currentState.events.Add(new PhysicsEvent
                {
                    me = callbacks.id,
                    other = target.id,
                    controllerHit = expectedHit
                });

                var calls = 0;
                GameObject receivedTarget = null;
                PhysicsControllerHit receivedHit = default;
                callbacks.onControllerColliderHit += (other, hit) =>
                {
                    calls++;
                    receivedTarget = other;
                    receivedHit = hit;
                };
                SetField(typeof(PredictionManager), manager, "<isVerified>k__BackingField", true);
                SetField(typeof(PredictionManager), manager, "<isReplaying>k__BackingField", true);

                physics.PostSimulate();

                Assert.That(calls, Is.EqualTo(1));
                Assert.That(receivedTarget, Is.SameAs(targetObject));
                Assert.That(receivedHit.point, Is.EqualTo(expectedHit.point));
                Assert.That(receivedHit.normal, Is.EqualTo(expectedHit.normal));
                Assert.That(receivedHit.moveDirection, Is.EqualTo(expectedHit.moveDirection));
                Assert.That(receivedHit.moveLength, Is.EqualTo(expectedHit.moveLength));
                Assert.That(physics.currentState.events.Count, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(callbacksObject);
                Object.DestroyImmediate(targetObject);
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        private static PredictionManager CreateSpawnedPredictionManager(
            GameObject managerObject,
            NetworkManager networkManager)
        {
            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", networkManager);
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            manager.SetIsSpawned(true, false);
            return manager;
        }

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }

        private static int SerializedBits<T>(T value)
        {
            using var packer = BitPackerPool.Get();
            Packer<T>.Write(packer, value);
            return packer.positionInBits;
        }

        private static PhysicsEvent DeltaRoundTrip(PhysicsEvent baseline, PhysicsEvent expected)
        {
            PhysicsEvent received = default;
            using var packer = BitPackerPool.Get();
            Assert.That(DeltaPacker<PhysicsEvent>.Write(packer, baseline, expected), Is.True);
            var payloadBits = packer.positionInBits;
            packer.ResetPositionAndMode(true);
            DeltaPacker<PhysicsEvent>.Read(packer, baseline, ref received);
            Assert.That(packer.positionInBits, Is.EqualTo(payloadBits));
            return received;
        }
    }
#endif
}
