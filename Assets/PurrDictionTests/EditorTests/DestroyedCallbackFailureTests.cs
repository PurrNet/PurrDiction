using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class DestroyedCallbackFailureTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>
        /// A world tears down every identity when it despawns, which a client does when it transfers to a migrated
        /// host. A game's Destroyed callback that throws there (CRACKED's touched a list another identity had
        /// already disposed) used to abort the teardown: the remaining identities stayed registered, and the fresh
        /// world from the new host could not be applied on top of them.
        /// </summary>
        [Test]
        public void ThrowingDestroyedCallbackDoesNotStopTheRestOfTheObjectFromUnregistering()
        {
            var managerObject = new GameObject("Teardown manager");
            var pieceObject = new GameObject("Piece with a failing callback");
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                SetField(manager, "<tickRate>k__BackingField", 20);

                var throwing = pieceObject.AddComponent<ThrowingDestroyedIdentity>();
                throwing.AttachForTest(manager, new PredictedComponentID(new PredictedObjectID(910), 0));
                Register(manager, throwing);

                var survivor = pieceObject.AddComponent<RecordingDestroyedIdentity>();
                survivor.AttachForTest(manager, new PredictedComponentID(new PredictedObjectID(910), 1));
                Register(manager, survivor);

                LogAssert.Expect(LogType.Exception, new Regex("game callback failed"));
                Assert.DoesNotThrow(() => manager.UnregisterInstance(pieceObject, false, true));

                Assert.That(survivor.destroyedCalls, Is.EqualTo(1), "the identity after the failing one was not destroyed");
                Assert.That(manager.GetIdentity(throwing.id), Is.Null);
                Assert.That(manager.GetIdentity(survivor.id), Is.Null, "the identity after the failing one stayed registered");
                Assert.That(GetField<int>(manager, "_systemsCount"), Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(pieceObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        private static void Register(PredictionManager manager, PredictedIdentity identity)
        {
            var systems = GetField<PredictedSystemList>(manager, "_systems");
            systems.Add(identity);
            SetField(manager, "_systemsCount", systems.Count);
            GetField<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap")[identity.id] = identity;
        }

        private static T GetField<T>(PredictionManager manager, string name)
        {
            var field = typeof(PredictionManager).GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {name}");
            return (T)field.GetValue(manager);
        }

        private static void SetField(PredictionManager manager, string name, object value)
        {
            var field = typeof(PredictionManager).GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {name}");
            field.SetValue(manager, value);
        }
    }

    public sealed class ThrowingDestroyedIdentity : OmittedStateIdentity
    {
        protected override void Destroyed() => throw new InvalidOperationException("game callback failed");
    }

    public sealed class RecordingDestroyedIdentity : OmittedStateIdentity
    {
        public int destroyedCalls;

        protected override void Destroyed() => destroyedCalls++;
    }
}
