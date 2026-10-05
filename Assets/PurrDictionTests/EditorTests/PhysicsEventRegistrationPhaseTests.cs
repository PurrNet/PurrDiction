using System;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PhysicsEventRegistrationPhaseTests
    {
        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

#if UNITY_PHYSICS_3D
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Manual3DRegistrationSuppressesOnlyVerifiedReplay(bool verified, bool replaying)
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted3DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedPhysicsCallbacks>();
            var target = fixture.target.AddComponent<PredictedPhysicsCallbacks>();
            fixture.RegisterObjects();
            int calls = 0;
            int legacyCalls = 0;
            caller.onPredictedCollisionEnter += _ => calls++;
#pragma warning disable CS0618 // the GameObject-only events must keep firing until they are removed
            caller.onCollisionEnter += (_, _) => legacyCalls++;
#pragma warning restore CS0618
            bool authoritative = verified && replaying;
            if (authoritative)
                physics.currentState.events.Add(new PhysicsEvent { me = caller.id, other = target.id });
            fixture.SetPhase(verified, replaying);

            physics.RegisterEvent(PhysicsEventType.Enter, caller, fixture.target, false,
                Vector3.one, Vector3.up, Vector3.right);

            Assert.That(physics.currentState.events.Count, Is.EqualTo(1),
                "verified replay preserves only the seeded batch; all other phases record the manual contact");
            Assert.That(calls, Is.EqualTo(authoritative ? 0 : 1));
            physics.PostSimulate();
            Assert.That(calls, Is.EqualTo(1), "the authoritative batch still dispatches normally exactly once");
            Assert.That(legacyCalls, Is.EqualTo(calls), "the GameObject-only event mirrors the id-carrying one");
            Assert.That(physics.currentState.events.Count, Is.Zero);
        }

        [Test]
        public void Every3DEntryPointPreservesTheAuthoritativeBatch()
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted3DPhysics>();
            var controller = fixture.caller.AddComponent<CharacterController>();
            var caller = fixture.caller.AddComponent<PredictedPhysicsCallbacks>();
            var collider = fixture.target.AddComponent<BoxCollider>();
            fixture.target.AddComponent<PredictedPhysicsCallbacks>();
            fixture.RegisterObjects();
            var hit = new ControllerColliderHit();
            Set(typeof(ControllerColliderHit), hit, "m_Controller", controller);
            Set(typeof(ControllerColliderHit), hit, "m_Collider", collider);
            fixture.SetPhase(true, true);

            physics.RegisterControllerColliderHit(caller, hit);
            physics.RegisterEvent(PhysicsEventType.Enter, caller, collider);
            // The phase guard must run before reading a transient Unity Collision payload.
            Assert.DoesNotThrow(() => physics.RegisterEvent(PhysicsEventType.Enter, caller, (Collision)null));
            physics.RegisterEvent(PhysicsEventType.Enter, caller, fixture.target, true);
            Assert.That(physics.currentState.events.Count, Is.Zero);

            fixture.SetPhase(false, true);
            physics.RegisterControllerColliderHit(caller, hit);
            physics.RegisterEvent(PhysicsEventType.Enter, caller, collider);
            Assert.That(physics.currentState.events.Count, Is.EqualTo(2),
                "speculative replay must retain controller and trigger predictions");
        }
#endif

#if UNITY_PHYSICS_2D
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Direct2DRegistrationSuppressesOnlyVerifiedReplay(bool verified, bool replaying)
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted2DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedRigidbody2D>();
            var target = fixture.target.AddComponent<PredictedRigidbody2D>();
            var collider = fixture.target.AddComponent<BoxCollider2D>();
            fixture.RegisterObjects();
            int calls = 0;
            int legacyCalls = 0;
            caller.onPredictedTriggerEnter += _ => calls++;
#pragma warning disable CS0618 // the GameObject-only events must keep firing until they are removed
            caller.onTriggerEnter += _ => legacyCalls++;
#pragma warning restore CS0618
            bool authoritative = verified && replaying;
            if (authoritative)
                physics.currentState.events.Add(new Physics2DEvent { me = caller.id, other = target.id, isTrigger = true });
            fixture.SetPhase(verified, replaying);

            physics.RegisterEvent(PhysicsEventType.Enter, caller, collider);
            if (authoritative)
                Assert.DoesNotThrow(() => physics.RegisterEvent(PhysicsEventType.Enter, caller, (Collision2D)null));

            Assert.That(physics.currentState.events.Count, Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(authoritative ? 0 : 1));
            physics.PostSimulate();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(legacyCalls, Is.EqualTo(calls), "the GameObject-only event mirrors the id-carrying one");
            Assert.That(physics.currentState.events.Count, Is.Zero);
        }
#endif

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _networkObject = new("event registration network");
            private readonly GameObject _managerObject = new("event registration manager");
            public readonly GameObject caller = new("event caller");
            public readonly GameObject target = new("event target");
            public readonly PredictionManager manager;

            public Fixture()
            {
                var network = _networkObject.AddComponent<NetworkManager>();
                Set(typeof(NetworkManager), network, "_clientTickManager", new TickManager(20, network, null, false));
                manager = _managerObject.AddComponent<PredictionManager>();
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", network);
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
                manager.SetIsSpawned(true, false);
            }

            public void RegisterObjects()
            {
                manager.RegisterInstance(caller, new PredictedObjectID(10), null, false, false);
                manager.RegisterInstance(target, new PredictedObjectID(20), null, false, false);
            }

            public void SetPhase(bool verified, bool replaying)
            {
                Set(typeof(PredictionManager), manager, "<isVerified>k__BackingField", verified);
                Set(typeof(PredictionManager), manager, "<isReplaying>k__BackingField", replaying);
            }

            public void Dispose()
            {
                Object.DestroyImmediate(caller);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(_managerObject);
                Object.DestroyImmediate(_networkObject);
            }
        }

        private static void Set(Type type, object target, string fieldName, object value)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {type.Name}.{fieldName}");
            field.SetValue(target, value);
        }
    }
}
