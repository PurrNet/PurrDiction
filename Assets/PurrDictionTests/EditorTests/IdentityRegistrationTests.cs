using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class IdentityRegistrationTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>
        /// A pooled piece keeps its instance id, so a pool entry that expires after the id has
        /// already been re-registered to a live replacement must not evict the replacement.
        /// Losing the lookup silently drops every later addressed state record for that id,
        /// which freezes the receiver's copy at the last value it managed to apply.
        /// </summary>
        [Test]
        public void UnregisteringStaleIdentityKeepsLiveRegistrationForSameId()
        {
            var managerObject = new GameObject("Registration manager");
            var staleObject = new GameObject("Stale pooled identity");
            var liveObject = new GameObject("Live identity");
            try
            {
                var id = new PredictedComponentID(new PredictedObjectID(903), 0);
                var manager = CreateManager(managerObject);

                var stale = staleObject.AddComponent<OmittedStateIdentity>();
                stale.AttachForTest(manager, id);
                Register(manager, stale);

                var live = liveObject.AddComponent<OmittedStateIdentity>();
                live.AttachForTest(manager, id);
                Register(manager, live);

                Assert.That(manager.GetIdentity(id), Is.SameAs(live));

                manager.UnregisterInstance(stale);

                Assert.That(
                    manager.GetIdentity(id),
                    Is.SameAs(live),
                    "the expiring pooled identity evicted the live registration for its id");
            }
            finally
            {
                Object.DestroyImmediate(liveObject);
                Object.DestroyImmediate(staleObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void UnregisteringLiveIdentityClearsRegistration()
        {
            var managerObject = new GameObject("Registration manager");
            var identityObject = new GameObject("Live identity");
            try
            {
                var id = new PredictedComponentID(new PredictedObjectID(904), 0);
                var manager = CreateManager(managerObject);

                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                identity.AttachForTest(manager, id);
                Register(manager, identity);

                manager.UnregisterInstance(identity);

                Assert.That(manager.GetIdentity(id), Is.Null);
            }
            finally
            {
                if (identityObject)
                    Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        private static void Register(
            PredictionManager manager,
            PredictedIdentity identity)
        {
            var systems = GetField<List<PredictedIdentity>>(manager, "_systems");
            systems.Add(identity);
            SetField(manager, "_systemsCount", systems.Count);

            var instanceMap =
                GetField<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    manager,
                    "_instanceMap");
            instanceMap[identity.id] = identity;
        }

        private static PredictionManager CreateManager(GameObject managerObject)
        {
            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(manager, "<tickRate>k__BackingField", 20);
            return manager;
        }

        private static T GetField<T>(PredictionManager manager, string name)
        {
            var field = typeof(PredictionManager).GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {name}");
            return (T)field.GetValue(manager);
        }

        private static void SetField(
            PredictionManager manager,
            string name,
            object value)
        {
            var field = typeof(PredictionManager).GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"missing field {name}");
            field.SetValue(manager, value);
        }

    }
}
