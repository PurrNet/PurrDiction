using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// The network mirror replaces the old PredictedIdentitySpawner: NetworkIdentity components
    /// inside predicted prefabs follow the authoritative topology on the server and the verified
    /// topology on clients, with observers gated on committed visibility and acknowledgements.
    /// </summary>
    public class PredictedNetworkMirrorTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly List<GameObject> _objects = new();
        private readonly PlayerID _owner = new(3, false);
        private readonly PlayerID _viewer = new(7, false);

        [TearDown]
        public void TearDown()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i])
                    UnityEngine.Object.DestroyImmediate(_objects[i]);
            }
            _objects.Clear();
        }

        [Test]
        public void PrototypeCountsNetworkIdentitiesPerPieceInHierarchyOrder()
        {
            var prefab = MixedPrefab();
            var proto = PiecePrototype.Build(prefab);

            Assert.That(proto, Is.Not.Null);
            Assert.That(proto.pieceCount, Is.EqualTo(2));
            Assert.That(proto.networkIdentityCount, Is.EqualTo(3));
            Assert.That(proto.pieces[0].networkIdentityCount, Is.EqualTo(2), "root piece owns its own and its plain child's identity");
            Assert.That(proto.pieces[0].networkIdOffset, Is.Zero);
            Assert.That(proto.pieces[1].networkIdentityCount, Is.EqualTo(1));
            Assert.That(proto.pieces[1].networkIdOffset, Is.EqualTo(2));

            var collected = new List<NetworkIdentity>();
            PiecePrototype.CollectNetworkIdentities(prefab.transform, collected);
            Assert.That(collected.Count, Is.EqualTo(2));
            Assert.That(collected[0].gameObject, Is.SameAs(prefab));
            Assert.That(collected[1].gameObject.name, Is.EqualTo("plain child"));
        }

        [Test]
        public void ServerCreateReservesOneBlockAndSpawnsEveryIdentityWithItsOwner()
        {
            using var world = new World(this, asServer: true);
            var id = world.Create(_owner);

            Assert.That(world.hierarchy.TryGetRecord(id, out var root), Is.True);
            Assert.That(world.hierarchy.TryGetRecord(new PredictedObjectID(id.instanceId.value + 1), out var child), Is.True);
            Assert.That(root.networkId, Is.EqualTo(new NetworkID(100)));
            Assert.That(child.networkId, Is.EqualTo(new NetworkID(102)), "the child piece continues the root's block");

            Assert.That(world.bridge.reservations, Is.EqualTo(new[] { 3 }));
            Assert.That(world.bridge.early, Has.Count.EqualTo(3));
            Assert.That(world.bridge.early[0].id, Is.EqualTo(new NetworkID(100)));
            Assert.That(world.bridge.early[1].id, Is.EqualTo(new NetworkID(101)));
            Assert.That(world.bridge.early[2].id, Is.EqualTo(new NetworkID(102)));
            Assert.That(world.bridge.early[1].identity.gameObject.name, Is.EqualTo("plain child"));
            Assert.That(world.bridge.finalized, Has.Count.EqualTo(3));
            Assert.That(world.bridge.owners, Is.EqualTo(new PlayerID?[] { _owner, _owner, _owner }));
            Assert.That(world.mirror.spawnedPieceCount, Is.EqualTo(2));
        }

        [Test]
        public void ServerDeleteDespawnsEveryMirroredIdentity()
        {
            using var world = new World(this, asServer: true);
            var id = world.Create(null);
            world.bridge.Reset();

            world.DeleteNow(id);

            Assert.That(world.bridge.despawned, Has.Count.EqualTo(3));
            Assert.That(world.mirror.spawnedPieceCount, Is.Zero);
            Assert.That(world.bridge.early, Is.Empty);
        }

        [Test]
        public void ServerOwnershipChangesFollowThePredictedOwner()
        {
            using var world = new World(this, asServer: true);
            var id = world.Create(null);
            world.bridge.Reset();

            world.manager.SetOwnership(id, _owner);

            Assert.That(world.bridge.owners, Is.EqualTo(new PlayerID?[] { _owner, _owner, _owner }));
        }

        [Test]
        public void ClientSpawnsOnlyFromVerifiedTopologyAndDespawnsWhenItLeaves()
        {
            using var world = new World(this, asServer: false);

            var speculative = world.Create(null);
            Assert.That(world.bridge.early, Is.Empty, "speculative client spawns never touch PurrNet");
            Assert.That(world.bridge.reservations, Is.Empty, "clients never allocate network ids");
            world.DeleteNow(speculative);

            var verifiedId = new PredictedObjectID(40);
            var block = new NetworkID(500);
            world.ApplyVerified(21, verifiedId, block);
            world.hierarchy.SyncNetworkMirror();

            Assert.That(world.bridge.early, Has.Count.EqualTo(3));
            Assert.That(world.bridge.early[0].id, Is.EqualTo(block));
            Assert.That(world.bridge.early[2].id, Is.EqualTo(new NetworkID(502)));
            Assert.That(world.bridge.finalized, Has.Count.EqualTo(3));
            Assert.That(world.bridge.owners, Is.Empty, "ownership is server authority; clients only follow PurrNet");
            Assert.That(world.mirror.IsSpawned(verifiedId), Is.True);

            world.bridge.Reset();
            world.hierarchy.SyncNetworkMirror();
            Assert.That(world.bridge.early, Is.Empty, "a repeated sync is idempotent");

            world.ApplyVerified(22, null, default);
            world.hierarchy.SyncNetworkMirror();
            Assert.That(world.bridge.despawned, Has.Count.EqualTo(3));
            Assert.That(world.mirror.spawnedPieceCount, Is.Zero);
        }

        [Test]
        public void ClientSpeculativeRemovalOfAVerifiedInstanceDespawnsUntilItReturns()
        {
            using var world = new World(this, asServer: false);
            var verifiedId = new PredictedObjectID(40);
            world.ApplyVerified(21, verifiedId, new NetworkID(500));
            world.hierarchy.SyncNetworkMirror();
            world.bridge.Reset();

            // A mispredicted delete pools the object; its identities cannot stay spawned on it.
            world.DeleteNow(verifiedId);
            Assert.That(world.bridge.despawned, Has.Count.EqualTo(3));
            Assert.That(world.mirror.spawnedPieceCount, Is.Zero);

            world.bridge.Reset();
            world.ApplyVerified(22, verifiedId, new NetworkID(500));
            world.hierarchy.SyncNetworkMirror();
            Assert.That(world.bridge.early, Has.Count.EqualTo(3), "the verified frame re-materializes and re-spawns it");
        }

        [Test]
        public void ObserversFollowCommittedVisibilityOnceThePlayerAcknowledgedTheSpawn()
        {
            using var world = new World(this, asServer: true);
            var id = world.Create(null);
            world.bridge.Reset();
            var timeline = new PlayerVisibilityTimeline(defaultVisible: true);
            ulong spawnTick = world.manager.localTick + 1;

            world.mirror.SyncObservers(_viewer, timeline, spawnTick - 1);
            Assert.That(world.bridge.observers, Is.Empty, "no observer before the client can have the instance");

            world.mirror.SyncObservers(_viewer, timeline, spawnTick);
            Assert.That(world.bridge.observers, Has.Count.EqualTo(3));
            Assert.That(world.mirror.IsObserving(_viewer, id), Is.True);

            world.bridge.Reset();
            world.mirror.SyncObservers(_viewer, timeline, spawnTick + 5);
            Assert.That(world.bridge.observers, Is.Empty, "steady state adds nothing");

            timeline.SetVisible(spawnTick + 6, id, false);
            world.mirror.SyncObservers(_viewer, timeline, spawnTick + 5);
            Assert.That(world.bridge.removedObservers, Has.Count.EqualTo(3), "hiding removes the observer immediately");

            world.bridge.Reset();
            timeline.SetVisible(spawnTick + 8, id, true);
            world.mirror.SyncObservers(_viewer, timeline, spawnTick + 7);
            Assert.That(world.bridge.observers, Is.Empty, "re-entry waits for the client to acknowledge the visible frame");
            world.mirror.SyncObservers(_viewer, timeline, spawnTick + 8);
            Assert.That(world.bridge.observers, Has.Count.EqualTo(3));

            world.bridge.Reset();
            world.mirror.RemovePlayer(_viewer);
            Assert.That(world.bridge.removedObservers, Has.Count.EqualTo(3));
            Assert.That(world.mirror.IsObserving(_viewer, id), Is.False);
        }

        [Test]
        public void PooledReuseForAnotherInstanceDespawnsTheStaleIdentitiesFirst()
        {
            using var world = new World(this, asServer: true, pooled: true);
            var first = world.Create(null);
            world.DeleteNow(first);
            world.bridge.Reset();

            var second = world.Create(null);
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(world.bridge.despawned, Is.Empty, "the delete already despawned the pooled identities");
            Assert.That(world.bridge.early, Has.Count.EqualTo(3));
            Assert.That(world.bridge.early[0].id, Is.EqualTo(new NetworkID(103)), "a reused object gets a fresh block");
        }

        private GameObject MixedPrefab()
        {
            var prefab = NewObject("mixed prefab");
            prefab.AddComponent<MirrorProbe>();
            prefab.AddComponent<NetworkIdentity>();

            var plain = NewObject("plain child");
            plain.transform.SetParent(prefab.transform, false);
            plain.AddComponent<NetworkIdentity>();

            var piece = NewObject("child piece");
            piece.transform.SetParent(prefab.transform, false);
            piece.AddComponent<MirrorProbe>();
            piece.AddComponent<NetworkIdentity>();
            return prefab;
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        internal sealed class RecordingBridge : IPredictedNetworkBridge
        {
            public readonly List<int> reservations = new();
            public readonly List<(NetworkIdentity identity, NetworkID id)> early = new();
            public readonly List<NetworkIdentity> finalized = new();
            public readonly List<NetworkIdentity> despawned = new();
            public readonly List<(NetworkIdentity identity, PlayerID player)> observers = new();
            public readonly List<(NetworkIdentity identity, PlayerID player)> removedObservers = new();
            public readonly List<PlayerID?> owners = new();
            private ulong _next = 100;

            public void Reset()
            {
                reservations.Clear(); early.Clear(); finalized.Clear(); despawned.Clear();
                observers.Clear(); removedObservers.Clear(); owners.Clear();
            }

            public NetworkID ReserveNetworkIds(int count)
            {
                reservations.Add(count);
                var first = new NetworkID(_next);
                _next += (ulong)count;
                return first;
            }

            public void EarlySpawn(NetworkIdentity identity, NetworkID id) => early.Add((identity, id));

            public void FinalizeSpawn(NetworkIdentity identity) => finalized.Add(identity);
            public void Despawn(NetworkIdentity identity) => despawned.Add(identity);
            public void AddObserver(NetworkIdentity identity, PlayerID player) => observers.Add((identity, player));
            public void RemoveObserver(NetworkIdentity identity, PlayerID player) => removedObservers.Add((identity, player));
            public void SetOwner(NetworkIdentity identity, PlayerID? owner) => owners.Add(owner);
        }

        private sealed class World : IDisposable
        {
            internal readonly PredictionManager manager;
            internal readonly PredictedHierarchy hierarchy;
            internal readonly RecordingBridge bridge = new();
            private readonly PredictedPrefabs _prefabs;
            private readonly NetworkManager _network;
            private readonly PredictedNetworkMirrorTests _owner;
            private readonly bool _asServer;

            internal PredictedNetworkMirror mirror => hierarchy.networkMirror;

            internal World(PredictedNetworkMirrorTests owner, bool asServer, bool pooled = false)
            {
                _owner = owner;
                _asServer = asServer;
                _network = owner.NewObject("network").AddComponent<NetworkManager>();
                Set(typeof(NetworkManager), _network, asServer ? "<isServer>k__BackingField" : "<isClient>k__BackingField", true);
                var ticks = new TickManager(20, _network, null, asServer);
                Set(typeof(NetworkManager), _network, asServer ? "_serverTickManager" : "_clientTickManager", ticks);

                manager = owner.NewObject(asServer ? "server" : "client").AddComponent<PredictionManager>();
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", _network);
                manager.SetIsSpawned(true, asServer);
                Set(typeof(PredictionManager), manager, "<cachedIsServer>k__BackingField", asServer);
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
                Set(typeof(PredictionManager), manager, "<tickDelta>k__BackingField", 1f / 20);
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", 20UL);
                Set(typeof(PredictionManager), manager, "<localTickInContext>k__BackingField", 20UL);
                Set(typeof(PredictionManager), manager, "_physicsProvider", default(PredictionPhysicsProvider));
                Set(typeof(PredictionManager), manager, "_updateViewMode", UpdateViewMode.None);

                _prefabs = ScriptableObject.CreateInstance<PredictedPrefabs>();
                _prefabs.prefabs.Add(new PredictedPrefab { prefab = owner.MixedPrefab(), pooled = pooled, warmupCount = 0 });
                manager.predictedPrefabs = _prefabs;

                hierarchy = manager.RegisterSystem<PredictedHierarchy>();
                Set(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", hierarchy);
                hierarchy.networkMirror.SetBridge(bridge);
            }

            /// <summary>Deletes are deferred to the next simulate; run that step directly.</summary>
            internal void DeleteNow(PredictedObjectID id)
            {
                hierarchy.Delete(id);
                typeof(PredictedHierarchy).GetMethod("DeleteNow", Instance)!.Invoke(hierarchy, new object[] { id });
            }

            internal PredictedObjectID Create(PlayerID? instanceOwner)
            {
                var id = hierarchy.Create(0, Vector3.zero, Quaternion.identity, instanceOwner);
                Assert.That(id.HasValue, Is.True);
                return id.Value;
            }

            /// <summary>Client only: makes a topology with (optionally) one mixed instance the latest verified state.</summary>
            internal void ApplyVerified(ulong tick, PredictedObjectID? instance, NetworkID block)
            {
                Assert.That(_asServer, Is.False);
                var records = DisposableList<InstanceDetails>.Create(2);
                if (instance.HasValue)
                {
                    var root = instance.Value;
                    records.Add(new InstanceDetails(0, 0, root, Vector3.zero, Quaternion.identity, null, null, block));
                    records.Add(new InstanceDetails(0, 1, new PredictedObjectID(root.instanceId.value + 1), Vector3.zero, Quaternion.identity, null, null, new NetworkID(block, 2)));
                }

                var state = new PredictedHierarchyState(records, DisposableList<PredictedObjectID>.Create(0), 60);
                hierarchy.ApplyHistoricalTopology(state);
                state.Dispose();
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", tick);
                hierarchy.RefreshVerifiedFromLive(tick);
            }

            public void Dispose()
            {
                hierarchy.Cleanup();
                var systems = (List<PredictedIdentity>)typeof(PredictionManager).GetField("_systems", Instance)!.GetValue(manager);
                foreach (var identity in systems)
                    identity.ReleasePredictionStateForPool();
                systems.Clear();
                ((Dictionary<PredictedComponentID, PredictedIdentity>)typeof(PredictionManager).GetField("_instanceMap", Instance)!.GetValue(manager)).Clear();
                Set(typeof(PredictionManager), manager, "_systemsCount", 0);
                Set(typeof(PredictionManager), manager, "<hierarchy>k__BackingField", null);
                typeof(PredictionManager).GetMethod("CleanupAllSystems", Instance)!.Invoke(manager, null);
                manager.SetIsSpawned(false, true);
                manager.SetIsSpawned(false, false);
                var pools = typeof(PredictionManager).GetField("_pools", Instance)!.GetValue(manager) as IDisposable;
                pools?.Dispose();
                UnityEngine.Object.DestroyImmediate(_prefabs);
            }

            private static void Set(Type type, object target, string name, object value)
            {
                var field = type.GetField(name, Instance);
                Assert.That(field, Is.Not.Null, $"missing field {type.Name}.{name}");
                field.SetValue(target, value);
            }
        }
    }

    public struct MirrorProbeState : IPredictedData<MirrorProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class MirrorProbe : PredictedIdentity<MirrorProbeState>
    {
    }
}
