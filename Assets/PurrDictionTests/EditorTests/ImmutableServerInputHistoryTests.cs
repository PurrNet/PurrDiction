using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ImmutableServerInputHistoryTests
    {
        private const BindingFlags Members =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(ImmutableHistoryInput));
            Hasher.PrepareType(typeof(EmptyState));
            Hasher.PrepareType(typeof(ImmutableHistoryInputProbe));
            Packer<ImmutableHistoryInput>.RegisterWriter((packer, input) =>
            {
                if (input.value == int.MinValue)
                    throw new InvalidOperationException("Test input serializer failure");
                Packer<int>.Write(packer, input.value);
            });
            Packer<ImmutableHistoryInput>.RegisterReader(
                (BitPacker packer, ref ImmutableHistoryInput input) =>
                    input.value = Packer<int>.Read(packer));
        }

        [Test]
        public void RemovedIdentityRetainsOriginalInputBytesWithoutObservers()
        {
            using var fixture = new Fixture();
            var input = fixture.Add(710);
            fixture.Seed(input, 10, 123);
            fixture.Capture(10);
            var before = fixture.Snapshot(10);
            fixture.manager.UnregisterInstance(input);
            input.ReleasePredictionStateForPool();
            Assert.That(fixture.manager.inputHistorySystems, Is.Zero);
            Assert.That(fixture.Snapshot(10), Is.EqualTo(before));
            fixture.AssertInput(10, 710, 123);

            fixture.Capture(11);
            Assert.That(fixture.EntryCount(11), Is.Zero,
                "the removal changes future ticks, not the already captured tick");
            fixture.AssertInput(10, 710, 123);
        }

        [Test]
        public void ReusingOneObjectWithAnotherIdCannotRewritePastInputOrVisibilityRoot()
        {
            using var fixture = new Fixture();
            var input = fixture.Add(720);
            fixture.Seed(input, 10, 321);
            fixture.Capture(10);
            var before = fixture.Snapshot(10);
            fixture.manager.UnregisterInstance(input);
            input.ReleasePredictionStateForPool();
            fixture.Register(input, 999);
            fixture.Seed(input, 11, 456);
            fixture.Capture(11);

            fixture.AssertInput(10, 720, 321);
            fixture.AssertInput(11, 999, 456);
            Assert.That(fixture.Snapshot(10), Is.EqualTo(before));
            Assert.That(fixture.FirstEntryRoot(10), Is.EqualTo(new PredictedObjectID(720)));
            Assert.That(fixture.FirstEntryRoot(11), Is.EqualTo(new PredictedObjectID(999)));
        }

        [Test]
        public void RegisteringAnIdentityAndReplacingSourceHistoryCannotMutateACapturedTick()
        {
            using var fixture = new Fixture();
            var first = fixture.Add(730);
            fixture.Seed(first, 10, 10);
            fixture.Capture(10);
            var before = fixture.Snapshot(10);
            var second = fixture.Add(731);
            fixture.Seed(first, 10, 90);
            fixture.Seed(second, 10, 91);
            fixture.Capture(10);

            Assert.That(fixture.Snapshot(10), Is.EqualTo(before));
            fixture.AssertInput(10, 730, 10);
            Assert.That(fixture.EntryCount(10), Is.EqualTo(1),
                "registering another identity cannot add it to an earlier captured roster");
        }

        [Test]
        public void LookupCannotInventATickEvenWhenLiveInputHistoryContainsIt()
        {
            using var fixture = new Fixture();
            var input = fixture.Add(740);
            fixture.Seed(input, 10, 100);
            fixture.Seed(input, 11, 110);
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(10));
            fixture.Capture(10);
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(11));
            fixture.Seed(input, 12, 120);
            fixture.Capture(12);
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(11));
            Assert.Throws<InvalidOperationException>(() => fixture.Capture(11),
                "past input values do not establish the missing historical roster");
        }

        [Test]
        public void MissingPreparedInputDoesNotPublishAnIncompleteBlock()
        {
            using var fixture = new Fixture();
            fixture.Add(750);
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Capture(10));
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(10));
            Assert.That(Get<bool>(fixture.manager, "_hasCapturedInputHistory"), Is.False);
        }

        [Test]
        public void SerializerFailurePreservesThePreviousTranscriptAndAllowsRetry()
        {
            using var fixture = new Fixture();
            var first = fixture.Add(760);
            var second = fixture.Add(761);
            fixture.Seed(first, 10, 10);
            fixture.Seed(second, 10, 20);
            fixture.Capture(10);
            var before = fixture.Snapshot(10);
            fixture.Seed(first, 11, 30);
            fixture.Seed(second, 11, int.MinValue);
            Assert.Throws<InvalidOperationException>(() => fixture.Capture(11));
            Assert.That(fixture.Snapshot(10), Is.EqualTo(before));
            Assert.That(Get<ulong>(fixture.manager, "_latestCapturedInputTick"), Is.EqualTo(10));
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(11));

            fixture.Seed(second, 11, 40);
            fixture.Capture(11);
            Assert.That(fixture.EntryCount(11), Is.EqualTo(2));
            Assert.That(fixture.Snapshot(10), Is.EqualTo(before));
        }

        [Test]
        public void RateGrowthPreservesAvailableBlocksButCannotResurrectEvictedTicks()
        {
            using var fixture = new Fixture();
            fixture.Capture(10);
            fixture.Capture(42);
            Assert.That(fixture.Capacity, Is.EqualTo(33));
            Assert.DoesNotThrow(() => fixture.Block(10));
            fixture.Capture(43);
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(10));

            fixture.SetRate(60);
            Assert.DoesNotThrow(() => fixture.Block(42));
            Assert.That(fixture.Capacity, Is.EqualTo(97));
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(10),
                "a larger ring cannot recover data that was already evicted");
            fixture.Capture(138);
            Assert.DoesNotThrow(() => fixture.Block(42));
            fixture.Capture(139);
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(42));
        }

        [Test]
        public void RateShrinkDisposesExpiredPayloadsAndKeepsTheBoundaryTick()
        {
            using var fixture = new Fixture(60);
            var input = fixture.Add(770);
            fixture.Seed(input, 10, 10);
            fixture.Capture(10);
            var expired = fixture.Block(10);
            var expiredPayload = Get<BitPacker>(expired, "packer");
            fixture.Seed(input, 68, 68);
            fixture.Capture(68);
            fixture.Seed(input, 100, 100);
            fixture.Capture(100);
            Assert.That(expiredPayload.positionInBits, Is.GreaterThan(0));

            fixture.SetRate(20);
            fixture.AssertInput(68, 770, 68);
            Assert.That(fixture.Capacity, Is.EqualTo(33));
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(10));
            Assert.That(expiredPayload.positionInBits, Is.Zero,
                "shrinking returns expired payloads to their pool");
        }

        [Test]
        public void DisposalReleasesCapturedAndScratchPayloadsAndStartsANewEpoch()
        {
            using var fixture = new Fixture();
            fixture.Capture(10);
            fixture.Capture(43);
            var current = fixture.Block(43);
            var scratch = Get<object>(fixture.manager, "_inputBlockScratch");
            var currentFrame = Get<BitPacker>(current, "packer");
            var scratchFrame = Get<BitPacker>(scratch, "packer");
            Assert.That(scratchFrame, Is.Not.Null);
            fixture.Clear();
            Assert.That(Get<object>(fixture.manager, "_inputBlockCache"), Is.Null);
            Assert.That(currentFrame.positionInBits, Is.Zero);
            Assert.That(scratchFrame.positionInBits, Is.Zero);
            Assert.That(Get<bool>(fixture.manager, "_hasCapturedInputHistory"), Is.False);
            fixture.Capture(1);
            Assert.DoesNotThrow(() => fixture.Block(1));
            Assert.Throws<MissingPredictionBaselineException>(() => fixture.Block(43));
        }

        private sealed class Fixture : IDisposable
        {
            private readonly List<GameObject> _objects = new();
            private readonly List<ImmutableHistoryInputProbe> _inputs = new();
            internal readonly PredictionManager manager;

            internal Fixture(int rate = 20)
            {
                var networkObject = Create("Immutable input history network");
                var network = networkObject.AddComponent<NetworkManager>();
                Set(typeof(NetworkManager), network, "_clientTickManager", new TickManager(rate, network, null, false));
                manager = Create("Immutable input history manager").AddComponent<PredictionManager>();
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", network);
                SetRate(rate);
                manager.SetIsSpawned(true, false);
            }

            internal ImmutableHistoryInputProbe Add(uint objectId)
            {
                var input = Create($"Immutable input {objectId}").AddComponent<ImmutableHistoryInputProbe>();
                _inputs.Add(input);
                Register(input, objectId);
                return input;
            }

            internal void Register(ImmutableHistoryInputProbe input, uint objectId)
                => manager.RegisterInstance(input.gameObject, new PredictedObjectID(objectId), null, false, false);

            internal void Seed(ImmutableHistoryInputProbe input, ulong tick, int value)
            {
                // Seed through the actual input decoder without invoking the deliberately
                // throwing writer used by the transactional capture regression.
                using var payload = BitPackerPool.Get();
                Packer<bool>.Write(payload, true);
                Packer<int>.Write(payload, value);
                payload.ResetPositionAndMode(true);
                input.ReadFirstInput(tick, payload);
            }

            internal void Capture(ulong tick) => Invoke(manager, "CaptureInputHistory", tick);
            internal object Block(ulong tick) => Invoke(manager, "GetInputBlockForTick", tick);
            internal int Capacity => ((Array)Get<object>(manager, "_inputBlockCache")).Length;
            internal int EntryCount(ulong tick) => Get<IList>(Block(tick), "entries").Count;
            internal PredictedObjectID FirstEntryRoot(ulong tick)
                => Get<PredictedObjectID>(Get<IList>(Block(tick), "entries")[0], "rootId");
            internal void SetRate(int rate) => Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", rate);
            internal void Clear() => Invoke(manager, "DisposeInputBlockCache");

            // A captured block is its payload packer plus the entry table that addresses it.
            internal byte[] Snapshot(ulong tick)
            {
                var block = Block(tick);
                var packer = Get<BitPacker>(block, "packer");
                var bytes = new List<byte>();
                for (int i = 0; i < packer.positionInBytes; i++)
                    bytes.Add(packer.buffer[i]);
                foreach (var entry in Get<IList>(block, "entries"))
                {
                    bytes.AddRange(BitConverter.GetBytes(Get<PredictedComponentID>(entry, "id").objectId.instanceId.value));
                    bytes.AddRange(BitConverter.GetBytes(Get<PredictedComponentID>(entry, "id").componentId.value));
                    bytes.AddRange(BitConverter.GetBytes(Get<int>(entry, "bitOrigin")));
                    bytes.AddRange(BitConverter.GetBytes(Get<int>(entry, "bitLength")));
                }
                return bytes.ToArray();
            }

            internal void AssertInput(ulong tick, uint objectId, int value)
            {
                var block = Block(tick);
                var packer = Get<BitPacker>(block, "packer");
                var entries = Get<IList>(block, "entries");
                Assert.That(entries.Count, Is.EqualTo(1));
                var entry = entries[0];
                Assert.That(Get<PredictedComponentID>(entry, "id"),
                    Is.EqualTo(new PredictedComponentID(new PredictedObjectID(objectId), 0)));
                int bits = Get<int>(entry, "bitLength");
                using var reader = BitPackerPool.Get();
                reader.WriteBitDataWithoutConsumingIt(new BitData(packer, Get<int>(entry, "bitOrigin"), bits));
                Assert.That(reader.positionInBits, Is.EqualTo(bits));
                reader.ResetPositionAndMode(true);
                Assert.That(Packer<bool>.Read(reader), Is.True);
                Assert.That(Packer<ImmutableHistoryInput>.Read(reader).value, Is.EqualTo(value));
                Assert.That(reader.positionInBits, Is.EqualTo(bits));
            }

            private GameObject Create(string name)
            {
                var result = new GameObject(name);
                _objects.Add(result);
                return result;
            }

            public void Dispose()
            {
                Clear();
                foreach (var input in _inputs)
                    input.ReleasePredictionStateForPool();
                for (var i = _objects.Count - 1; i >= 0; i--)
                    Object.DestroyImmediate(_objects[i]);
            }
        }

        private static T Get<T>(object target, string name)
            => (T)target.GetType().GetField(name, Members).GetValue(target);

        private static void Set(Type declaringType, object target, string name, object value)
            => declaringType.GetField(name, Members).SetValue(target, value);

        private static object Invoke(object target, string name, params object[] arguments)
        {
            try
            {
                return target.GetType().GetMethod(name, Members).Invoke(target, arguments);
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    public struct ImmutableHistoryInput : IPredictedData
    {
        public int value;
        public void Dispose() { }
    }

    public sealed class ImmutableHistoryInputProbe : PredictedIdentity<ImmutableHistoryInput, EmptyState>
    {
        protected override void Simulate(ImmutableHistoryInput input, ref EmptyState state, float delta) { }
    }
}
