using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class LocalGameObjectCullingTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType<PredictedHierarchyState>();
            Hasher.PrepareType<InstanceDetails>();
            Hasher.PrepareType<PredictedObjectID>();
            Hasher.PrepareType<PredictedComponentID>();
            Hasher.PrepareType<PredictedGameObjectState>();
        }

        [Test]
        public void QueuedCullWaitsForItsFrameThenRetainsRecordsAndPoolsPieces()
        {
            using var context = ClientHierarchyContext.Create(
                nameof(QueuedCullWaitsForItsFrameThenRetainsRecordsAndPoolsPieces));

            context.interest.QueueLocalTier(
                context.rootId,
                NetworkLODProfile.CulledTier,
                30);
            context.interest.ApplyQueuedLocalTiers(29);

            Assert.That(context.hierarchy.TryGetGameObject(context.rootId, out var liveRoot), Is.True);
            Assert.That(liveRoot, Is.SameAs(context.root));

            context.interest.ApplyQueuedLocalTiers(30);

            Assert.That(context.root.activeSelf, Is.False);
            Assert.That(context.hierarchy.HasRootRecords(context.rootId), Is.True);

            for (var i = 0; i < context.pieceIds.Count; i++)
            {
                var pieceId = context.pieceIds[i];
                Assert.That(context.hierarchy.TryGetRootId(pieceId, out var retainedRoot), Is.True);
                Assert.That(retainedRoot, Is.EqualTo(context.rootId));
                Assert.That(context.hierarchy.TryGetGameObject(pieceId, out _), Is.False);
                Assert.That(context.pool.Contains(pieceId), Is.True);
            }
        }

        [Test]
        public void HierarchyReconcileDoesNotRematerializeACulledRoot()
        {
            using var context = ClientHierarchyContext.Create(
                nameof(HierarchyReconcileDoesNotRematerializeACulledRoot));
            var snapshot = context.hierarchy.currentState.Duplicate();

            try
            {
                context.interest.ReceiveLocalTier(
                    context.rootId,
                    NetworkLODProfile.CulledTier,
                    10);

                InvokeSetUnityState(context.hierarchy, snapshot);

                Assert.That(context.hierarchy.HasRootRecords(context.rootId), Is.True);
                Assert.That(context.hierarchy.TryGetGameObject(context.rootId, out _), Is.False);
                Assert.That(context.pool.Contains(context.rootId), Is.True);
            }
            finally
            {
                snapshot.Dispose();
            }
        }

        [Test]
        public void ReentryMaterializesDormantAndConfirmsOnlyAtTheAbsoluteTick()
        {
            using var context = ClientHierarchyContext.Create(
                nameof(ReentryMaterializesDormantAndConfirmsOnlyAtTheAbsoluteTick));

            context.interest.ReceiveLocalTier(
                context.rootId,
                NetworkLODProfile.CulledTier,
                10);
            context.interest.ReceiveLocalTier(context.rootId, 1, 20);

            Assert.That(context.interest.CanMaterializeLocalRoot(context.rootId, 19), Is.False);
            Assert.That(context.interest.CanMaterializeLocalRoot(context.rootId, 20), Is.True);
            Assert.That(context.hierarchy.TryGetGameObject(context.rootId, out _), Is.False);

            Assert.That(context.hierarchy.MaterializeLocalRoot(context.rootId), Is.True);
            Assert.That(context.hierarchy.TryGetGameObject(context.rootId, out var materializedRoot), Is.True);
            Assert.That(materializedRoot, Is.SameAs(context.root));
            Assert.That(context.manager.TryGetIdentity(
                new PredictedComponentID(context.rootId, 0), out var identity), Is.True);
            Assert.That(identity.IsLocallyDormant(), Is.True);

            context.interest.ConfirmLocalAbsolute(context.rootId, 19);

            Assert.That(context.interest.TryGetLocalRelevance(context.rootId, out var pendingTier), Is.True);
            Assert.That(pendingTier, Is.EqualTo(NetworkLODProfile.CulledTier));
            Assert.That(identity.IsLocallyDormant(), Is.True);

            context.interest.ConfirmLocalAbsolute(context.rootId, 20);

            Assert.That(context.interest.TryGetLocalRelevance(context.rootId, out var confirmedTier), Is.True);
            Assert.That(confirmedTier, Is.EqualTo(1));
            Assert.That(identity.IsLocallyDormant(), Is.False);
            Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));
            Assert.That(identity.interestSendIntervalTicks, Is.EqualTo(2));
        }

        [Test]
        public void RecullingAnUnconfirmedMaterializedRootPoolsItAgain()
        {
            using var context = ClientHierarchyContext.Create(
                nameof(RecullingAnUnconfirmedMaterializedRootPoolsItAgain));

            context.interest.ReceiveLocalTier(
                context.rootId,
                NetworkLODProfile.CulledTier,
                10);
            context.interest.ReceiveLocalTier(context.rootId, 1, 20);
            Assert.That(context.hierarchy.MaterializeLocalRoot(context.rootId), Is.True);
            Assert.That(context.hierarchy.TryGetGameObject(context.rootId, out _), Is.True);

            context.interest.ReceiveLocalTier(
                context.rootId,
                NetworkLODProfile.CulledTier,
                21);

            Assert.That(context.hierarchy.HasRootRecords(context.rootId), Is.True);
            Assert.That(context.hierarchy.TryGetGameObject(context.rootId, out _), Is.False);
            Assert.That(context.pool.Contains(context.rootId), Is.True);
            Assert.That(context.interest.CanMaterializeLocalRoot(context.rootId, 22), Is.False);
        }

        [Test]
        public void ShutdownDisposeDoesNotRematerializeCulledRoots()
        {
            using var context = ClientHierarchyContext.Create(
                nameof(ShutdownDisposeDoesNotRematerializeCulledRoots));

            context.interest.ReceiveLocalTier(
                context.rootId,
                NetworkLODProfile.CulledTier,
                10);

            context.interest.Dispose(false);

            Assert.That(context.hierarchy.HasRootRecords(context.rootId), Is.True);
            Assert.That(context.hierarchy.TryGetGameObject(context.rootId, out _), Is.False);
            Assert.That(context.pool.Contains(context.rootId), Is.True);
        }

        [Test]
        public void PruningDeletedRootClearsCulledAndPendingLocalTiers()
        {
            using var context = ClientHierarchyContext.Create(
                nameof(PruningDeletedRootClearsCulledAndPendingLocalTiers));

            context.interest.ReceiveLocalTier(
                context.rootId,
                NetworkLODProfile.CulledTier,
                10);
            context.interest.ReceiveLocalTier(context.rootId, 1, 20);

            var emptyState = new PredictedHierarchyState(
                DisposableList<InstanceDetails>.Create(),
                DisposableList<PredictedObjectID>.Create(),
                100);

            try
            {
                InvokeSetUnityState(context.hierarchy, emptyState);
                context.hierarchy.currentState.spawnedPrefabs.Clear();
                context.hierarchy.currentState.nextInstanceId = emptyState.nextInstanceId;

                context.interest.PruneStaleLocalTiers();

                Assert.That(context.hierarchy.HasRootRecords(context.rootId), Is.False);
                Assert.That(context.interest.TryGetLocalRelevance(context.rootId, out _), Is.False);
                Assert.That(context.interest.CanMaterializeLocalRoot(context.rootId, 20), Is.False);
            }
            finally
            {
                emptyState.Dispose();
            }
        }

        [Test]
        public void RuntimePrefabPoolExpiresOnlyAfterTwoSecondsOfTicks()
        {
            var managerObject = new GameObject(nameof(RuntimePrefabPoolExpiresOnlyAfterTwoSecondsOfTicks));
            var pooledObject = new GameObject("PooledRuntimePrefab");
            var manager = managerObject.AddComponent<PredictionManager>();
            var pool = new PredictedPiecePool();
            var id = new PredictedObjectID(40);

            try
            {
                SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
                pool.PutTree(
                    3,
                    id,
                    Vector3.zero,
                    pooledObject,
                    new List<PooledPiece> { new PooledPiece(id, 0, pooledObject) },
                    10,
                    true);

                SetField(typeof(PredictionManager), manager, "<localTick>k__BackingField", 50UL);
                pool.ClearOld(manager);

                Assert.That(pool.Contains(id), Is.True);
                Assert.That(pooledObject, Is.Not.Null);

                SetField(typeof(PredictionManager), manager, "<localTick>k__BackingField", 51UL);
                pool.ClearOld(manager);

                Assert.That(pool.Contains(id), Is.False);
                Assert.That(pooledObject == null, Is.True);
            }
            finally
            {
                if (pooledObject)
                    Object.DestroyImmediate(pooledObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void PositionalDecodeStopsWhenTheIncomingSystemCountExceedsTheLocalCount()
        {
            var managerObject = new GameObject(
                nameof(PositionalDecodeStopsWhenTheIncomingSystemCountExceedsTheLocalCount));

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                using var packer = BitPackerPool.Get();
                Packer<PackedInt>.Write(packer, (PackedInt)4);
                packer.ResetPositionAndMode(true);

                var method = typeof(PredictionManager).GetMethod(
                    "ReadPositionalStateEntries",
                    InstanceFields);
                Assert.That(method, Is.Not.Null);
                Assert.That(
                    () => method.Invoke(manager, new object[] { packer, 10UL, 9UL, 10UL, false }),
                    Throws.Nothing);
            }
            finally
            {
                Object.DestroyImmediate(managerObject);
            }
        }

        private static void InvokeSetUnityState(
            PredictedHierarchy hierarchy,
            PredictedHierarchyState state)
        {
            var method = typeof(PredictedHierarchy).GetMethod("SetUnityState", InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(hierarchy, new object[] { state });
        }

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }

        private sealed class ClientHierarchyContext : IDisposable
        {
            public readonly GameObject networkObject;
            public readonly GameObject managerObject;
            public readonly GameObject root;
            public readonly PredictionManager manager;
            public readonly PredictedHierarchy hierarchy;
            public readonly PredictionInterestModule interest;
            public readonly PredictedObjectID rootId;
            public readonly List<PredictedObjectID> pieceIds;
            public readonly PredictedPiecePool pool;

            private readonly NetworkLODProfile _networkProfile;
            private readonly PredictionLODProfile _predictionProfile;

            private ClientHierarchyContext(
                GameObject networkObject,
                GameObject managerObject,
                GameObject root,
                PredictionManager manager,
                PredictedHierarchy hierarchy,
                PredictionInterestModule interest,
                PredictedObjectID rootId,
                List<PredictedObjectID> pieceIds,
                PredictedPiecePool pool,
                NetworkLODProfile networkProfile,
                PredictionLODProfile predictionProfile)
            {
                this.networkObject = networkObject;
                this.managerObject = managerObject;
                this.root = root;
                this.manager = manager;
                this.hierarchy = hierarchy;
                this.interest = interest;
                this.rootId = rootId;
                this.pieceIds = pieceIds;
                this.pool = pool;
                _networkProfile = networkProfile;
                _predictionProfile = predictionProfile;
            }

            public static ClientHierarchyContext Create(string name)
            {
                var networkObject = new GameObject($"{name}.NetworkManager");
                var managerObject = new GameObject($"{name}.PredictionManager");
                var root = BuildCompoundRig(name);
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var hierarchy = manager.RegisterSystem<PredictedHierarchy>();
                SetField(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);

                hierarchy.ReserveSceneObject(root, -1);
                hierarchy.RegisterReservedSceneObjects();
                hierarchy.RunGetLatestUnityState();

                Assert.That(hierarchy.TryGetId(root, out var rootId), Is.True);

                var pieceIds = new List<PredictedObjectID>();
                var records = hierarchy.currentState.spawnedPrefabs;
                for (var i = 0; i < records.Count; i++)
                {
                    if (records[i].rootId.Equals(rootId))
                        pieceIds.Add(records[i].instanceId);
                }

                var networkProfile = ScriptableObject.CreateInstance<NetworkLODProfile>();
                networkProfile.Configure(new[]
                {
                    new NetworkLODTier { maxDistance = 10f, sendIntervalTicks = 1 },
                    new NetworkLODTier { maxDistance = 20f, sendIntervalTicks = 2 }
                }, true);
                var predictionProfile = ScriptableObject.CreateInstance<PredictionLODProfile>();
                predictionProfile.Configure(networkProfile, new[]
                {
                    new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.KeepConfigured },
                    new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.ServerRelay }
                });
                var interest = new PredictionInterestModule(
                    manager,
                    predictionProfile,
                    null,
                    null,
                    false);
                SetField(typeof(PredictionManager), manager, "<interest>k__BackingField", interest);

                var poolField = typeof(PredictedHierarchy).GetField("_pool", InstanceFields);
                Assert.That(poolField, Is.Not.Null);
                var pool = (PredictedPiecePool)poolField.GetValue(hierarchy);

                return new ClientHierarchyContext(
                    networkObject,
                    managerObject,
                    root,
                    manager,
                    hierarchy,
                    interest,
                    rootId,
                    pieceIds,
                    pool,
                    networkProfile,
                    predictionProfile);
            }

            public void Dispose()
            {
                interest.Dispose();
                SetField(typeof(PredictionManager), manager, "<interest>k__BackingField", null);

                if (root)
                    Object.DestroyImmediate(root);
                if (managerObject)
                    Object.DestroyImmediate(managerObject);
                if (networkObject)
                    Object.DestroyImmediate(networkObject);
                if (_predictionProfile)
                    Object.DestroyImmediate(_predictionProfile);
                if (_networkProfile)
                    Object.DestroyImmediate(_networkProfile);
            }

            private static GameObject BuildCompoundRig(string name)
            {
                var root = new GameObject($"{name}.Root");
                root.AddComponent<PredictedGameObject>();

                var folder = new GameObject("Folder");
                folder.transform.SetParent(root.transform);

                var firstPiece = new GameObject("FirstPiece");
                firstPiece.AddComponent<PredictedGameObject>();
                firstPiece.transform.SetParent(folder.transform);

                var childPiece = new GameObject("ChildPiece");
                childPiece.AddComponent<PredictedGameObject>();
                childPiece.transform.SetParent(firstPiece.transform);

                var secondPiece = new GameObject("SecondPiece");
                secondPiece.AddComponent<PredictedGameObject>();
                secondPiece.transform.SetParent(root.transform);

                return root;
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
        }
    }
}
