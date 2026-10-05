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
    public sealed class DeterministicMetadataOwnershipTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(MetadataOwnedCollectionState));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ExactMetadataApplyRetainsCollectionOwnershipAndEverySnapshot(bool omitted, bool changed)
        {
            using var f = new Fixture();
            // Real deterministic saves must still clone each unchanged tick independently.
            for (ulong tick = 10; tick <= 12; tick++) f.identity.SaveStateInHistory(tick);
            Assert.That(f.copies, Is.EqualTo(3));
            var before = new FULL_STATE<MetadataOwnedCollectionState>[3];
            for (ulong tick = 10; tick <= 12; tick++)
                Assert.That(f.history.Read(tick, out before[tick - 10]), Is.True);
            Assert.That(before[0].state.values.list, Is.Not.SameAs(before[1].state.values.list));
            Assert.That(before[1].state.values.list, Is.Not.SameAs(before[2].state.values.list));
            var liveBacking = f.identity.currentState.values.list;
            f.ResetPhaseCounts();

            f.Apply(11, changed ? Owner(20) : Owner(10), omitted);

            Assert.That(f.copies, Is.Zero, "updating metadata of an existing snapshot must not clone its owned state");
            Assert.That(f.disposals, Is.Zero, "the original snapshot still owns the same collection");
            Assert.That(f.history.Count, Is.EqualTo(3));
            for (ulong tick = 10; tick <= 12; tick++)
            {
                Assert.That(f.history.Read(tick, out var after), Is.True);
                var prior = before[tick - 10];
                Assert.That(after.state.ownershipId, Is.EqualTo(prior.state.ownershipId));
                Assert.That(after.state.values.list, Is.SameAs(prior.state.values.list));
                Assert.That(after.state.values, Is.EqualTo(new[] { 17, 23 }));
                Assert.That(after.prediction.owner, Is.EqualTo(tick == 11 && changed ? Owner(20) : Owner(10)));
            }
            Assert.That(f.identity.currentState.values.list, Is.SameAs(liveBacking));
            Assert.That(f.identity.owner, Is.EqualTo(Owner(10)), "historical apply must not immediately change the live owner");

            f.identity.Rollback(11);
            Assert.That(f.identity.owner, Is.EqualTo(changed ? Owner(20) : Owner(10)));
            var live = f.identity.currentState.values;
            live[0] = 999;
            Assert.That(f.history.Read(11, out var saved), Is.True);
            Assert.That(saved.state.values[0], Is.EqualTo(17), "rollback must retain independent live-state ownership");
        }

        [Test]
        public void DeterministicInputSubclassUsesTheSameExactMetadataOwnershipPath()
        {
            using var f = new Fixture(withInput: true);
            f.identity.SaveStateInHistory(11);
            Assert.That(f.history.Read(11, out var before), Is.True);
            f.ResetPhaseCounts();
            f.Apply(11, Owner(20), omitted: true);
            Assert.That(f.identity.hasInput, Is.True);
            Assert.That(f.copies, Is.Zero);
            Assert.That(f.disposals, Is.Zero);
            Assert.That(f.history.Read(11, out var after), Is.True);
            Assert.That(after.state.values.list, Is.SameAs(before.state.values.list));
            Assert.That(after.prediction.owner, Is.EqualTo(Owner(20)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingTickClonesPreviousSnapshotWithoutAliasingEitherNeighbor(bool omitted)
        {
            using var f = new Fixture();
            var previous = f.Write(10, 10);
            var later = f.Write(12, 12);
            f.ResetPhaseCounts();

            f.Apply(11, Owner(20), omitted);

            Assert.That(f.copies, Is.EqualTo(1), "a new historical slot needs its own state ownership");
            Assert.That(f.disposals, Is.Zero);
            Assert.That(f.history.Count, Is.EqualTo(3));
            Assert.That(f.history.Read(11, out var inserted), Is.True);
            Assert.That(inserted.state.values, Is.EqualTo(new[] { 10 }));
            Assert.That(inserted.state.values.list, Is.Not.SameAs(previous.state.values.list));
            Assert.That(inserted.state.values.list, Is.Not.SameAs(later.state.values.list));
            Assert.That(inserted.prediction.owner, Is.EqualTo(Owner(20)));
            var insertedValues = inserted.state.values;
            insertedValues[0] = 99;
            Assert.That(previous.state.values[0], Is.EqualTo(10));
            Assert.That(later.state.values[0], Is.EqualTo(12));
            Assert.That(previous.prediction.owner, Is.EqualTo(Owner(10)));
            f.identity.ClearFuture(11);
            Assert.That(later.state.values.isDisposed, Is.True);
            Assert.That(previous.state.values.isDisposed, Is.False);
            Assert.That(inserted.state.values.isDisposed, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingTickMaterializesIndependentSnapshotEvenWhenMetadataAndStateCompareEqual(bool omitted)
        {
            using var f = new Fixture();
            var previous = f.Write(10, 17, 23);
            f.ResetPhaseCounts();
            f.Apply(11, Owner(10), omitted);
            Assert.That(f.history.Count, Is.EqualTo(2));
            Assert.That(f.history.Read(11, out var inserted), Is.True,
                "equivalent state contents must not prevent materializing the requested deterministic tick");
            Assert.That(inserted.state.values.list, Is.Not.SameAs(previous.state.values.list));
            Assert.That(inserted.state.values, Is.EqualTo(new[] { 17, 23 }));
            Assert.That(inserted.prediction.owner, Is.EqualTo(Owner(10)));
            Assert.That(f.history.Read(10, out var retained), Is.True);
            Assert.That(retained.state.values.list, Is.SameAs(previous.state.values.list));
            Assert.That(retained.state.values.isDisposed, Is.False);
            Assert.That(retained.state.values, Is.EqualTo(new[] { 17, 23 }));
            var insertedValues = inserted.state.values;
            insertedValues[0] = 99;
            Assert.That(retained.state.values[0], Is.EqualTo(17));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WrappedHistoricalUpdatePrunesExpiredEntryWithoutCopyingTheRetainedSnapshot(bool omitted)
        {
            using var f = new Fixture(capacity: 4);
            for (ulong tick = 0; tick <= 46; tick++) f.Write(tick, (int)tick);
            Assert.That(Get<int>(typeof(History<FULL_STATE<MetadataOwnedCollectionState>>), f.history, "m_head"), Is.GreaterThan(0));
            Assert.That(f.history.Read(40, out var expired), Is.True);
            var retained = new FULL_STATE<MetadataOwnedCollectionState>[6];
            for (ulong tick = 41; tick <= 46; tick++)
                Assert.That(f.history.Read(tick, out retained[tick - 41]), Is.True);
            f.ResetPhaseCounts();

            f.Apply(45, Owner(20), omitted);

            Assert.That(f.copies, Is.Zero);
            Assert.That(f.disposals, Is.EqualTo(1), "only tick 40 should be removed by the normal four-tick window");
            Assert.That(expired.state.values.isDisposed, Is.True);
            Assert.That(f.history.Read(40, out _), Is.False);
            Assert.That(f.history.Count, Is.EqualTo(6));
            for (ulong tick = 41; tick <= 46; tick++)
            {
                Assert.That(f.history.Read(tick, out var after), Is.True);
                Assert.That(after.state.values.list, Is.SameAs(retained[tick - 41].state.values.list));
                Assert.That(after.state.values[0], Is.EqualTo((int)tick));
                Assert.That(after.prediction.owner, Is.EqualTo(tick == 45 ? Owner(20) : Owner(10)));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NoHistoricalPredecessorUpdatesOnlyLiveMetadata(bool omitted)
        {
            using var f = new Fixture();
            var future = f.Write(12, 12);
            var liveBacking = f.identity.currentState.values.list;
            f.ResetPhaseCounts();
            f.Apply(11, Owner(20), omitted);
            Assert.That(f.copies, Is.Zero);
            Assert.That(f.disposals, Is.Zero);
            Assert.That(f.identity.owner, Is.EqualTo(Owner(20)));
            Assert.That(f.identity.currentState.values.list, Is.SameAs(liveBacking));
            Assert.That(f.history.Count, Is.EqualTo(1));
            Assert.That(f.history.Read(12, out var after), Is.True);
            Assert.That(after.state.values.list, Is.SameAs(future.state.values.list));
            Assert.That(after.prediction.owner, Is.EqualTo(Owner(10)));
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _managerObject = new("Metadata ownership manager");
            private readonly GameObject _identityObject = new("Metadata ownership identity");
            private readonly PredictionManager _manager;
            private readonly History<PredictedIdentityState> _metadata;
            private readonly PurrCopy<MetadataOwnedCollectionState>.CopyDelegate _previousCopy;
            private readonly IEqualityComparer<MetadataOwnedCollectionState> _previousEquality;
            private readonly Action<int> _previousDispose;
            private readonly Dictionary<int, int> _disposeCounts = new();
            private readonly Dictionary<int, DisposableList<int>> _collections = new();
            private int _nextId;
            public int copies;
            public int disposals;
            public readonly DeterministicIdentity<MetadataOwnedCollectionState> identity;
            public readonly History<FULL_STATE<MetadataOwnedCollectionState>> history;

            public Fixture(int capacity = 200, bool withInput = false)
            {
                _previousCopy = PurrCopy<MetadataOwnedCollectionState>.Copy;
                _previousEquality = PurrEquality<MetadataOwnedCollectionState>.Default;
                _previousDispose = MetadataOwnedCollectionState.onDispose;
                PurrCopy<MetadataOwnedCollectionState>.Copy = Clone;
                PurrEquality<MetadataOwnedCollectionState>.Default = new StateContentsComparer();
                MetadataOwnedCollectionState.onDispose = id =>
                {
                    disposals++;
                    _disposeCounts[id]++;
                };
                _manager = _managerObject.AddComponent<PredictionManager>();
                Set(typeof(PredictionManager), _manager, "<tickRate>k__BackingField", 20);
                var id = new PredictedComponentID(new PredictedObjectID(2401), 0);
                if (withInput)
                {
                    var probe = _identityObject.AddComponent<MetadataOwnedInputProbe>();
                    probe.Attach(_manager, id);
                    identity = probe;
                }
                else
                {
                    var probe = _identityObject.AddComponent<MetadataOwnedProbe>();
                    probe.Attach(_manager, id);
                    identity = probe;
                }
                identity.fullPredictedState = Full(NewState(new[] { 17, 23 }));
                identity.SetOwner(Owner(10));
                history = new History<FULL_STATE<MetadataOwnedCollectionState>>(capacity);
                Set(typeof(DeterministicIdentity<MetadataOwnedCollectionState>), identity, "_stateHistory", history);
                _metadata = _manager.GetVerifiedHistory<PredictedIdentityState>(id, out _);
            }

            private MetadataOwnedCollectionState Clone(in MetadataOwnedCollectionState source)
            {
                copies++;
                Assert.That(source.values.isDisposed, Is.False, "cannot copy a disposed historical collection");
                return NewState(source.values.list);
            }
            private MetadataOwnedCollectionState NewState(IList<int> values)
            {
                int id = ++_nextId;
                var list = DisposableList<int>.Create(values);
                _disposeCounts.Add(id, 0);
                _collections.Add(id, list);
                return new MetadataOwnedCollectionState { ownershipId = id, values = list };
            }
            public FULL_STATE<MetadataOwnedCollectionState> Write(ulong tick, params int[] values)
            {
                var state = Full(NewState(values));
                history.Write(tick, state);
                return state; // borrowed snapshot for assertions; history retains sole ownership
            }
            public void ResetPhaseCounts() { copies = 0; disposals = 0; }
            public void Apply(ulong tick, PlayerID owner, bool omitted)
            {
                var initial = new PredictedIdentityState { owner = Owner(10), wasOnSimulationStartCalled = true };
                var incoming = new PredictedIdentityState { owner = owner, wasOnSimulationStartCalled = true };
                _metadata.Write(8, omitted ? incoming : initial);
                if (omitted)
                    identity.ReadUnchangedState(tick, 8, tick);
                else
                {
                    using var payload = BitPackerPool.Get();
                    bool changed = owner != Owner(10);
                    Packer<bool>.Write(payload, changed);
                    if (changed) DeltaPacker<PredictedIdentityState>.Write(payload, initial, incoming);
                    payload.ResetPositionAndMode(true);
                    identity.ReadState(tick, payload, 8, tick);
                }
            }
            public void Dispose()
            {
                try
                {
                    // Editor-only probes are seeded without the normal Unity lifecycle.
                    // Exercise the production release path explicitly before destroying them.
                    identity.ReleasePredictionStateForPool();
                    Object.DestroyImmediate(_identityObject);
                    typeof(PredictionManager).GetMethod("ClearVerifiedStores", Fields).Invoke(_manager, null);
                    Object.DestroyImmediate(_managerObject);
                    foreach (var pair in _disposeCounts)
                    {
                        Assert.That(pair.Value, Is.EqualTo(1), $"collection owner {pair.Key} must be disposed exactly once");
                        Assert.That(_collections[pair.Key].isDisposed, Is.True);
                    }
                }
                finally
                {
                    PurrCopy<MetadataOwnedCollectionState>.Copy = _previousCopy;
                    PurrEquality<MetadataOwnedCollectionState>.Default = _previousEquality;
                    MetadataOwnedCollectionState.onDispose = _previousDispose;
                }
            }
        }

        private sealed class StateContentsComparer : IEqualityComparer<MetadataOwnedCollectionState>
        {
            public bool Equals(MetadataOwnedCollectionState a, MetadataOwnedCollectionState b)
            {
                if (a.values.isDisposed || b.values.isDisposed)
                    return a.values.isDisposed == b.values.isDisposed;
                if (a.values.Count != b.values.Count) return false;
                for (int i = 0; i < a.values.Count; i++) if (a.values[i] != b.values[i]) return false;
                return true;
            }
            public int GetHashCode(MetadataOwnedCollectionState value) => 0;
        }

        private static PlayerID Owner(ulong value) => new(new PackedULong(value), false);
        private static FULL_STATE<MetadataOwnedCollectionState> Full(MetadataOwnedCollectionState state)
            => new() { state = state, prediction = new PredictedIdentityState { owner = Owner(10), wasOnSimulationStartCalled = true } };
        private static T Get<T>(Type type, object target, string field) => (T)type.GetField(field, Fields).GetValue(target);
        private static void Set(Type type, object target, string field, object value) => type.GetField(field, Fields).SetValue(target, value);
    }

    public struct MetadataOwnedCollectionState : IPredictedData<MetadataOwnedCollectionState>
    {
        public static Action<int> onDispose;
        public int ownershipId;
        public DisposableList<int> values;
        public void Dispose()
        {
            if (ownershipId != 0) onDispose?.Invoke(ownershipId);
            values.Dispose();
        }
    }
    public sealed class MetadataOwnedProbe : DeterministicIdentity<MetadataOwnedCollectionState>
    {
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        { predictionManager = manager; id = componentId; myType = GetType(); }
    }
    public struct MetadataOwnedInput : IPredictedData { public void Dispose() { } }
    public sealed class MetadataOwnedInputProbe : DeterministicIdentity<MetadataOwnedInput, MetadataOwnedCollectionState>
    {
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        { predictionManager = manager; id = componentId; myType = GetType(); }
        protected override void Simulate(MetadataOwnedInput input, ref MetadataOwnedCollectionState state, sfloat delta) { }
    }
}
