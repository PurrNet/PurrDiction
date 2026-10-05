using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    // Requires the isolated candidate in the normal Unity editor test assembly.
    public sealed class VerifiedHistoryMaintenanceTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        public struct Value : IDisposable
        {
            public int number;
            public void Dispose() { }
        }

        static int Backing(History<Value> history) =>
            ((Array)typeof(History<Value>).GetField("m_values", Fields).GetValue(history)).Length;

        static void Set(Type type, object target, string field, object value) =>
            type.GetField(field, Fields).SetValue(target, value);

        sealed class Fixture : IDisposable
        {
            readonly GameObject managerObject = new GameObject("history-maintenance-manager");
            readonly GameObject networkObject = new GameObject("history-maintenance-network");
            readonly List<GameObject> extraObjects = new List<GameObject>();
            readonly NetworkManager network;
            public readonly PredictionManager manager;

            public Fixture(bool host = false)
            {
                Hasher.PrepareType(typeof(Value));
                network = networkObject.AddComponent<NetworkManager>();
                manager = managerObject.AddComponent<PredictionManager>();
                Set(typeof(NetworkManager), network, "<isClient>k__BackingField", true);
                Set(typeof(NetworkManager), network, "<isServer>k__BackingField", host);
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", network);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedClient", true);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedServer", host);
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 60);
            }

            public History<Value> Create(uint objectId, int entries, int subKey = 0)
            {
                var id = new PredictedComponentID(new PredictedObjectID(objectId), 0);
                var history = manager.GetVerifiedHistory<Value>(id, subKey, out var created);
                Assert.That(created, Is.True);
                for (int tick = 0; tick < entries; tick++) history.Write((ulong)tick, new Value { number = tick });
                return history;
            }

            public void Register(uint objectId)
            {
                var map = (Dictionary<PredictedComponentID, PredictedIdentity>)typeof(PredictionManager)
                    .GetField("_instanceMap", Fields).GetValue(manager);
                // The production predicate protects ownership by logical ID, including a Unity
                // object whose reference may be transitioning during teardown.
                map.Add(new PredictedComponentID(new PredictedObjectID(objectId), 0), null);
            }

            PredictedHierarchy _hierarchy;
            History<FULL_STATE<PredictedHierarchyState>> _verifiedTopology;

            PredictedHierarchy Hierarchy()
            {
                if (_hierarchy)
                    return _hierarchy;
                var hierarchyObject = new GameObject("history-maintenance-hierarchy");
                extraObjects.Add(hierarchyObject);
                _hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                Set(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", _hierarchy);
                return _hierarchy;
            }

            PredictedPiecePool PiecePool() =>
                (PredictedPiecePool)typeof(PredictedHierarchy).GetField("_pool", Fields).GetValue(Hierarchy());

            public void Pool(uint objectId)
            {
                var piece = new GameObject("history-maintenance-pooled-piece");
                extraObjects.Add(piece);
                PiecePool().PutPiece(1, new PredictedObjectID(objectId), 0, piece, 0);
            }

            public void Unpool(uint objectId) => PiecePool().ReleaseClaim(new PredictedObjectID(objectId));

            public void VerifiedTopology(ulong tick, params uint[] pieceIds)
            {
                if (_verifiedTopology == null)
                {
                    _verifiedTopology = new History<FULL_STATE<PredictedHierarchyState>>(600);
                    typeof(PredictedIdentity<PredictedHierarchyState>).GetField("_verifiedHistory", Fields)
                        .SetValue(Hierarchy(), _verifiedTopology);
                }

                var pieces = DisposableList<InstanceDetails>.Create(pieceIds.Length);
                foreach (var pieceId in pieceIds)
                    pieces.Add(new InstanceDetails(0, 0, new PredictedObjectID(pieceId), Vector3.zero, Quaternion.identity, null, null));
                _verifiedTopology.Write(tick, new FULL_STATE<PredictedHierarchyState>
                {
                    state = new PredictedHierarchyState(pieces, DisposableList<PredictedObjectID>.Create(0), 1000)
                });
            }

            public void Retire(uint objectId)
            {
                var id = new PredictedComponentID(new PredictedObjectID(objectId), 0);
                var map = (Dictionary<PredictedComponentID, PredictedIdentity>)typeof(PredictionManager)
                    .GetField("_instanceMap", Fields).GetValue(manager);
                map.Remove(id);
                typeof(PredictionManager).GetMethod("RetireVerifiedStores", Fields).Invoke(manager, new object[] { id });
            }

            public void Floor(ulong floor) => Set(typeof(PredictionManager), manager, "_verifiedHistoryBaselineFloor", floor);
            public void Maintain() => typeof(PredictionManager).GetMethod("MaintainVerifiedStoreStorage", Fields).Invoke(manager, null);
            public void Dispose()
            {
                _verifiedTopology?.Clear();
                typeof(PredictionManager).GetMethod("ClearVerifiedStores", Fields).Invoke(manager, null);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedClient", false);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedServer", false);
                Set(typeof(NetworkManager), network, "<isClient>k__BackingField", false);
                Set(typeof(NetworkManager), network, "<isServer>k__BackingField", false);
                foreach (var gameObject in extraObjects) if (gameObject) Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(managerObject);
                Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void RegisteredAndRollbackPoolOwnedIdsRemainEagerAndUnpruned()
        {
            using var fixture = new Fixture();
            var live = fixture.Create(10, 50);
            var module = fixture.Create(10, 50, 7);
            var pooled = fixture.Create(11, 50);
            fixture.Register(10); fixture.Pool(11); fixture.Floor(49);
            fixture.Maintain();
            foreach (var history in new[] { live, module, pooled })
            {
                Assert.That(Backing(history), Is.EqualTo(901));
                Assert.That(history.Count, Is.EqualTo(50));
                Assert.That(history.OldestTick, Is.Zero);
            }
            Assert.That(fixture.manager.verifiedHistoryMaintainedStoresTotal, Is.Zero);
        }

        [Test]
        public void MaintenanceInspectsAtMostEightStoresAndChangesAtMostOne()
        {
            using var fixture = new Fixture();
            for (uint id = 100; id < 108; id++) { fixture.Create(id, 1); fixture.Register(id); }
            var first = fixture.Create(108, 40);
            var second = fixture.Create(109, 40);
            fixture.Maintain();
            Assert.That(fixture.manager.verifiedHistoryMaintenanceInspectionsTotal, Is.EqualTo(8));
            Assert.That(Backing(first), Is.EqualTo(901));
            fixture.Maintain();
            Assert.That(Backing(first), Is.EqualTo(40));
            Assert.That(Backing(second), Is.EqualTo(901));
            Assert.That(fixture.manager.verifiedHistoryMaintainedStoresTotal, Is.EqualTo(1));
            fixture.Maintain();
            Assert.That(Backing(second), Is.EqualTo(40));
        }

        [Test]
        public void PureClientPrunesAtMostSixtyFourEntriesAndEventuallyKeepsItsAnchor()
        {
            using var fixture = new Fixture();
            var history = fixture.Create(201, 300);
            fixture.Floor(299);
            for (int pass = 0; pass < 6; pass++)
            {
                long beforeEntries = fixture.manager.verifiedHistoryPrunedEntriesTotal;
                long beforeBytes = fixture.manager.verifiedHistoryPrunedPayloadBytesTotal + fixture.manager.verifiedHistoryCompactedArrayPayloadBytesTotal;
                fixture.Maintain();
                Assert.That(fixture.manager.verifiedHistoryPrunedEntriesTotal - beforeEntries, Is.LessThanOrEqualTo(64));
                long afterBytes = fixture.manager.verifiedHistoryPrunedPayloadBytesTotal + fixture.manager.verifiedHistoryCompactedArrayPayloadBytesTotal;
                Assert.That(afterBytes - beforeBytes, Is.LessThanOrEqualTo(65536));
                Assert.That(history.ReadOrPrevious(299, out var value), Is.True);
                Assert.That(value.number, Is.EqualTo(299));
                Assert.That(history.ReadOrPrevious(10000, out value), Is.True);
                Assert.That(value.number, Is.EqualTo(299));
            }
            Assert.That(history.Count, Is.EqualTo(1));
            Assert.That(Backing(history), Is.EqualTo(1));
        }

        [Test]
        public void HostCompactsButNeverPrunesEntriesAtTheClientFloor()
        {
            using var fixture = new Fixture(true);
            var history = fixture.Create(301, 40);
            fixture.Floor(10000);
            fixture.Maintain();
            Assert.That(history.Count, Is.EqualTo(40));
            Assert.That(history.OldestTick, Is.Zero);
            Assert.That(Backing(history), Is.EqualTo(40));
            Assert.That(fixture.manager.verifiedHistoryPrunedEntriesTotal, Is.Zero);
        }

        [Test]
        public void LogicalStoreRebindRestoresEagerCapacityAndRetainsTheSameHistory()
        {
            using var fixture = new Fixture();
            var history = fixture.Create(401, 40, 8);
            fixture.Maintain();
            Assert.That(Backing(history), Is.EqualTo(40));
            var rebound = fixture.manager.GetVerifiedHistory<Value>(new PredictedComponentID(new PredictedObjectID(401), 0), 8, out var created);
            Assert.That(created, Is.False);
            Assert.That(rebound, Is.SameAs(history));
            Assert.That(Backing(rebound), Is.EqualTo(901));
            Assert.That(fixture.manager.verifiedHistoryRestoredArrayPayloadBytesTotal, Is.EqualTo(901 * 12));
            for (ulong tick = 0; tick < 40; tick++)
            {
                Assert.That(rebound.Read(tick, out var value), Is.True);
                Assert.That(value.number, Is.EqualTo((int)tick));
            }
        }

        [Test]
        public void ServerReleasesARetiredIdAtOnceAndRecyclesItsStoresForTheNextSpawn()
        {
            using var fixture = new Fixture(true);
            var state = fixture.Create(600, 40);
            var module = fixture.Create(600, 40, 3);
            fixture.Retire(600);
            fixture.Maintain();

            Assert.That(fixture.manager.verifiedStoreCount, Is.Zero, "every store of the id goes at once");
            Assert.That(fixture.manager.verifiedHistoryReleasedStoresTotal, Is.EqualTo(2));
            Assert.That(state.Count, Is.Zero);
            Assert.That(module.Count, Is.Zero);

            var reused = fixture.manager.GetVerifiedHistory<Value>(
                new PredictedComponentID(new PredictedObjectID(601), 0), 0, out var created);
            Assert.That(created, Is.True);
            Assert.That(reused, Is.SameAs(state).Or.SameAs(module));
            Assert.That(reused.Count, Is.Zero);
            Assert.That(Backing(reused), Is.EqualTo(901), "recycled stores keep their eager capacity");
            Assert.That(fixture.manager.verifiedHistoryRecycledStoresTotal, Is.EqualTo(1));
        }

        [Test]
        public void PureClientKeepsARetiredIdUntilNoVerifiedHierarchyFromTheFloorHoldsIt()
        {
            using var fixture = new Fixture();
            var history = fixture.Create(700, 20);
            fixture.VerifiedTopology(10, 700);
            fixture.Floor(10);
            fixture.Retire(700);

            fixture.Maintain();
            Assert.That(history.Count, Is.EqualTo(20), "the server may still delta against the piece at the floor");

            fixture.VerifiedTopology(12);
            fixture.Maintain();
            Assert.That(history.Count, Is.EqualTo(20), "the floor still anchors the state that held the piece");

            fixture.Floor(12);
            fixture.Maintain();
            Assert.That(fixture.manager.verifiedStoreCount, Is.Zero);
            Assert.That(history.Count, Is.Zero);
        }

        [Test]
        public void PureClientKeepsARetiredIdThatRollbackCanStillHandBack()
        {
            using var fixture = new Fixture();
            var history = fixture.Create(800, 5);
            fixture.Pool(800);
            fixture.VerifiedTopology(10);
            fixture.Floor(10);
            fixture.Retire(800);

            fixture.Maintain();
            Assert.That(history.Count, Is.EqualTo(5));

            fixture.Unpool(800);
            fixture.Maintain();
            Assert.That(fixture.manager.verifiedStoreCount, Is.Zero);
        }

        [Test]
        public void PureClientNeedsVerifiedTopologyToReleaseARetiredId()
        {
            using var withoutTopology = new Fixture();
            var kept = withoutTopology.Create(900, 5);
            withoutTopology.Floor(10);
            withoutTopology.Retire(900);
            withoutTopology.Maintain();
            // Orphan maintenance may still prune it to its anchor, but the store itself stays.
            Assert.That(withoutTopology.manager.verifiedStoreCount, Is.EqualTo(1),
                "with no verified hierarchy nothing proves the id gone");
            Assert.That(kept.ReadOrPrevious(10, out var anchor), Is.True);
            Assert.That(anchor.number, Is.EqualTo(4));

            using var withTopology = new Fixture();
            withTopology.Create(901, 5);
            withTopology.VerifiedTopology(10, 1);
            withTopology.Floor(10);
            withTopology.Retire(901);
            withTopology.Maintain();
            Assert.That(withTopology.manager.verifiedStoreCount, Is.Zero);
        }

        [TestCase("<isSimulating>k__BackingField")]
        [TestCase("<isReplaying>k__BackingField")]
        public void InProgressSimulationNeverRunsStorageMaintenance(string field)
        {
            using var fixture = new Fixture();
            var history = fixture.Create(501, 40);
            Set(typeof(PredictionManager), fixture.manager, field, true);
            fixture.Maintain();
            Assert.That(Backing(history), Is.EqualTo(901));
            Assert.That(fixture.manager.verifiedHistoryMaintenanceInspectionsTotal, Is.Zero);
            Set(typeof(PredictionManager), fixture.manager, field, false);
        }
    }
}
