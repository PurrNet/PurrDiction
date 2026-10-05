using System;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Physics Exit events raised because the other object was deleted cannot resolve that
    /// object's GameObject any more. The id-carrying events must still deliver them with the
    /// recorded PredictedComponentID, while the GameObject-only events keep skipping them so
    /// existing handlers never receive a null GameObject.
    /// </summary>
    public sealed class PhysicsEventDeletedOtherTests
    {
        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

#if UNITY_PHYSICS_3D
        [TestCase(true)]
        [TestCase(false)]
        public void Exit3DAfterOtherDeletedReachesOnlyIdCarryingEvent(bool isTrigger)
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted3DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedPhysicsCallbacks>();
            var target = fixture.target.AddComponent<PredictedPhysicsCallbacks>();
            fixture.RegisterObjects();
            var targetId = target.id;
            var recorder = new Recorder3D(caller);

            physics.currentState.events.Add(new PhysicsEvent
                { me = caller.id, other = targetId, type = PhysicsEventType.Exit, isTrigger = isTrigger });
            fixture.manager.UnregisterInstance(fixture.target, false, false);
            Assert.That(targetId.GetGameObject(fixture.manager), Is.Null, "precondition: the other is gone");
            fixture.SetPhase(true, true);

            physics.PostSimulate();

            var kind = isTrigger ? "trigger" : "collision";
            Assert.That(recorder.predicted, Is.EqualTo(new[] { $"{kind}/Exit" }));
            Assert.That(recorder.legacy, Is.Empty, "GameObject-only handlers must never see a null other");
            Assert.That(recorder.lastOther, Is.Null);
            Assert.That(recorder.lastOtherId, Is.EqualTo(targetId));
        }

        [TestCase(PhysicsEventType.Enter)]
        [TestCase(PhysicsEventType.Stay)]
        public void NonExit3DWithMissingOtherIsStillDropped(PhysicsEventType type)
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted3DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedPhysicsCallbacks>();
            var target = fixture.target.AddComponent<PredictedPhysicsCallbacks>();
            fixture.RegisterObjects();
            var recorder = new Recorder3D(caller);

            physics.currentState.events.Add(new PhysicsEvent
                { me = caller.id, other = target.id, type = type, isTrigger = true });
            fixture.manager.UnregisterInstance(fixture.target, false, false);
            fixture.SetPhase(true, true);

            physics.PostSimulate();

            Assert.That(recorder.predicted, Is.Empty);
            Assert.That(recorder.legacy, Is.Empty);
        }

        [Test]
        public void Exit3DWithLiveOtherReachesBothEventsWithSamePayload()
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted3DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedPhysicsCallbacks>();
            var target = fixture.target.AddComponent<PredictedPhysicsCallbacks>();
            fixture.RegisterObjects();
            var recorder = new Recorder3D(caller);

            physics.currentState.events.Add(new PhysicsEvent
                { me = caller.id, other = target.id, type = PhysicsEventType.Exit, isTrigger = false });
            fixture.SetPhase(true, true);

            physics.PostSimulate();

            Assert.That(recorder.legacy, Is.EqualTo(new[] { "collision/Exit" }));
            Assert.That(recorder.predicted, Is.EqualTo(new[] { "collision/Exit" }));
            Assert.That(recorder.lastOther, Is.SameAs(fixture.target));
            Assert.That(recorder.lastLegacyOther, Is.SameAs(fixture.target));
            Assert.That(recorder.lastOtherId, Is.EqualTo(target.id));
        }

        private sealed class Recorder3D
        {
            public readonly System.Collections.Generic.List<string> legacy = new();
            public readonly System.Collections.Generic.List<string> predicted = new();
            public GameObject lastOther;
            public GameObject lastLegacyOther;
            public PredictedComponentID lastOtherId;

            public Recorder3D(PredictedPhysicsCallbacks callbacks)
            {
#pragma warning disable CS0618 // the GameObject-only events must keep firing until they are removed
                callbacks.onCollisionEnter += (other, _) => Legacy("collision/Enter", other);
                callbacks.onCollisionExit += (other, _) => Legacy("collision/Exit", other);
                callbacks.onCollisionStay += (other, _) => Legacy("collision/Stay", other);
                callbacks.onTriggerEnter += other => Legacy("trigger/Enter", other);
                callbacks.onTriggerExit += other => Legacy("trigger/Exit", other);
                callbacks.onTriggerStay += other => Legacy("trigger/Stay", other);
#pragma warning restore CS0618
                callbacks.onPredictedCollisionEnter += c => Predicted("collision/Enter", c.other, c.otherId);
                callbacks.onPredictedCollisionExit += c => Predicted("collision/Exit", c.other, c.otherId);
                callbacks.onPredictedCollisionStay += c => Predicted("collision/Stay", c.other, c.otherId);
                callbacks.onPredictedTriggerEnter += t => Predicted("trigger/Enter", t.other, t.otherId);
                callbacks.onPredictedTriggerExit += t => Predicted("trigger/Exit", t.other, t.otherId);
                callbacks.onPredictedTriggerStay += t => Predicted("trigger/Stay", t.other, t.otherId);
            }

            private void Legacy(string kind, GameObject other)
            {
                legacy.Add(kind);
                lastLegacyOther = other;
            }

            private void Predicted(string kind, GameObject other, PredictedComponentID otherId)
            {
                predicted.Add(kind);
                lastOther = other;
                lastOtherId = otherId;
            }
        }
#endif

#if UNITY_PHYSICS_2D
        [TestCase(true)]
        [TestCase(false)]
        public void Exit2DAfterOtherDeletedReachesOnlyIdCarryingEvent(bool isTrigger)
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted2DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedRigidbody2D>();
            var target = fixture.target.AddComponent<PredictedRigidbody2D>();
            fixture.RegisterObjects();
            var targetId = target.id;
            var recorder = new Recorder2D(caller);

            physics.currentState.events.Add(new Physics2DEvent
                { me = caller.id, other = targetId, type = PhysicsEventType.Exit, isTrigger = isTrigger });
            fixture.manager.UnregisterInstance(fixture.target, false, false);
            Assert.That(targetId.GetGameObject(fixture.manager), Is.Null, "precondition: the other is gone");
            fixture.SetPhase(true, true);

            physics.PostSimulate();

            var kind = isTrigger ? "trigger" : "collision";
            Assert.That(recorder.predicted, Is.EqualTo(new[] { $"{kind}/Exit" }));
            Assert.That(recorder.legacy, Is.Empty, "GameObject-only handlers must never see a null other");
            Assert.That(recorder.lastOther, Is.Null);
            Assert.That(recorder.lastOtherId, Is.EqualTo(targetId));
        }

        [Test]
        public void Enter2DWithMissingOtherIsStillDropped()
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted2DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedRigidbody2D>();
            var target = fixture.target.AddComponent<PredictedRigidbody2D>();
            fixture.RegisterObjects();
            var recorder = new Recorder2D(caller);

            physics.currentState.events.Add(new Physics2DEvent
                { me = caller.id, other = target.id, type = PhysicsEventType.Enter, isTrigger = true });
            fixture.manager.UnregisterInstance(fixture.target, false, false);
            fixture.SetPhase(true, true);

            physics.PostSimulate();

            Assert.That(recorder.predicted, Is.Empty);
            Assert.That(recorder.legacy, Is.Empty);
        }

        [Test]
        public void Exit2DWithLiveOtherReachesBothEventsWithSamePayload()
        {
            using var fixture = new Fixture();
            var physics = fixture.manager.RegisterSystem<Predicted2DPhysics>();
            var caller = fixture.caller.AddComponent<PredictedRigidbody2D>();
            var target = fixture.target.AddComponent<PredictedRigidbody2D>();
            fixture.RegisterObjects();
            var recorder = new Recorder2D(caller);

            physics.currentState.events.Add(new Physics2DEvent
                { me = caller.id, other = target.id, type = PhysicsEventType.Exit, isTrigger = true });
            fixture.SetPhase(true, true);

            physics.PostSimulate();

            Assert.That(recorder.legacy, Is.EqualTo(new[] { "trigger/Exit" }));
            Assert.That(recorder.predicted, Is.EqualTo(new[] { "trigger/Exit" }));
            Assert.That(recorder.lastOther, Is.SameAs(fixture.target));
            Assert.That(recorder.lastLegacyOther, Is.SameAs(fixture.target));
            Assert.That(recorder.lastOtherId, Is.EqualTo(target.id));
        }

        private sealed class Recorder2D
        {
            public readonly System.Collections.Generic.List<string> legacy = new();
            public readonly System.Collections.Generic.List<string> predicted = new();
            public GameObject lastOther;
            public GameObject lastLegacyOther;
            public PredictedComponentID lastOtherId;

            public Recorder2D(PredictedRigidbody2D body)
            {
#pragma warning disable CS0618 // the GameObject-only events must keep firing until they are removed
                body.onCollisionEnter += (other, _) => Legacy("collision/Enter", other);
                body.onCollisionExit += (other, _) => Legacy("collision/Exit", other);
                body.onCollisionStay += (other, _) => Legacy("collision/Stay", other);
                body.onTriggerEnter += other => Legacy("trigger/Enter", other);
                body.onTriggerExit += other => Legacy("trigger/Exit", other);
                body.onTriggerStay += other => Legacy("trigger/Stay", other);
#pragma warning restore CS0618
                body.onPredictedCollisionEnter += c => Predicted("collision/Enter", c.other, c.otherId);
                body.onPredictedCollisionExit += c => Predicted("collision/Exit", c.other, c.otherId);
                body.onPredictedCollisionStay += c => Predicted("collision/Stay", c.other, c.otherId);
                body.onPredictedTriggerEnter += t => Predicted("trigger/Enter", t.other, t.otherId);
                body.onPredictedTriggerExit += t => Predicted("trigger/Exit", t.other, t.otherId);
                body.onPredictedTriggerStay += t => Predicted("trigger/Stay", t.other, t.otherId);
            }

            private void Legacy(string kind, GameObject other)
            {
                legacy.Add(kind);
                lastLegacyOther = other;
            }

            private void Predicted(string kind, GameObject other, PredictedComponentID otherId)
            {
                predicted.Add(kind);
                lastOther = other;
                lastOtherId = otherId;
            }
        }
#endif

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _networkObject = new("deleted other network");
            private readonly GameObject _managerObject = new("deleted other manager");
            public readonly GameObject caller = new("deleted other caller");
            public readonly GameObject target = new("deleted other target");
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
