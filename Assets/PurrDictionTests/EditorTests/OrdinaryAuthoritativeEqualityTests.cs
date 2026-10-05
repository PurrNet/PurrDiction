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
    public sealed class OrdinaryAuthoritativeEqualityTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly PredictedIdentityState Metadata = new() { wasOnSimulationStartCalled = true };

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
                "The actual generated comparer must distinguish the tiny Vector3 correction.");
            Assert.That(Bits(previous.position.x), Is.Not.EqualTo(Bits(incoming.position.x)));
        }

        [TestCase(20UL)]
        [TestCase(22UL)]
        public void ReceivedFullStateReplacesHistoryWithTinyCorrectionAtSameOrLaterTick(ulong tick)
        {
            using var f = new Fixture();
            f.predicted.Write(20, Full(Value(0f)));
            f.verified.Write(20, Full(Value(0f)));
            f.predicted.Write(tick + 1, Full(Value(100f)));
            var incoming = Value(0.000001f);

            f.identity.RunClearFuture(tick);
            ReadFull(f.identity, tick, incoming);
            f.identity.RunRollback(tick);

            Assert.That(f.predicted.Read(tick + 1, out _), Is.False);
            AssertValue(f.identity.currentState, incoming);
            Assert.That(f.predicted.Read(tick, out var stored), Is.True);
            AssertValue(stored.state, incoming);
            Assert.That(f.verified.Read(tick, out var verified), Is.True);
            AssertValue(verified.state, incoming);
        }

        [Test]
        public void ReceivedChangedDeltaReplacesSpeculationAndVerifiedStateWithTinyCorrection()
        {
            using var f = new Fixture();
            var baseline = Value(1f, "baseline");
            var incoming = Value(0.000001f);
            f.verified.Write(8, Full(baseline));
            f.verified.Write(10, Full(Value(0f)));
            f.predicted.Write(10, Full(Value(0f)));

            ReadDelta(f.identity, 11, 8, baseline, incoming);
            f.identity.RunRollback(11);

            AssertValue(f.identity.currentState, incoming);
            Assert.That(f.verified.Read(11, out var verified), Is.True);
            AssertValue(verified.state, incoming);
        }

        [Test]
        public void LaterDeltaDecodesAgainstTheExactPreviouslyReceivedFullState()
        {
            using var f = new Fixture();
            f.predicted.Write(20, Full(Value(0f)));
            f.verified.Write(20, Full(Value(0f)));
            var acknowledged = Value(0.000001f);
            ReadFull(f.identity, 21, acknowledged);
            var next = Value(0.25f, "next");

            ReadDelta(f.identity, 22, 21, acknowledged, next);
            f.identity.RunRollback(22);

            AssertValue(f.identity.currentState, next);
            Assert.That(f.verified.ReadOrPrevious(21, out var baseline), Is.True);
            AssertValue(baseline.state, acknowledged);
        }

        [Test]
        public void ServerFullCheckpointAndFollowingCustomDeltaShareTheSameAcknowledgedBaseline()
        {
            using var sender = new Fixture(true);
            using var receiver = new Fixture(true);
            sender.verified.Write(10, Full(Value(0f)));
            sender.SetLive(11, Value(0.000001f));
            using (var checkpoint = BitPackerPool.Get())
            {
                sender.identity.WriteFirstState(11, checkpoint);
                checkpoint.ResetPositionAndMode(true);
                receiver.identity.ReadFirstState(11, checkpoint, 11);
            }

            sender.SetLive(12, Value(1f));
            TransferCurrent(sender, receiver, 12, 11);
            receiver.identity.RunRollback(12);

            AssertValue(receiver.identity.currentState, Value(1f));
        }

        [Test]
        public void DeltaAndFullRecipientsShareTheExactAcknowledgedBaseline()
        {
            using var sender = new Fixture(true);
            using var continuing = new Fixture(true);
            using var joining = new Fixture(true);
            sender.verified.Write(10, Full(Value(0f)));
            continuing.verified.Write(10, Full(Value(0f)));
            continuing.predicted.Write(10, Full(Value(0f)));
            sender.SetLive(11, Value(0.000001f));
            TransferCurrent(sender, continuing, 11, 10);
            using (var checkpoint = BitPackerPool.Get())
            {
                sender.identity.WriteFirstState(11, checkpoint);
                checkpoint.ResetPositionAndMode(true);
                joining.identity.ReadFirstState(11, checkpoint, 11);
            }

            // Both clients now acknowledge tick 11. One shared sender ledger must be a
            // valid baseline for both, including when a custom delta uses exact arithmetic.
            sender.SetLive(12, Value(1f));
            TransferCurrent(sender, continuing, 12, 11);
            TransferCurrent(sender, joining, 12, 11);
            continuing.identity.RunRollback(12);
            joining.identity.RunRollback(12);

            var continuedState = continuing.identity.currentState;
            var joinedState = joining.identity.currentState;
            AssertValue(continuedState, Value(1f));
            AssertValue(joinedState, Value(1f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnchangedOlderBaselineOverridesNewerTinyCorrection(bool omitted)
        {
            using var f = new Fixture();
            f.verified.Write(8, Full(Value(0f)));
            f.verified.Write(10, Full(Value(0.000001f)));
            f.predicted.Write(10, Full(Value(0.000001f)));

            ReadUnchanged(f.identity, 11, 8, omitted);
            f.identity.RunRollback(11);

            AssertValue(f.identity.currentState, Value(0f));
            Assert.That(f.verified.Read(11, out var authoritative), Is.True,
                "A newer intervening value requires an anchor even when its difference from the old baseline is tiny.");
            AssertValue(authoritative.state, Value(0f));
            Assert.That(f.verified.Read(10, out var intervening), Is.True);
            AssertValue(intervening.state, Value(0.000001f));
        }

        [Test]
        public void RestoreVerifiedStateCannotDiscardATinyCorrection()
        {
            using var f = new Fixture();
            f.predicted.Write(20, Full(Value(0f)));
            f.verified.Write(21, Full(Value(0.000001f)));

            Assert.That(f.identity.RestoreVerifiedState(21), Is.True);

            AssertValue(f.identity.currentState, Value(0.000001f));
            Assert.That(f.predicted.Read(21, out var restored), Is.True);
            AssertValue(restored.state, Value(0.000001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProvenUnchangedBaselineRemainsSparseUntilAnInterveningValue(bool omitted)
        {
            using var f = new Fixture();
            f.verified.Write(8, Full(Value(0f)));
            f.predicted.Write(8, Full(Value(0f)));
            for (ulong tick = 9; tick <= 60; tick++)
                ReadUnchanged(f.identity, tick, 8, omitted);
            Assert.That(f.verified.Count, Is.EqualTo(1),
                "Wire-unchanged baseline proof should not require a dense verified ledger.");
            Assert.That(f.verified.GetEntryTick(0), Is.EqualTo(8));

            ReadFull(f.identity, 61, Value(10f));
            ReadUnchanged(f.identity, 62, 8, omitted);
            ReadUnchanged(f.identity, 63, 62, omitted);

            Assert.That(f.verified.Count, Is.EqualTo(3));
            Assert.That(f.verified.Read(8, out var old), Is.True);
            AssertValue(old.state, Value(0f));
            Assert.That(f.verified.Read(61, out var middle), Is.True);
            AssertValue(middle.state, Value(10f));
            Assert.That(f.verified.ReadOrPrevious(63, out var current), Is.True);
            AssertValue(current.state, Value(0f));
        }

        [Test]
        public void ReceivedOwnedStateHasIndependentVerifiedReplayAndLiveOwnership()
        {
            using var f = new OwnershipFixture();
            f.ReadFull(20, 7);
            f.identity.RunRollback(20);
            Assert.That(f.predicted.Read(20, out var replay), Is.True);
            Assert.That(f.verified.Read(20, out var verified), Is.True);
            Assert.That(replay.state.token, Is.Not.EqualTo(verified.state.token));
            Assert.That(f.identity.currentState.token, Is.Not.EqualTo(replay.state.token));
            f.identity.currentState.value = 99;
            Assert.That(replay.state.value, Is.EqualTo(7));
            Assert.That(verified.state.value, Is.EqualTo(7));

            // Identical incoming content also exercises replacement/coalescing disposal.
            f.ReadFull(20, 7);
            Assert.That(f.identity.RestoreVerifiedState(20), Is.True);
            Assert.That(f.identity.currentState.value, Is.EqualTo(7));
            f.ReadFull(21, 8);
            f.identity.RunRollback(21);
            Assert.That(f.identity.currentState.value, Is.EqualTo(8));
        }

        [TestCase("full")]
        [TestCase("delta")]
        [TestCase("unchanged")]
        [TestCase("omitted")]
        [TestCase("restore")]
        public void ApplyingAuthorityRetiresExpiredSpeculationButPreservesItsBoundaryAnchor(string path)
        {
            using var f = new Fixture();
            f.predicted.Write(1, Full(Value(10f)));
            f.predicted.Write(4, Full(Value(20f)));
            f.predicted.Write(6, Full(Value(0f)));
            var authority = Value(0.000001f);
            f.verified.Write(6, Full(authority));

            // The 600-tick window ends at tick 5: tick 4 remains its sparse anchor.
            switch (path)
            {
                case "full": ReadFull(f.identity, 605, authority); break;
                case "delta": ReadDelta(f.identity, 605, 6, authority, authority); break;
                case "unchanged": ReadUnchanged(f.identity, 605, 6, false); break;
                case "omitted": ReadUnchanged(f.identity, 605, 6, true); break;
                case "restore": Assert.That(f.identity.RestoreVerifiedState(605), Is.True); break;
            }

            Assert.That(f.predicted.Read(1, out _), Is.False, "expired speculation must still be retired");
            Assert.That(f.predicted.Read(4, out var anchor), Is.True);
            AssertValue(anchor.state, Value(20f));
            Assert.That(f.predicted.Read(6, out _), Is.True);
            Assert.That(f.predicted.Read(605, out var received), Is.True);
            AssertValue(received.state, authority);
        }

        private static void ReadFull(OrdinaryGeneratedEqualityProbe identity, ulong tick,
            DeterministicGeneratedEqualityState incoming)
        {
            using var payload = BitPackerPool.Get();
            Packer<PredictedIdentityState>.Write(payload, Metadata);
            Packer<DeterministicGeneratedEqualityState>.Write(payload, incoming);
            payload.ResetPositionAndMode(true);
            PredictedIdentityState decodedMetadata = default;
            DeterministicGeneratedEqualityState decoded = default;
            Packer<PredictedIdentityState>.Read(payload, ref decodedMetadata);
            Packer<DeterministicGeneratedEqualityState>.Read(payload, ref decoded);
            AssertValue(decoded, incoming); // Prove the bytes preserve this small correction.
            decoded.Dispose();
            decodedMetadata.Dispose();
            // Entering states travel as deltas against the identity's initial state.
            payload.ResetPositionAndMode(false);
            DeltaPacker<PredictedIdentityState>.Write(payload, default, Metadata);
            DeltaPacker<DeterministicGeneratedEqualityState>.Write(payload, default, incoming);
            payload.ResetPositionAndMode(true);
            identity.ReadFirstState(tick, payload, tick);
        }

        private static void ReadDelta(OrdinaryGeneratedEqualityProbe identity, ulong tick, ulong baselineTick,
            DeterministicGeneratedEqualityState baseline, DeterministicGeneratedEqualityState incoming)
        {
            using var payload = BitPackerPool.Get();
            Packer<bool>.Write(payload, true);
            DeltaPacker<PredictedIdentityState>.Write(payload, Metadata, Metadata);
            DeltaPacker<DeterministicGeneratedEqualityState>.Write(payload, baseline, incoming);
            payload.ResetPositionAndMode(true);
            Assert.That(Packer<bool>.Read(payload), Is.True);
            PredictedIdentityState decodedMetadata = default;
            DeterministicGeneratedEqualityState decoded = default;
            DeltaPacker<PredictedIdentityState>.Read(payload, Metadata, ref decodedMetadata);
            DeltaPacker<DeterministicGeneratedEqualityState>.Read(payload, baseline, ref decoded);
            AssertValue(decoded, incoming);
            decoded.Dispose();
            decodedMetadata.Dispose();
            payload.ResetPositionAndMode(true);
            identity.RunClearFuture(tick);
            identity.ReadState(tick, payload, baselineTick, tick);
        }

        private static void ReadUnchanged(OrdinaryGeneratedEqualityProbe identity, ulong tick, ulong baselineTick, bool omitted)
        {
            identity.RunClearFuture(tick);
            if (omitted)
                identity.ReadUnchangedState(tick, baselineTick, tick);
            else
            {
                using var payload = BitPackerPool.Get();
                Packer<bool>.Write(payload, false);
                payload.ResetPositionAndMode(true);
                identity.ReadState(tick, payload, baselineTick, tick);
            }
        }

        private static void TransferCurrent(Fixture sender, Fixture receiver, ulong tick, ulong baselineTick)
        {
            using var payload = BitPackerPool.Get();
            sender.identity.WriteCurrentState(default, payload, baselineTick);
            payload.ResetPositionAndMode(true);
            receiver.identity.RunClearFuture(tick);
            receiver.identity.ReadState(tick, payload, baselineTick, tick);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly GameObject _managerObject = new("Ordinary authority manager");
            private readonly GameObject _identityObject = new("Ordinary authority identity");
            public readonly PredictionManager manager;
            public readonly OrdinaryGeneratedEqualityProbe identity;
            public readonly History<FULL_STATE<DeterministicGeneratedEqualityState>> predicted = new(600);
            public readonly History<FULL_STATE<DeterministicGeneratedEqualityState>> verified;

            public Fixture(bool customDelta = false)
            {
                manager = _managerObject.AddComponent<PredictionManager>();
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 60);
                identity = customDelta
                    ? _identityObject.AddComponent<OrdinaryAdditiveAuthorityProbe>()
                    : _identityObject.AddComponent<OrdinaryGeneratedEqualityProbe>();
                identity.Attach(manager, new PredictedComponentID(new PredictedObjectID(2361), 0));
                verified = manager.GetVerifiedHistory<FULL_STATE<DeterministicGeneratedEqualityState>>(identity.id, out _);
                Set(typeof(PredictedIdentity<DeterministicGeneratedEqualityState>), identity, "_stateHistory", predicted);
                Set(typeof(PredictedIdentity<DeterministicGeneratedEqualityState>), identity, "_verifiedHistory", verified);
            }

            public void SetLive(ulong tick, DeterministicGeneratedEqualityState value)
            {
                Set(typeof(PredictionManager), manager, "<localTick>k__BackingField", tick);
                identity.fullPredictedState.Dispose();
                identity.fullPredictedState = Full(value);
            }

            public void Dispose()
            {
                identity.ReleasePredictionStateForPool();
                Object.DestroyImmediate(_identityObject);
                typeof(PredictionManager).GetMethod("ClearVerifiedStores", Fields).Invoke(manager, null);
                Object.DestroyImmediate(_managerObject);
            }
        }

        private sealed class OwnershipFixture : IDisposable
        {
            private readonly GameObject _managerObject = new("Owned authority manager");
            private readonly GameObject _identityObject = new("Owned authority identity");
            private readonly PredictionManager _manager;
            private readonly PurrCopy<OrdinaryOwnedAuthorityState>.CopyDelegate _oldCopy;
            private readonly IEqualityComparer<OrdinaryOwnedAuthorityState> _oldEquality;
            private readonly Dictionary<int, int> _disposals = new();
            private int _nextToken;
            public readonly OrdinaryOwnedAuthorityProbe identity;
            public readonly History<FULL_STATE<OrdinaryOwnedAuthorityState>> predicted = new(600);
            public readonly History<FULL_STATE<OrdinaryOwnedAuthorityState>> verified;

            public OwnershipFixture()
            {
                _oldCopy = PurrCopy<OrdinaryOwnedAuthorityState>.Copy;
                _oldEquality = PurrEquality<OrdinaryOwnedAuthorityState>.Default;
                PurrCopy<OrdinaryOwnedAuthorityState>.Copy = Clone;
                PurrEquality<OrdinaryOwnedAuthorityState>.Default = new ContentsComparer();
                OrdinaryOwnedAuthorityState.onDispose = token => _disposals[token]++;
                Packer<OrdinaryOwnedAuthorityState>.RegisterWriter((packer, value) => Packer<int>.Write(packer, value.value));
                Packer<OrdinaryOwnedAuthorityState>.RegisterReader((BitPacker packer, ref OrdinaryOwnedAuthorityState value) =>
                    value = New(Packer<int>.Read(packer)));
                _manager = _managerObject.AddComponent<PredictionManager>();
                Set(typeof(PredictionManager), _manager, "<tickRate>k__BackingField", 60);
                identity = _identityObject.AddComponent<OrdinaryOwnedAuthorityProbe>();
                identity.Attach(_manager, new PredictedComponentID(new PredictedObjectID(2362), 0));
                verified = _manager.GetVerifiedHistory<FULL_STATE<OrdinaryOwnedAuthorityState>>(identity.id, out _);
                Set(typeof(PredictedIdentity<OrdinaryOwnedAuthorityState>), identity, "_stateHistory", predicted);
                Set(typeof(PredictedIdentity<OrdinaryOwnedAuthorityState>), identity, "_verifiedHistory", verified);
            }

            private OrdinaryOwnedAuthorityState New(int value)
            {
                int token = ++_nextToken;
                _disposals.Add(token, 0);
                return new OrdinaryOwnedAuthorityState { value = value, token = token };
            }
            private OrdinaryOwnedAuthorityState Clone(in OrdinaryOwnedAuthorityState source)
            {
                if (source.token == 0) return default;
                Assert.That(_disposals[source.token], Is.Zero, "a disposed owner cannot be copied");
                return New(source.value);
            }
            public void ReadFull(ulong tick, int value)
            {
                using var payload = BitPackerPool.Get();
                // Entering states travel as deltas against the identity's initial state.
                DeltaPacker<PredictedIdentityState>.Write(payload, default, Metadata);
                DeltaPacker<OrdinaryOwnedAuthorityState>.Write(payload, default, new OrdinaryOwnedAuthorityState { value = value });
                payload.ResetPositionAndMode(true);
                identity.RunClearFuture(tick);
                identity.ReadFirstState(tick, payload, tick);
            }
            public void Dispose()
            {
                try
                {
                    identity.ReleasePredictionStateForPool();
                    Object.DestroyImmediate(_identityObject);
                    typeof(PredictionManager).GetMethod("ClearVerifiedStores", Fields).Invoke(_manager, null);
                    Object.DestroyImmediate(_managerObject);
                    foreach (var pair in _disposals)
                        Assert.That(pair.Value, Is.EqualTo(1), $"owned authoritative payload {pair.Key} must be disposed exactly once");
                }
                finally
                {
                    PurrCopy<OrdinaryOwnedAuthorityState>.Copy = _oldCopy;
                    PurrEquality<OrdinaryOwnedAuthorityState>.Default = _oldEquality;
                    OrdinaryOwnedAuthorityState.onDispose = null;
                    // The probe type belongs only to this fixture; leave no reader closure retaining it.
                    Packer<OrdinaryOwnedAuthorityState>.RegisterReader((BitPacker packer, ref OrdinaryOwnedAuthorityState value) =>
                        value = new OrdinaryOwnedAuthorityState { value = Packer<int>.Read(packer) });
                }
            }
        }

        private sealed class ContentsComparer : IEqualityComparer<OrdinaryOwnedAuthorityState>
        {
            public bool Equals(OrdinaryOwnedAuthorityState a, OrdinaryOwnedAuthorityState b) => a.value == b.value;
            public int GetHashCode(OrdinaryOwnedAuthorityState state) => state.value;
        }
        private static DeterministicGeneratedEqualityState Value(float x, string label = "same")
            => new() { label = label, position = new Vector3(x, 0f, 0f) };
        private static FULL_STATE<DeterministicGeneratedEqualityState> Full(DeterministicGeneratedEqualityState state)
            => new() { state = state, prediction = Metadata };
        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
        private static void AssertValue(DeterministicGeneratedEqualityState actual, DeterministicGeneratedEqualityState expected)
        {
            Assert.That(Bits(actual.position.x), Is.EqualTo(Bits(expected.position.x)), "authoritative x bits");
            Assert.That(actual.label, Is.EqualTo(expected.label));
        }
        private static void Set(Type type, object instance, string name, object value)
        {
            var field = type.GetField(name, Fields);
            Assert.That(field, Is.Not.Null, $"Missing {type.FullName}.{name}");
            field.SetValue(instance, value);
        }
    }

    public class OrdinaryGeneratedEqualityProbe : PredictedIdentity<DeterministicGeneratedEqualityState>
    {
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        { predictionManager = manager; id = componentId; myType = GetType(); }
    }
    public sealed class OrdinaryAdditiveAuthorityProbe : OrdinaryGeneratedEqualityProbe
    {
        protected override void WriteDeltaState(BitPacker packer,
            in DeterministicGeneratedEqualityState baseline, in DeterministicGeneratedEqualityState current)
        {
            Packer<float>.Write(packer, current.position.x - baseline.position.x);
            Packer<string>.Write(packer, current.label);
        }
        protected override void ReadDeltaState(BitPacker packer,
            in DeterministicGeneratedEqualityState baseline, ref DeterministicGeneratedEqualityState state)
        {
            state = new DeterministicGeneratedEqualityState
            {
                position = new Vector3(baseline.position.x + Packer<float>.Read(packer), 0f, 0f),
                label = Packer<string>.Read(packer)
            };
        }
    }
    public struct OrdinaryOwnedAuthorityState : IPredictedData<OrdinaryOwnedAuthorityState>
    {
        public static Action<int> onDispose;
        public int value;
        public int token;
        public void Dispose() { if (token != 0) onDispose?.Invoke(token); }
    }
    public sealed class OrdinaryOwnedAuthorityProbe : PredictedIdentity<OrdinaryOwnedAuthorityState>
    {
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        { predictionManager = manager; id = componentId; myType = GetType(); }
    }
}
