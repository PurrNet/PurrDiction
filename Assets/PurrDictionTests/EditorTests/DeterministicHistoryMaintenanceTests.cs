using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class DeterministicHistoryMaintenanceTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(DeterministicHistoryProbeState));
            Packer<DeterministicHistoryProbeState>.RegisterWriter(
                (packer, state) => Packer<int>.Write(packer, state.value));
            Packer<DeterministicHistoryProbeState>.RegisterReader(
                (BitPacker packer, ref DeterministicHistoryProbeState state) =>
                    state.value = Packer<int>.Read(packer));
        }

        [Test]
        public void UnchangedDeterministicSnapshotsRemainAtEveryTickAcrossRollbackAndResimulation()
        {
            using var fixture = new Fixture();
            var identity = fixture.identity;
            var history = fixture.history;

            // Deterministic simulation state is reconstructed locally, not replaced by a
            // state delta every frame. The old equality-skip regression removed these ticks.
            identity.fullPredictedState = FullState(7);
            for (ulong tick = 10; tick <= 14; tick++)
                identity.SaveStateInHistory(tick);
            for (ulong tick = 10; tick <= 14; tick++)
            {
                Assert.That(history.Read(tick, out var state), Is.True,
                    $"unchanged deterministic state must still have an exact snapshot at tick {tick}");
                Assert.That(state.state.value, Is.EqualTo(7));
            }

            identity.ClearFuture(12);
            Assert.That(history.Read(12, out _), Is.True);
            Assert.That(history.Read(13, out _), Is.False);
            Assert.That(history.Read(14, out _), Is.False);
            identity.currentState.value = 999;
            identity.Rollback(12);
            Assert.That(identity.currentState.value, Is.EqualTo(7));

            identity.increment = 3;
            identity.SimulateTick(12, 0.05f);
            identity.SaveStateInHistory(13);
            identity.SimulateTick(13, 0.05f);
            identity.SaveStateInHistory(14);
            Assert.That(history.Read(12, out var before), Is.True);
            Assert.That(before.state.value, Is.EqualTo(7));
            Assert.That(history.Read(13, out var first), Is.True);
            Assert.That(first.state.value, Is.EqualTo(10));
            Assert.That(history.Read(14, out var second), Is.True);
            Assert.That(second.state.value, Is.EqualTo(13));
            identity.Rollback(13);
            Assert.That(identity.currentState.value, Is.EqualTo(10));
        }

        [Test]
        public void RegisteredDeterministicMetadataAndOwnedSimulationHistoryAreExcludedFromMaintenance()
        {
            using var fixture = new Fixture();
            var metadata = fixture.SeedMetadata();
            fixture.history.Write(2, FullState(20));
            fixture.history.Write(8, FullState(80));
            fixture.history.Write(10, FullState(100));
            var ownedTicks = EntryTicks(fixture.history);
            var ownedBacking = BackingArray(fixture.history);
            var metadataBacking = BackingArray(metadata);
            fixture.Floor(9);

            fixture.Maintain();

            Assert.That(fixture.identity.isDeterministic, Is.True);
            Assert.That(fixture.manager.verifiedHistoryMaintenanceInspectionsTotal, Is.GreaterThan(0));
            Assert.That(fixture.manager.verifiedHistoryMaintainedStoresTotal, Is.Zero);
            Assert.That(EntryTicks(metadata), Is.EqualTo(new ulong[] { 1, 8, 10 }));
            Assert.That(BackingArray(metadata), Is.SameAs(metadataBacking));
            Assert.That(EntryTicks(fixture.history), Is.EqualTo(ownedTicks));
            Assert.That(BackingArray(fixture.history), Is.SameAs(ownedBacking));
            fixture.identity.Rollback(8);
            Assert.That(fixture.identity.currentState.value, Is.EqualTo(80));
        }

        [Test]
        public void OrphanMetadataAnchorSurvivesReplacementRebindAndRealDeterministicReads()
        {
            using var fixture = new Fixture();
            var metadata = fixture.SeedMetadata();
            fixture.history.Write(11, FullState(511));
            fixture.history.Write(12, FullState(512));
            var oldOwnedTicks = EntryTicks(fixture.history);
            var oldOwnedBacking = BackingArray(fixture.history);
            int eagerMetadataCapacity = BackingArray(metadata).Length;
            fixture.Detach(fixture.identity);
            fixture.Floor(9); // non-exact floor: retain metadata at 8, not just entries >= 9

            fixture.Maintain();

            Assert.That(fixture.manager.verifiedHistoryPrunedEntriesTotal, Is.EqualTo(1));
            Assert.That(EntryTicks(metadata), Is.EqualTo(new ulong[] { 8, 10 }));
            Assert.That(BackingArray(metadata).Length, Is.EqualTo(2));
            Assert.That(EntryTicks(fixture.history), Is.EqualTo(oldOwnedTicks),
                "unregistering does not make the separate deterministic simulation history a verified store");
            Assert.That(BackingArray(fixture.history), Is.SameAs(oldOwnedBacking));

            // A different component with the same logical ID has its own simulation history.
            // The first real metadata read must lazily rebind the retained shared ledger.
            var replacement = fixture.CreateReplacement(out var replacementHistory);
            replacementHistory.Write(11, FullState(111));
            replacementHistory.Write(12, FullState(112));
            var ownerC = Owner(30);
            var currentMetadata = new PredictedIdentityState
            {
                owner = ownerC,
                wasOnSimulationStartCalled = true
            };
            Assert.That(metadata.Read(8, out var baseline), Is.True);
            using (var payload = BitPackerPool.Get())
            {
                Packer<bool>.Write(payload, true);
                DeltaPacker<PredictedIdentityState>.Write(payload, baseline, currentMetadata);
                payload.ResetPositionAndMode(true);
                replacement.ReadState(11, payload, 9, 11);
            }
            Assert.That(Get<History<PredictedIdentityState>>(typeof(PredictedIdentity), replacement,
                "_metadataVerified"), Is.SameAs(metadata));
            Assert.That(BackingArray(metadata).Length, Is.EqualTo(eagerMetadataCapacity));
            replacement.Rollback(11);
            Assert.That(replacement.owner, Is.EqualTo(ownerC));
            Assert.That(replacement.currentState.value, Is.EqualTo(111),
                "a metadata delta must preserve this component's locally reconstructed deterministic state");
            Assert.That(replacementHistory.Read(11, out var at11), Is.True);
            Assert.That(at11.prediction.owner, Is.EqualTo(ownerC));
            Assert.That(at11.state.value, Is.EqualTo(111));

            // A subsequent unchanged record can still reference the same acknowledged
            // baseline, even though newer metadata at 10/11 has a different owner.
            replacement.ReadUnchangedState(12, 9, 12);
            replacement.Rollback(12);
            Assert.That(replacement.owner, Is.EqualTo(Owner(10)));
            Assert.That(replacement.currentState.value, Is.EqualTo(112));
            Assert.That(replacementHistory.Read(12, out var at12), Is.True);
            Assert.That(at12.prediction.owner, Is.EqualTo(Owner(10)));
            Assert.That(at12.state.value, Is.EqualTo(112));
            Assert.That(metadata.Read(12, out var verifiedMetadata), Is.True);
            Assert.That(verifiedMetadata.owner, Is.EqualTo(Owner(10)));

            fixture.manager.UnregisterInstance(fixture.identity);
            Assert.That(fixture.manager.GetIdentity(replacement.id), Is.SameAs(replacement),
                "late teardown of the old component must not unregister the replacement");
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _networkObject = new("Deterministic history network");
            private readonly GameObject _managerObject = new("Deterministic history manager");
            private readonly List<GameObject> _identityObjects = new();
            private readonly NetworkManager _network;
            public readonly PredictionManager manager;
            public readonly DeterministicHistoryProbe identity;
            public readonly History<FULL_STATE<DeterministicHistoryProbeState>> history;
            private readonly PredictedComponentID _id = new(new PredictedObjectID(2201), 0);

            public Fixture()
            {
                _network = _networkObject.AddComponent<NetworkManager>();
                manager = _managerObject.AddComponent<PredictionManager>();
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", true);
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", _network);
                Set(typeof(NetworkIdentity), manager, "_isSpawnedClient", true);
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
                identity = CreateReplacement(out history);
            }

            public DeterministicHistoryProbe CreateReplacement(
                out History<FULL_STATE<DeterministicHistoryProbeState>> owned)
            {
                var go = new GameObject("Deterministic history component");
                _identityObjects.Add(go);
                var probe = go.AddComponent<DeterministicHistoryProbe>();
                probe.Attach(manager, _id);
                probe.fullPredictedState = FullState(7);
                owned = new History<FULL_STATE<DeterministicHistoryProbeState>>(200);
                owned.Write(0, FullState(7));
                Set(typeof(DeterministicIdentity<DeterministicHistoryProbeState>), probe, "_stateHistory", owned);
                var systems = Get<List<PredictedIdentity>>(typeof(PredictionManager), manager, "_systems");
                systems.Add(probe);
                Set(typeof(PredictionManager), manager, "_systemsCount", systems.Count);
                Get<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    typeof(PredictionManager), manager, "_instanceMap").Add(_id, probe);
                return probe;
            }

            public History<PredictedIdentityState> SeedMetadata()
            {
                var metadata = manager.GetVerifiedHistory<PredictedIdentityState>(_id, out _);
                metadata.Write(1, new PredictedIdentityState { owner = Owner(1) });
                metadata.Write(8, new PredictedIdentityState { owner = Owner(10) });
                metadata.Write(10, new PredictedIdentityState { owner = Owner(20) });
                return metadata;
            }

            public void Floor(ulong tick) => Set(typeof(PredictionManager), manager, "_verifiedHistoryBaselineFloor", tick);
            public void Detach(DeterministicHistoryProbe probe) => manager.UnregisterInstance(probe);
            public void Maintain() => typeof(PredictionManager).GetMethod("MaintainVerifiedStoreStorage", Fields).Invoke(manager, null);

            public void Dispose()
            {
                Set(typeof(NetworkIdentity), manager, "_isSpawnedClient", false);
                Set(typeof(NetworkManager), _network, "<isClient>k__BackingField", false);
                foreach (var go in _identityObjects)
                    Object.DestroyImmediate(go);
                typeof(PredictionManager).GetMethod("ClearVerifiedStores", Fields).Invoke(manager, null);
                Object.DestroyImmediate(_managerObject);
                Object.DestroyImmediate(_networkObject);
            }
        }

        private static PlayerID Owner(ulong value) => new(new PackedULong(value), false);
        private static FULL_STATE<DeterministicHistoryProbeState> FullState(int value)
        {
            var full = new FULL_STATE<DeterministicHistoryProbeState>
            {
                state = new DeterministicHistoryProbeState { value = value }
            };
            full.prediction.wasOnSimulationStartCalled = true;
            return full;
        }
        private static ulong[] EntryTicks<T>(History<T> history) where T : struct, IDisposable
        {
            var ticks = new ulong[history.Count];
            for (int i = 0; i < ticks.Length; i++) ticks[i] = history.GetEntryTick(i);
            return ticks;
        }
        private static Array BackingArray<T>(History<T> history) where T : struct, IDisposable
            => Get<Array>(typeof(History<T>), history, "m_values");
        private static T Get<T>(Type type, object target, string name)
        {
            var field = type.GetField(name, Fields);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            return (T)field.GetValue(target);
        }
        private static void Set(Type type, object target, string name, object value)
        {
            var field = type.GetField(name, Fields);
            Assert.That(field, Is.Not.Null, $"Missing field {type.FullName}.{name}");
            field.SetValue(target, value);
        }
    }

    public struct DeterministicHistoryProbeState : IPredictedData<DeterministicHistoryProbeState>
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class DeterministicHistoryProbe : DeterministicIdentity<DeterministicHistoryProbeState>
    {
        public int increment;
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager;
            id = componentId;
            myType = GetType();
        }
        protected override void Simulate(ref DeterministicHistoryProbeState state, sfloat delta)
            => state.value += increment;
    }
}
