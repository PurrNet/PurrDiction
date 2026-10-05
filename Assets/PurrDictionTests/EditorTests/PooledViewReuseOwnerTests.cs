using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Rollback pools a piece without resetting it, so it keeps the id, owner and rendered view
    /// it last carried. Replay can hand that piece back under the same id. When the id now
    /// belongs to another owner it is a different logical object that merely reused the id, so
    /// its view must restart from the new spawn pose instead of sliding from wherever the
    /// previous object was last rendered. The same id under the same owner is the same object
    /// re-created by replay and keeps its view continuity.
    /// </summary>
    public sealed class PooledViewReuseOwnerTests
    {
        const string PrefabName = "PooledViewPrefab";
        const int TickRate = 20;
        const float TickDelta = 1f / TickRate;

        readonly List<GameObject> _cleanup = new ();
        readonly List<Object> _assetCleanup = new ();
        PredictionManager _manager;
        bool _previousIgnoreFailingMessages;

        [SetUp]
        public void SetUp()
        {
            _previousIgnoreFailingMessages = UnityEngine.TestTools.LogAssert.ignoreFailingMessages;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;

            foreach (var clone in FindPrefabClones())
                Object.DestroyImmediate(clone);

            if (_manager)
            {
                var poolParent = GetField<GameObject>(typeof(PredictionManager), _manager, "_poolParent");
                if (poolParent)
                    Object.DestroyImmediate(poolParent);
                _manager = null;
            }

            for (var i = 0; i < _cleanup.Count; i++)
            {
                if (_cleanup[i])
                    Object.DestroyImmediate(_cleanup[i]);
            }

            for (var i = 0; i < _assetCleanup.Count; i++)
            {
                if (_assetCleanup[i])
                    Object.DestroyImmediate(_assetCleanup[i]);
            }

            _cleanup.Clear();
            _assetCleanup.Clear();
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = _previousIgnoreFailingMessages;
        }

        [Test]
        public void ReplayReuseUnderNewOwnerRestartsViewFromNewSpawnPose()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterPrefab();

            var ownerA = new PlayerID(1, false);
            var ownerB = new PlayerID(2, false);
            var spawnA = Vector3.zero;
            var spawnB = new Vector3(0f, 0f, 150f);

            var pooled = SpawnTravelAndPool(hierarchy, ownerA, spawnA, new Vector3(4f, 0f, 0f));

            SetReplaying(true);
            var second = hierarchy.Create(0, spawnB, Quaternion.identity, ownerB);
            Assert.That(second, Is.EqualTo(new PredictedObjectID(2)), "replay must reuse the same id block");

            var instance = hierarchy.GetGameObject(second);
            Assert.That(instance, Is.SameAs(pooled), "the pooled piece is handed back under the reused id");

            var transformIdentity = instance.GetComponent<PredictedTransform>();
            Assert.That(transformIdentity.owner, Is.EqualTo((PlayerID?)ownerB));
            Assert.That(transformIdentity.currentState.unityPosition, Is.EqualTo(spawnB));

            RunViewPass(transformIdentity, 0f);
            transformIdentity.GetViewWorldPose(out var viewPosition, out _);
            Assert.That(Vector3.Distance(viewPosition, spawnB), Is.LessThan(1e-3f),
                $"view {viewPosition} must restart at the new object's spawn pose {spawnB}, " +
                "not at the previous owner's last rendered pose");

            // The next tick's sample must interpolate along the new object's own trajectory.
            transformIdentity.currentState.unityPosition = spawnB + new Vector3(0.5f, 0f, 0f);
            transformIdentity.RunUpdateRollbackInterpolation(TickDelta, false);
            RunViewPass(transformIdentity, TickDelta * 0.5f);
            transformIdentity.GetViewWorldPose(out viewPosition, out _);
            Assert.That(Mathf.Abs(viewPosition.z - spawnB.z), Is.LessThan(1e-3f),
                $"view {viewPosition} drifted laterally between the previous owner's pose and the new spawn");
            Assert.That(viewPosition.x, Is.GreaterThanOrEqualTo(spawnB.x - 1e-3f));
        }

        [Test]
        public void ReplayReuseUnderSameOwnerKeepsViewContinuity()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterPrefab();

            var owner = new PlayerID(1, false);
            var spawn = Vector3.zero;
            var lastRendered = new Vector3(4f, 0f, 0f);

            var pooled = SpawnTravelAndPool(hierarchy, owner, spawn, lastRendered);

            SetReplaying(true);
            var second = hierarchy.Create(0, spawn, Quaternion.identity, owner);
            Assert.That(second, Is.EqualTo(new PredictedObjectID(2)));

            var instance = hierarchy.GetGameObject(second);
            Assert.That(instance, Is.SameAs(pooled));

            var transformIdentity = instance.GetComponent<PredictedTransform>();
            Assert.That(transformIdentity.owner, Is.EqualTo((PlayerID?)owner));
            Assert.That(transformIdentity.currentState.unityPosition, Is.EqualTo(spawn));

            RunViewPass(transformIdentity, 0f);
            transformIdentity.GetViewWorldPose(out var viewPosition, out _);
            Assert.That(Vector3.Distance(viewPosition, lastRendered), Is.LessThan(1e-3f),
                "the same object re-created by replay keeps rendering from its last view pose");
        }

        GameObject SpawnTravelAndPool(PredictedHierarchy hierarchy, PlayerID owner, Vector3 spawn, Vector3 travelTo)
        {
            SetReplaying(false);
            var first = hierarchy.Create(0, spawn, Quaternion.identity, owner);
            Assert.That(first, Is.EqualTo(new PredictedObjectID(2)));

            var instance = hierarchy.GetGameObject(first);
            var transformIdentity = instance.GetComponent<PredictedTransform>();
            Assert.That(transformIdentity.owner, Is.EqualTo((PlayerID?)owner));

            // Simulate one tick of travel and render it, so the view buffer commits the far pose.
            transformIdentity.currentState.unityPosition = travelTo;
            transformIdentity.RunUpdateRollbackInterpolation(TickDelta, false);
            RunViewPass(transformIdentity, TickDelta);
            transformIdentity.GetViewWorldPose(out var rendered, out _);
            Assert.That(Vector3.Distance(rendered, travelTo), Is.LessThan(1e-3f), "precondition: view reached the far pose");

            RollBackToEmpty(hierarchy);
            Assert.That(instance.activeSelf, Is.False, "rollback pools the piece");
            return instance;
        }

        void RunViewPass(PredictedIdentity identity, float deltaTime)
        {
            var current = GetField<uint>(typeof(PredictionManager), _manager, "<viewPassId>k__BackingField");
            SetField(typeof(PredictionManager), _manager, "<viewPassId>k__BackingField", current + 1);
            identity.RunUpdateView(deltaTime);
        }

        void SetReplaying(bool replaying)
        {
            SetField(typeof(PredictionManager), _manager, "<isReplaying>k__BackingField", replaying);
        }

        static List<GameObject> FindPrefabClones()
        {
            var result = new List<GameObject>();
            var all = Resources.FindObjectsOfTypeAll<GameObject>();

            for (var i = 0; i < all.Length; i++)
            {
                var go = all[i];

                if (!go || !go.name.StartsWith(PrefabName, StringComparison.Ordinal))
                    continue;

                if (go.name == PrefabName)
                    continue;

                if (!go.scene.IsValid())
                    continue;

                result.Add(go);
            }

            return result;
        }

        void RegisterPrefab()
        {
            var prefab = Track(new GameObject(PrefabName));
            prefab.AddComponent<PredictedGameObject>();
            prefab.AddComponent<PredictedTransform>();

            var prefabsAsset = ScriptableObject.CreateInstance<PredictedPrefabs>();
            _assetCleanup.Add(prefabsAsset);
            prefabsAsset.prefabs.Add(new PredictedPrefab { prefab = prefab, pooled = false });

            _manager.predictedPrefabs = prefabsAsset;
        }

        static void RollBackToEmpty(PredictedHierarchy hierarchy)
        {
            var rollbackTarget = new PredictedHierarchyState(
                DisposableList<InstanceDetails>.Create(4),
                DisposableList<PredictedObjectID>.Create(4),
                2);

            try
            {
                var method = typeof(PredictedHierarchy).GetMethod("SetUnityState", InstanceFields);
                Assert.That(method, Is.Not.Null, "Missing PredictedHierarchy.SetUnityState");
                method.Invoke(hierarchy, new object[] { rollbackTarget });
            }
            finally
            {
                rollbackTarget.Dispose();
            }
        }

        GameObject Track(GameObject go)
        {
            _cleanup.Add(go);
            return go;
        }

        PredictedHierarchy CreateHierarchyWorld()
        {
            PurrCopy.Override<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<PredictedHierarchyState>();
            PurrNet.Utils.Hasher.PrepareType<InstanceDetails>();
            PurrNet.Utils.Hasher.PrepareType<PredictedObjectID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedComponentID>();
            PurrNet.Utils.Hasher.PrepareType<PredictedGameObjectState>();
            PurrNet.Utils.Hasher.PrepareType<PredictedTransformState>();

            var networkObject = Track(new GameObject("NetworkManager"));
            var managerObject = Track(new GameObject("PredictionManager"));

            var networkManager = networkObject.AddComponent<NetworkManager>();
            _manager = CreateSpawnedPredictionManager(managerObject, networkManager);
            var hierarchy = _manager.RegisterSystem<PredictedHierarchy>();
            SetField(typeof(PredictionManager), _manager, "<hierarchy>k__BackingField", hierarchy);
            return hierarchy;
        }

        const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        static PredictionManager CreateSpawnedPredictionManager(GameObject managerObject, NetworkManager networkManager)
        {
            var tickManager = new TickManager(TickRate, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", networkManager);
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", TickRate);
            manager.SetIsSpawned(true, false);
            return manager;
        }

        static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }

        static T GetField<T>(Type declaringType, object target, string fieldName)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }
    }
}
