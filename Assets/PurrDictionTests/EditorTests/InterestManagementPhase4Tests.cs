using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class InterestManagementPhase4Tests
    {
        [Test]
        public void ReentryAbsolutePersistsUntilClientConfirmation()
        {
            var controls = new InterestControlState();
            var root = new PredictedObjectID(12);

            controls.Queue(root, NetworkLODProfile.CulledTier);
            controls.PrepareForFrame(10);
            controls.MarkDelivered();
            controls.Queue(root, 0);
            controls.PrepareForFrame(20);

            Assert.That(controls.hasUnconfirmedReentries, Is.True);
            Assert.That(controls.RequiresAbsolute(root), Is.True);
            Assert.That(controls.ConfirmReentry(root, 19), Is.False);
            Assert.That(controls.RequiresAbsolute(root), Is.True);
            Assert.That(controls.ConfirmReentry(root, 20), Is.True);
            Assert.That(controls.hasUnconfirmedReentries, Is.False);
            Assert.That(controls.RequiresAbsolute(root), Is.False);
        }

        [Test]
        public void PrefabTierFloorDefaultsToZeroAndClampsToProfile()
        {
            var managerObject = new GameObject(nameof(PrefabTierFloorDefaultsToZeroAndClampsToProfile));
            var networkProfile = CreateNetworkProfile();
            var predictionProfile = CreatePredictionProfile(networkProfile);
            var module = new PredictionInterestModule(
                managerObject.AddComponent<PredictionManager>(),
                predictionProfile,
                null,
                null,
                true);

            try
            {
                Assert.That(default(PredictedPrefab).minimumInterestTier, Is.Zero);
                Assert.That(module.ClampMinimumInterestTier(0), Is.Zero);
                Assert.That(module.ClampMinimumInterestTier(1), Is.EqualTo(1));
                Assert.That(module.ClampMinimumInterestTier(42), Is.EqualTo(2));
                Assert.That(module.ClampMinimumInterestTier(NetworkLODProfile.CulledTier),
                    Is.EqualTo(NetworkLODProfile.CulledTier));
            }
            finally
            {
                module.Dispose();
                Object.DestroyImmediate(predictionProfile);
                Object.DestroyImmediate(networkProfile);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void PrefabTierFloorPrecedesProviderPinsAndOwnership()
        {
            var managerObject = new GameObject(nameof(PrefabTierFloorPrecedesProviderPinsAndOwnership));
            var networkProfile = CreateNetworkProfile();
            var predictionProfile = CreatePredictionProfile(networkProfile);
            var manager = managerObject.AddComponent<PredictionManager>();
            var module = new PredictionInterestModule(manager, predictionProfile, null, null, true);
            var root = new PredictedObjectID(17);
            var player = new PlayerID(new PackedULong(23), false);
            var target = new PredictionInterestModule.RootTarget(module, root, 2);
            var provider = new RecordingTierProvider { result = 1 };
            module.provider = provider;

            try
            {
                Assert.That(module.ResolveTier(target, player, 0), Is.EqualTo(1));
                Assert.That(provider.receivedTier, Is.EqualTo(2));

                module.SetPin(player, root, PredictionInterestPin.Culled);
                Assert.That(module.ResolveTier(target, player, 0),
                    Is.EqualTo(NetworkLODProfile.CulledTier));

                target.observedOwner = player;
                Assert.That(module.ResolveTier(target, player, 0), Is.Zero);

                target.observedOwner = null;
                module.SetPin(player, root, PredictionInterestPin.Relevant);
                Assert.That(module.ResolveTier(target, player, 0), Is.Zero);
            }
            finally
            {
                module.Dispose();
                Object.DestroyImmediate(predictionProfile);
                Object.DestroyImmediate(networkProfile);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ServerEventsSeparateTierChangesFromCulledBoundaryCrossings()
        {
            var managerObject = new GameObject(nameof(ServerEventsSeparateTierChangesFromCulledBoundaryCrossings));
            var networkProfile = CreateNetworkProfile();
            var predictionProfile = CreatePredictionProfile(networkProfile);
            var module = new PredictionInterestModule(
                managerObject.AddComponent<PredictionManager>(),
                predictionProfile,
                null,
                null,
                true);
            var root = new PredictedObjectID(31);
            var player = new PlayerID(new PackedULong(47), false);
            var target = new PredictionInterestModule.RootTarget(module, root, 0);
            int tierChanges = 0;
            int becameRelevant = 0;
            int becameIrrelevant = 0;
            byte previousTier = 0;
            byte currentTier = 0;

            module.OnServerTierChanged += (changedPlayer, changedRoot, previous, current) =>
            {
                Assert.That(changedPlayer, Is.EqualTo(player));
                Assert.That(changedRoot, Is.EqualTo(root));
                tierChanges++;
                previousTier = previous;
                currentTier = current;
            };
            module.OnServerBecameRelevant += (changedPlayer, changedRoot) =>
            {
                Assert.That(changedPlayer, Is.EqualTo(player));
                Assert.That(changedRoot, Is.EqualTo(root));
                becameRelevant++;
            };
            module.OnServerBecameIrrelevant += (changedPlayer, changedRoot) =>
            {
                Assert.That(changedPlayer, Is.EqualTo(player));
                Assert.That(changedRoot, Is.EqualTo(root));
                becameIrrelevant++;
            };

            try
            {
                target.ApplyTier(player, 1);
                Assert.That(tierChanges, Is.EqualTo(1));
                Assert.That(becameRelevant, Is.Zero);
                Assert.That(becameIrrelevant, Is.Zero);
                Assert.That(previousTier, Is.Zero);
                Assert.That(currentTier, Is.EqualTo(1));

                target.ApplyTier(player, NetworkLODProfile.CulledTier);
                Assert.That(tierChanges, Is.EqualTo(2));
                Assert.That(becameRelevant, Is.Zero);
                Assert.That(becameIrrelevant, Is.EqualTo(1));
                Assert.That(previousTier, Is.EqualTo(1));
                Assert.That(currentTier, Is.EqualTo(NetworkLODProfile.CulledTier));

                target.ApplyTier(player, 2);
                target.ApplyTier(player, 2);
                Assert.That(tierChanges, Is.EqualTo(3));
                Assert.That(becameRelevant, Is.EqualTo(1));
                Assert.That(becameIrrelevant, Is.EqualTo(1));
                Assert.That(previousTier, Is.EqualTo(NetworkLODProfile.CulledTier));
                Assert.That(currentTier, Is.EqualTo(2));
            }
            finally
            {
                module.Dispose();
                Object.DestroyImmediate(predictionProfile);
                Object.DestroyImmediate(networkProfile);
                Object.DestroyImmediate(managerObject);
            }
        }

        private static NetworkLODProfile CreateNetworkProfile()
        {
            var profile = ScriptableObject.CreateInstance<NetworkLODProfile>();
            profile.Configure(new[]
            {
                new NetworkLODTier { maxDistance = 10f, sendIntervalTicks = 1 },
                new NetworkLODTier { maxDistance = 20f, sendIntervalTicks = 2 },
                new NetworkLODTier { maxDistance = 30f, sendIntervalTicks = 4 }
            }, true);
            return profile;
        }

        private static PredictionLODProfile CreatePredictionProfile(NetworkLODProfile networkProfile)
        {
            var profile = ScriptableObject.CreateInstance<PredictionLODProfile>();
            profile.Configure(networkProfile);
            return profile;
        }

        private sealed class RecordingTierProvider : IPredictionInterestProvider
        {
            public byte result;
            public byte receivedTier;

            public byte ResolveTier(
                PredictionManager manager,
                PlayerID player,
                PredictedObjectID root,
                byte computedTier)
            {
                receivedTier = computedTier;
                return result;
            }
        }
    }
}
