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
    /// Re-running a deterministic spawn during rollback replay reuses the same POIDs, so the
    /// instance count must stay bounded even when the replayed spawn positions drift (the drift
    /// does not make the spawns new objects). Check reuse before retirement can hide churn.
    /// </summary>
    public sealed class ReplayDriftChurnTests
    {
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

        GameObject Track(GameObject go)
        {
            _cleanup.Add(go);
            return go;
        }

        const string PrefabName = "ChurnPlayerPrefab";
        const int SpawnCount = 3;
        const int ReplayRounds = 5;

        [Test]
        public void ReplayedSpawnsWithDriftingPositionsDoNotAccumulateClones()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterPrefab(hierarchy);

            for (var round = 0; round < ReplayRounds; round++)
            {
                var ids = new PredictedObjectID?[SpawnCount];

                for (var k = 0; k < SpawnCount; k++)
                {
                    var pos = new Vector3((round * SpawnCount + k) * 10f, 0, 0);
                    ids[k] = hierarchy.Create(0, pos, Quaternion.identity);
                    Assert.That(ids[k].HasValue, Is.True, $"round {round} spawn {k} failed");
                }

                for (var k = 0; k < SpawnCount; k++)
                {
                    Assert.That(ids[k].Value.instanceId.value, Is.EqualTo((uint)(2 + k)),
                        $"round {round}: replayed create must reuse the same POID block");
                }

                int liveCount = 0;
                foreach (var clone in FindPrefabClones())
                {
                    if (clone.activeInHierarchy)
                        liveCount++;
                }

                Assert.That(liveCount, Is.EqualTo(SpawnCount),
                    $"round {round}: live instance count must match the spawn count");
                TestContext.WriteLine($"round {round}: active={liveCount}, totalClones={FindPrefabClones().Count}");

                RollBackToEmpty(hierarchy);
            }

            int totalClones = FindPrefabClones().Count;
            Assert.That(totalClones, Is.EqualTo(SpawnCount),
                $"{ReplayRounds} replays of the same {SpawnCount} spawns left {totalClones} prefab clones " +
                "in the scene; replay should reuse the existing complete trees without extra instantiations");
        }

        [Test]
        public void DriftedReplayKeepsEachIdentityAndRestoresItsNewSpawnState()
        {
            var hierarchy = CreateHierarchyWorld();
            RegisterPrefab(hierarchy, withTransform: true);
            var originals = new GameObject[SpawnCount];

            for (var round = 0; round < ReplayRounds; round++)
            {
                SetField(typeof(PredictionManager), _manager, "<isReplaying>k__BackingField", round > 0);
                for (var k = 0; k < SpawnCount; k++)
                {
                    var position = new Vector3((round * SpawnCount + k) * 10f, round, 0);
                    var rotation = Quaternion.Euler(0, round * 30f + k * 5f, 0);
                    var id = hierarchy.Create(0, position, rotation);
                    Assert.That(id, Is.Not.Null);
                    Assert.That(id.Value, Is.EqualTo(new PredictedObjectID((uint)(2 + k))));
                    var instance = hierarchy.GetGameObject(id);
                    Assert.That(instance, Is.Not.Null);
                    if (round == 0)
                        originals[k] = instance;
                    else
                        Assert.That(instance, Is.SameAs(originals[k]),
                            "a complete matching tree must not be swapped with another replayed identity");

                    var identity = instance.GetComponent<PredictedTransform>();
                    Assert.That(identity.id.objectId, Is.EqualTo(id.Value));
                    Assert.That(_manager.GetIdentity(identity.id), Is.SameAs(identity));
                    Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));
                    Assert.That(instance.transform.position, Is.EqualTo(position));
                    Assert.That(Quaternion.Angle(instance.transform.rotation, rotation), Is.LessThan(0.001f));
                    Assert.That(identity.currentState.unityPosition, Is.EqualTo(position),
                        "reuse must initialize prediction from the new spawn pose");
                    Assert.That(Quaternion.Angle(identity.currentState.unityRotation, rotation), Is.LessThan(0.001f));

                    // Leave stale future state behind before rollback. Reusing the same
                    // component must not carry that state into the next replayed spawn.
                    identity.currentState.unityPosition = Vector3.one * -999f;
                    identity.currentState.unityRotation = Quaternion.Euler(90, 0, 0);
                    instance.transform.position = Vector3.one * -555f;
                }

                RollBackToEmpty(hierarchy);
            }

            Assert.That(FindPrefabClones().Count, Is.EqualTo(SpawnCount));
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

        void RegisterPrefab(PredictedHierarchy hierarchy, bool withTransform = false)
        {
            var prefab = Track(new GameObject(PrefabName));
            prefab.AddComponent<PredictedGameObject>();
            if (withTransform)
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

        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        private static PredictionManager CreateSpawnedPredictionManager(GameObject managerObject, NetworkManager networkManager)
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

        private static T GetField<T>(Type declaringType, object target, string fieldName)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }
    }
}
