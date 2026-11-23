using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// LateAwake runs again when an identity is set up fresh after a reset: a scene object
    /// re-registered with reset (a client reconnecting) or a pooled instance recycled for a new id.
    /// The documented pattern constructs static modules there unguarded
    /// (<c>_timer = new TimerModule(this)</c>), so the second run must replace the modules of the
    /// first run instead of appending a second copy, which grows the static roster on one peer only
    /// ("Static module roster mismatch"). Guarded LateAwakes keep working as before.
    /// </summary>
    public sealed class StaticModuleReregistrationTests
    {
        readonly List<GameObject> _cleanup = new ();
        PredictionManager _manager;

        [SetUp]
        public void SetUp()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            PurrNet.Utils.Hasher.PrepareType<EmptyState>();
            PurrNet.Utils.Hasher.PrepareType<TimerModule>();
            PurrNet.Utils.Hasher.PrepareType<OtherTimerModule>();
            PurrNet.Utils.Hasher.PrepareType<TimerState>();

            var networkObject = Track(new GameObject("NetworkManager"));
            var managerObject = Track(new GameObject("PredictionManager"));
            var networkManager = networkObject.AddComponent<NetworkManager>();

            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            _manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), _manager, "<networkManager>k__BackingField", networkManager);
            SetField(typeof(PredictionManager), _manager, "<tickRate>k__BackingField", 20);
            _manager.SetIsSpawned(true, false);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            for (var i = _cleanup.Count - 1; i >= 0; i--)
            {
                if (_cleanup[i])
                    Object.DestroyImmediate(_cleanup[i]);
            }

            _cleanup.Clear();
            _manager = null;
        }

        GameObject Track(GameObject go)
        {
            _cleanup.Add(go);
            return go;
        }

        static readonly PredictedObjectID SceneObjectId = new PredictedObjectID(40);

        void RegisterAgain(GameObject go)
        {
            // What a reconnecting client does with its scene objects: unregister with reset, register with reset.
            _manager.UnregisterInstance(go, true, false);
            _manager.RegisterInstance(go, SceneObjectId, null, true, false);
        }

        [Test]
        public void UnguardedLateAwakeReplacesItsModulesAfterAReset()
        {
            var go = Track(new GameObject("UnguardedTimers"));
            var identity = go.AddComponent<UnguardedTimerIdentity>();

            _manager.RegisterInstance(go, SceneObjectId, null, true, false);
            Assert.That(identity.lateAwakeCalls, Is.EqualTo(1));
            Assert.That(identity.ModuleCount(), Is.EqualTo(2));
            var firstTimer = identity.timer;

            RegisterAgain(go);

            Assert.That(identity.lateAwakeCalls, Is.EqualTo(2), "LateAwake runs again after a reset");
            Assert.That(identity.ModuleCount(), Is.EqualTo(2),
                "the second LateAwake must replace the modules of the first, not add copies");
            Assert.That(identity.timer, Is.Not.SameAs(firstTimer));
            Assert.That(identity.ModuleAt(0), Is.SameAs(identity.timer),
                "the field set by LateAwake must point at the registered module");
            Assert.That(identity.ModuleAt(1), Is.SameAs(identity.cooldown));
            Assert.That(identity.timer.moduleIndex, Is.EqualTo(0));
            Assert.That(identity.cooldown.moduleIndex, Is.EqualTo(1));

            RegisterAgain(go);
            Assert.That(identity.ModuleCount(), Is.EqualTo(2), "repeated resets stay at one copy");
        }

        [Test]
        public void GuardedLateAwakeKeepsItsModulesAfterAReset()
        {
            var go = Track(new GameObject("GuardedTimer"));
            var identity = go.AddComponent<GuardedTimerIdentity>();

            _manager.RegisterInstance(go, SceneObjectId, null, true, false);
            var firstTimer = identity.timer;
            Assert.That(identity.ModuleCount(), Is.EqualTo(1));

            RegisterAgain(go);

            Assert.That(identity.lateAwakeCalls, Is.EqualTo(2));
            Assert.That(identity.ModuleCount(), Is.EqualTo(1));
            Assert.That(identity.timer, Is.SameAs(firstTimer), "a guarded LateAwake keeps its first module");
            Assert.That(identity.ModuleAt(0), Is.SameAs(firstTimer));
        }

        [Test]
        public void PartlyGuardedLateAwakeReplacesOnlyWhatItRegistersAgain()
        {
            var go = Track(new GameObject("PartlyGuarded"));
            var identity = go.AddComponent<PartlyGuardedIdentity>();

            _manager.RegisterInstance(go, SceneObjectId, null, true, false);
            Assert.That(identity.ModuleCount(), Is.EqualTo(2));
            var guarded = identity.guarded;
            var firstUnguarded = identity.unguarded;

            RegisterAgain(go);

            Assert.That(identity.ModuleCount(), Is.EqualTo(2));
            Assert.That(identity.ModuleAt(0), Is.SameAs(guarded), "the guarded module stays in place");
            Assert.That(identity.ModuleAt(1), Is.SameAs(identity.unguarded), "the unguarded one is replaced in place");
            Assert.That(identity.unguarded, Is.Not.SameAs(firstUnguarded));
        }

        [Test]
        public void TryGetModuleGuardKeepsItsModuleAfterAReset()
        {
            var go = Track(new GameObject("TryGetGuarded"));
            var identity = go.AddComponent<TryGetGuardedIdentity>();

            _manager.RegisterInstance(go, SceneObjectId, null, true, false);
            var first = identity.ModuleAt(0);

            RegisterAgain(go);

            Assert.That(identity.ModuleCount(), Is.EqualTo(1));
            Assert.That(identity.ModuleAt(0), Is.SameAs(first));
        }

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }

    public abstract class ModuleProbeIdentity : PredictedIdentity<EmptyState>
    {
        public int lateAwakeCalls;

        static readonly FieldInfo ModulesField =
            typeof(PredictedIdentity).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic);

        List<PredictedModule> Modules() => (List<PredictedModule>)ModulesField.GetValue(this);

        public int ModuleCount() => Modules().Count;

        public PredictedModule ModuleAt(int index) => Modules()[index];

        protected override EmptyState GetInitialState() => default;
    }

    public sealed class UnguardedTimerIdentity : ModuleProbeIdentity
    {
        public TimerModule timer;
        public TimerModule cooldown;

        protected override void LateAwake()
        {
            lateAwakeCalls++;
            timer = new TimerModule(this);
            cooldown = new TimerModule(this);
        }
    }

    public sealed class GuardedTimerIdentity : ModuleProbeIdentity
    {
        public TimerModule timer;

        protected override void LateAwake()
        {
            lateAwakeCalls++;
            if (timer != null)
                return;
            timer = new TimerModule(this);
        }
    }

    public sealed class OtherTimerModule : TimerModule
    {
        public OtherTimerModule(PredictedIdentity identity) : base(identity) { }
    }

    public sealed class PartlyGuardedIdentity : ModuleProbeIdentity
    {
        public TimerModule guarded;
        public OtherTimerModule unguarded;

        protected override void LateAwake()
        {
            lateAwakeCalls++;
            if (guarded == null)
                guarded = new TimerModule(this);
            unguarded = new OtherTimerModule(this);
        }
    }

    public sealed class TryGetGuardedIdentity : ModuleProbeIdentity
    {
        protected override void LateAwake()
        {
            lateAwakeCalls++;
            if (!TryGetModule<TimerModule>(out _))
                _ = new TimerModule(this);
        }
    }
}
