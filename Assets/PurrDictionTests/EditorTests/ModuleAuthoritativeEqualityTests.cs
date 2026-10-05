using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ModuleAuthoritativeEqualityTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly ModulePredictedState Metadata = new() { wasOnSimulationStartCalled = true };

        [OneTimeSetUp]
        public void RegisterRealGeneratedCode()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(DeterministicGeneratedEqualityState));
            var previous = Value(0f);
            var incoming = Value(0.000001f);
            Assert.That(typeof(IPurrEquatable<DeterministicGeneratedEqualityState>)
                .IsAssignableFrom(typeof(DeterministicGeneratedEqualityState)), Is.True);
            Assert.That(Packer.AreEqualRef(ref previous, ref incoming), Is.False,
                "The real generated Vector3 comparer must distinguish the tiny correction.");
        }

        [TestCase(20UL)]
        [TestCase(22UL)]
        public void ReceivedFullCorrectionSurvivesRollbackAtSameOrLaterTick(ulong tick)
        {
            using var f = GeneratedFixture();
            f.predicted.Write(20, Full(Value(0f)));
            f.verified.Write(20, Full(Value(0f)));
            f.predicted.Write(tick + 1, Full(Value(100f)));

            ReadFull(f.module, tick, Value(0.000001f));
            f.module.RollbackInternal(tick);

            AssertValue(f.module.currentState, Value(0.000001f));
            Assert.That(f.predicted.Read(tick + 1, out _), Is.False);
            Assert.That(f.predicted.Read(tick, out var replay), Is.True);
            AssertValue(replay.state, Value(0.000001f));
            Assert.That(f.verified.Read(tick, out var verified), Is.True);
            AssertValue(verified.state, Value(0.000001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GeneratedTinyDeltaSurvivesReceivedStorageAndRollback(bool softCorrection)
        {
            using var f = GeneratedFixture();
            if (softCorrection)
                f.identity.SetPredictionPolicyOverride(PredictionPolicy.SoftCorrection);
            var baseline = Value(0f);
            var incoming = Value(0.000001f);
            f.predicted.Write(10, Full(baseline));
            f.verified.Write(10, Full(baseline));
            using var payload = BitPackerPool.Get();
            Packer<bool>.Write(payload, true);
            DeltaPacker<ModulePredictedState>.Write(payload, Metadata, Metadata);
            Assert.That(DeltaPacker<DeterministicGeneratedEqualityState>.Write(payload, baseline, incoming), Is.True,
                "Generated delta serialization must preserve the tiny change before module storage sees it.");
            payload.ResetPositionAndMode(true);
            Assert.That(Packer<bool>.Read(payload), Is.True);
            ModulePredictedState decodedMetadata = default;
            DeterministicGeneratedEqualityState decoded = default;
            DeltaPacker<ModulePredictedState>.Read(payload, Metadata, ref decodedMetadata);
            DeltaPacker<DeterministicGeneratedEqualityState>.Read(payload, baseline, ref decoded);
            AssertValue(decoded, incoming);
            decoded.Dispose();
            payload.ResetPositionAndMode(true);

            f.module.ReadStateInternal(11, payload, 10, 11);
            f.module.RollbackInternal(11);

            AssertValue(f.module.currentState, incoming);
            Assert.That(f.verified.Read(11, out var verified), Is.True);
            AssertValue(verified.state, incoming);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CarryingAnOlderBaselineOverridesAnInterveningTinyCorrection(bool omitted)
        {
            using var f = GeneratedFixture();
            f.verified.Write(8, Full(Value(0f)));
            f.verified.Write(10, Full(Value(0.000001f)));
            f.predicted.Write(10, Full(Value(0.000001f)));

            ReadUnchanged(f.module, 11, 8, omitted);
            f.module.RollbackInternal(11);

            AssertValue(f.module.currentState, Value(0f));
            Assert.That(f.verified.Read(11, out var verified), Is.True);
            AssertValue(verified.state, Value(0f));
            Assert.That(f.verified.Read(10, out var previous), Is.True);
            AssertValue(previous.state, Value(0.000001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProvenUnchangedBaselineKeepsSparseVerifiedCoverage(bool omitted)
        {
            using var f = GeneratedFixture();
            f.verified.Write(8, Full(Value(0f)));
            for (ulong tick = 9; tick <= 60; ++tick)
                ReadUnchanged(f.module, tick, 8, omitted);
            Assert.That(f.verified.Count, Is.EqualTo(1));

            ReadFull(f.module, 61, Value(10f));
            ReadUnchanged(f.module, 62, 8, omitted);
            ReadUnchanged(f.module, 63, 62, omitted);

            Assert.That(f.verified.Count, Is.EqualTo(3));
            f.module.RollbackInternal(63);
            AssertValue(f.module.currentState, Value(0f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SavedTinyCorrectionIsTheSameBaselineForFullAndDeltaRecipients(bool fullFirst)
        {
            using var sender = GeneratedFixture(true);
            using var continuing = GeneratedFixture();
            using var joining = GeneratedFixture();
            sender.Save(10, Value(0f));
            TransferFull(sender, continuing, 10);
            sender.Save(11, Value(0.000001f));

            if (fullFirst)
            {
                TransferFull(sender, joining, 11);
                TransferCurrent(sender, continuing, 11, 10);
            }
            else
            {
                TransferCurrent(sender, continuing, 11, 10);
                TransferFull(sender, joining, 11);
            }

            AssertValue(continuing.module.currentState, Value(0.000001f));
            AssertValue(joining.module.currentState, Value(0.000001f));
            Assert.That(sender.predicted.ReadOrPrevious(11, out var saved), Is.True);
            AssertValue(saved.state, Value(0.000001f));
            Assert.That(sender.verified.ReadOrPrevious(11, out var acknowledged), Is.True);
            AssertValue(acknowledged.state, Value(0.000001f));

            // Both recipients acknowledge tick 11, so the next generated delta must
            // reconstruct identically from the sender's one shared baseline ledger.
            sender.Save(12, Value(1f));
            TransferCurrent(sender, continuing, 12, 11);
            TransferCurrent(sender, joining, 12, 11);
            continuing.module.RollbackInternal(12);
            joining.module.RollbackInternal(12);
            AssertValue(continuing.module.currentState, Value(1f));
            AssertValue(joining.module.currentState, Value(1f));
        }

        [Test]
        public void HistoricalFullStateUsesTheExactSavedTickRatherThanCurrentLiveState()
        {
            using var sender = GeneratedFixture(true);
            using var receiver = GeneratedFixture();
            sender.Save(10, Value(0f));
            sender.Save(11, Value(0.000001f));
            sender.Save(12, Value(1f));

            TransferFull(sender, receiver, 11);
            receiver.module.RollbackInternal(11);

            AssertValue(receiver.module.currentState, Value(0.000001f));
            AssertValue(sender.module.currentState, Value(1f));
        }

        [Test]
        public void SpeculativeClientSavesPreserveTinyChangesAndCoalesceOnlyEqualValues()
        {
            using var f = GeneratedFixture();
            f.Save(10, Value(0f));
            f.Save(11, Value(0.000001f));
            Assert.That(f.predicted.Count, Is.EqualTo(2),
                "Strict generated equality must preserve tiny changes in speculative history too.");
            Assert.That(f.predicted.Read(11, out var saved), Is.True);
            AssertValue(saved.state, Value(0.000001f));
            f.Save(12, Value(0.000001f));
            Assert.That(f.predicted.Count, Is.EqualTo(2), "Identical speculative values still coalesce.");

            ReadFull(f.module, 11, Value(0.000001f));
            f.module.RollbackInternal(11);

            Assert.That(f.predicted.Count, Is.EqualTo(2));
            AssertValue(f.module.currentState, Value(0.000001f));
        }

        [Test]
        public void OwnedReceivedStateHasIndependentLiveReplayAndVerifiedLifetimes()
        {
            var oldCopy = PurrCopy<ModuleOwnedAuthorityState>.Copy;
            var disposals = new Dictionary<int, int>();
            int nextToken = 0;
            ModuleOwnedAuthorityState New(int value)
            {
                int token = ++nextToken;
                disposals.Add(token, 0);
                return new ModuleOwnedAuthorityState { value = value, token = token };
            }
            ModuleOwnedAuthorityState Clone(in ModuleOwnedAuthorityState source)
            {
                if (source.token == 0) return default;
                Assert.That(disposals[source.token], Is.Zero, "a disposed owner must never be copied");
                return New(source.value);
            }
            PurrCopy<ModuleOwnedAuthorityState>.Copy = Clone;
            ModuleOwnedAuthorityState.onDispose = token => disposals[token]++;
            Packer<ModuleOwnedAuthorityState>.RegisterWriter((packer, value) => Packer<int>.Write(packer, value.value));
            Packer<ModuleOwnedAuthorityState>.RegisterReader((BitPacker packer, ref ModuleOwnedAuthorityState value) =>
                value = New(Packer<int>.Read(packer)));
            try
            {
                using (var f = new Fixture<ModuleOwnedAuthorityState>(id => new ModuleOwnedAuthorityProbe(id)))
                {
                    ReadFull(f.module, 20, new ModuleOwnedAuthorityState { value = 7 });
                    f.module.RollbackInternal(20);
                    Assert.That(f.predicted.Read(20, out var replay), Is.True);
                    Assert.That(f.verified.Read(20, out var verified), Is.True);
                    Assert.That(replay.state.token, Is.Not.EqualTo(verified.state.token));
                    Assert.That(f.module.currentState.token, Is.Not.EqualTo(replay.state.token));
                    Assert.That(f.module.currentState.token, Is.Not.EqualTo(verified.state.token));
                    f.module.currentState.value = 99;
                    Assert.That(replay.state.value, Is.EqualTo(7));
                    Assert.That(verified.state.value, Is.EqualTo(7));
                    ReadFull(f.module, 20, new ModuleOwnedAuthorityState { value = 7 });
                    f.module.RollbackInternal(20);
                    ReadFull(f.module, 21, new ModuleOwnedAuthorityState { value = 8 });
                    f.module.RollbackInternal(21);
                    Assert.That(f.module.currentState.value, Is.EqualTo(8));
                }
                foreach (var pair in disposals)
                    Assert.That(pair.Value, Is.EqualTo(1), $"module owner {pair.Key} must be disposed once");
            }
            finally
            {
                PurrCopy<ModuleOwnedAuthorityState>.Copy = oldCopy;
                ModuleOwnedAuthorityState.onDispose = null;
                Packer<ModuleOwnedAuthorityState>.RegisterReader((BitPacker packer, ref ModuleOwnedAuthorityState value) =>
                    value = new ModuleOwnedAuthorityState { value = Packer<int>.Read(packer) });
            }
        }

        private static Fixture<DeterministicGeneratedEqualityState> GeneratedFixture(bool server = false)
            => new(id => new ModuleGeneratedAuthorityProbe(id), server);

        private static void ReadFull<T>(PredictedModule<T> module, ulong tick, T state)
            where T : struct, IPredictedData<T>
        {
            using var payload = BitPackerPool.Get();
            // Entering states travel as deltas against the module's initial state.
            DeltaPacker<ModulePredictedState>.Write(payload, default, Metadata);
            DeltaPacker<T>.Write(payload, default, state);
            payload.ResetPositionAndMode(true);
            module.ClearFutureInternal(tick);
            module.ReadFirstStateInternal(tick, payload, tick);
        }

        private static void ReadUnchanged(PredictedModule<DeterministicGeneratedEqualityState> module,
            ulong tick, ulong baselineTick, bool omitted)
        {
            module.ClearFutureInternal(tick);
            if (omitted)
                module.ReadUnchangedStateInternal(tick, baselineTick, tick);
            else
            {
                using var payload = BitPackerPool.Get();
                Packer<bool>.Write(payload, false);
                payload.ResetPositionAndMode(true);
                module.ReadStateInternal(tick, payload, baselineTick, tick);
            }
        }

        private static void TransferFull(Fixture<DeterministicGeneratedEqualityState> sender,
            Fixture<DeterministicGeneratedEqualityState> receiver, ulong tick)
        {
            using var payload = BitPackerPool.Get();
            sender.module.WriteFirstStateInternal(tick, payload);
            payload.ResetPositionAndMode(true);
            receiver.module.ReadFirstStateInternal(tick, payload, tick);
        }

        private static void TransferCurrent(Fixture<DeterministicGeneratedEqualityState> sender,
            Fixture<DeterministicGeneratedEqualityState> receiver, ulong tick, ulong baselineTick)
        {
            using var payload = BitPackerPool.Get();
            sender.module.WriteStateInternal(default, payload, baselineTick);
            payload.ResetPositionAndMode(true);
            receiver.module.ClearFutureInternal(tick);
            receiver.module.ReadStateInternal(tick, payload, baselineTick, tick);
        }

        private sealed class Fixture<T> : IDisposable where T : struct, IPredictedData<T>
        {
            private readonly GameObject _managerObject = new("Module authority manager");
            private readonly GameObject _identityObject = new("Module authority identity");
            public readonly PredictionManager manager;
            public readonly ModuleAuthorityIdentity identity;
            public readonly PredictedModule<T> module;
            public readonly History<MODULE_STATE<T>> predicted;
            public readonly History<MODULE_STATE<T>> verified;

            public Fixture(Func<PredictedIdentity, PredictedModule<T>> create, bool server = false)
            {
                manager = _managerObject.AddComponent<PredictionManager>();
                typeof(PredictionManager).GetField("<tickRate>k__BackingField", Fields).SetValue(manager, 60);
                typeof(PredictionManager).GetField("<cachedIsServer>k__BackingField", Fields).SetValue(manager, server);
                identity = _identityObject.AddComponent<ModuleAuthorityIdentity>();
                identity.Attach(manager);
                module = create(identity);
                predicted = (History<MODULE_STATE<T>>)typeof(PredictedModule<T>).GetField("_history", Fields).GetValue(module);
                verified = manager.GetVerifiedHistory<MODULE_STATE<T>>(identity.id, 1 + module.moduleIndex, out _);
            }

            public void Save(ulong tick, T state)
            {
                typeof(PredictionManager).GetField("<localTick>k__BackingField", Fields).SetValue(manager, tick);
                module.fullPredictedState.Dispose();
                module.fullPredictedState = new MODULE_STATE<T> { state = state, prediction = Metadata };
                module.SaveStateInternal(tick);
            }

            public void Dispose()
            {
                module.ReleaseStateForPoolInternal();
                Object.DestroyImmediate(_identityObject);
                typeof(PredictionManager).GetMethod("ClearVerifiedStores", Fields).Invoke(manager, null);
                Object.DestroyImmediate(_managerObject);
            }
        }

        private static DeterministicGeneratedEqualityState Value(float x)
            => new() { label = "same", position = new Vector3(x, 0f, 0f) };
        private static MODULE_STATE<DeterministicGeneratedEqualityState> Full(DeterministicGeneratedEqualityState state)
            => new() { state = state, prediction = Metadata };
        private static void AssertValue(DeterministicGeneratedEqualityState actual, DeterministicGeneratedEqualityState expected)
        {
            Assert.That(BitConverter.SingleToInt32Bits(actual.position.x),
                Is.EqualTo(BitConverter.SingleToInt32Bits(expected.position.x)), "authoritative module x bits");
            Assert.That(actual.label, Is.EqualTo(expected.label));
        }
    }

    public sealed class ModuleAuthorityIdentity : PredictedIdentity<DeterministicGeneratedEqualityState>
    {
        public override bool supportsSoftCorrection => true;
        public void Attach(PredictionManager manager)
        {
            predictionManager = manager;
            id = new PredictedComponentID(new PredictedObjectID(2363), 0);
            myType = GetType();
        }
    }
    public sealed class ModuleGeneratedAuthorityProbe : PredictedModule<DeterministicGeneratedEqualityState>
    {
        public ModuleGeneratedAuthorityProbe(PredictedIdentity identity) : base(identity) { }
    }
    public struct ModuleOwnedAuthorityState : IPredictedData<ModuleOwnedAuthorityState>
    {
        public static Action<int> onDispose;
        public int value;
        public int token;
        public void Dispose() { if (token != 0) onDispose?.Invoke(token); }
    }
    public sealed class ModuleOwnedAuthorityProbe : PredictedModule<ModuleOwnedAuthorityState>
    {
        public ModuleOwnedAuthorityProbe(PredictedIdentity identity) : base(identity) { }
    }
}
