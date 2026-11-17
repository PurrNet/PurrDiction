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

        [TestCase(0UL, false)]
        [TestCase(57UL, true)]
        [TestCase(ulong.MaxValue, true)]
        public void PackedTranscriptOwnershipPreservesNullableIdsAndRestorationRules(ulong id, bool bot)
        {
            var owner = new PlayerID(id, bot);
            var uploaded = new PredictionManager.CachedInputEntry(default, default, 3, 7, false, 11, 13, owner, true);
            Assert.That(uploaded.owner.HasValue, Is.True);
            Assert.That(uploaded.owner.Value.id.value, Is.EqualTo(id));
            Assert.That(uploaded.owner.Value.isBot, Is.EqualTo(bot));
            Assert.That(uploaded.RestoredBy(new PlayerID(id, !bot)), Is.True,
                "restoration uses PlayerID identity equality, which compares the numeric ID");
            Assert.That(uploaded.RestoredBy(new PlayerID(id == 0 ? 1UL : 0UL, false)), Is.False);
            var unowned = new PredictionManager.CachedInputEntry(default, default, 3, 7, false, 11, 13, null, true);
            Assert.That(unowned.owner.HasValue, Is.False);
            Assert.That(unowned.RestoredBy(PlayerID.Server), Is.False, "null ownership is distinct from server ID zero");
            var generated = new PredictionManager.CachedInputEntry(default, default, 3, 7, false, 11, 13, owner, false);
            Assert.That(generated.RestoredBy(owner), Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(7)]
        public void IdentityRetainsCompactIndependentUploadedBits(int prefixBits)
        {
            using var fixture = new Fixture();
            var identity = fixture.Add(815);
            using var source = BitPackerPool.Get();
            if (prefixBits > 0) source.WriteBits(5, (byte)prefixBits);
            source.WriteBits(0x1abcd, 17);
            var scratch = BitPackerPool.Get();
            scratch.EnsureBitsExist(65536 * 8);
            scratch.Dispose();
            identity.RecordUploadedInputBits(source, prefixBits);
            identity.ConsumeUploadedInputBits(10, true);
            Assert.That(identity.TryGetUploadedInputBits(10, out var uploaded), Is.True);
            Assert.That(uploaded.Equals(new BitData(source, prefixBits, 17)), Is.True);
            var retained = (BitPacker)typeof(PredictedIdentity).GetField("_uploadedInputBits", Members).GetValue(identity);
            Assert.That(retained.buffer.Length, Is.LessThan(64));
            Assert.That(retained, Is.Not.SameAs(scratch));
            source.ResetPositionAndMode(false);
            source.WriteBits(0, 32);
            retained.ResetPositionAndMode(true);
            Assert.That(retained.ReadBits(17), Is.EqualTo(0x1abcdUL));
            identity.ConsumeUploadedInputBits(11, false);
            Assert.That(identity.TryGetUploadedInputBits(11, out _), Is.False);
        }

        [Test]
        public void UnsanitizedUploadIsCapturedFromItsUploadedBitsWithoutReserializing()
        {
            using var fixture = new Fixture();
            var identity = fixture.Add(820);
            using var upload = BitPackerPool.Get();
            Packer<bool>.Write(upload, true);
            // The registered writer throws for this value, so capture must copy the received bits.
            Packer<int>.Write(upload, int.MinValue);
            upload.ResetPositionAndMode(true);
            identity.QueueInput(upload, new PlayerID(5, false));
            identity.PrepareInput(true, false, 10, false);
            fixture.Capture(10);

            var entries = Get<PredictionManager.CachedInputEntries>(fixture.Block(10), "entries");
            Assert.That(Get<bool>(entries[0], "asUploaded"), Is.True, "the owner restores its own upload");
            fixture.AssertInput(10, 820, int.MinValue);
        }

        // Discord report (Tekla, 2026-11-17): a body swap from Simulate ran twice on the host because the
        // released body replayed the click that released it.
        [Test]
        public void MissingInputAfterAnOwnerChangeDoesNotRepeatThePreviousOwnersInput()
        {
            using var fixture = new Fixture();
            var identity = fixture.Add(830);
            identity.owner = new PlayerID(5, false);
            fixture.Upload(identity, 77, new PlayerID(5, false));
            identity.PrepareInput(true, false, 10, true);
            fixture.Capture(10);
            fixture.AssertInput(10, 830, 77);

            identity.PrepareInput(true, false, 11, true);
            fixture.Capture(11);
            fixture.AssertInput(11, 830, 77);

            identity.owner = new PlayerID(6, false);
            identity.PrepareInput(true, false, 12, true);
            fixture.Capture(12);
            fixture.AssertInput(12, 830, 0);
        }

        [Test]
        public void InputQueuedByThePreviousOwnerIsDroppedWhenTheOwnerChanges()
        {
            using var fixture = new Fixture();
            var identity = fixture.Add(831);
            identity.owner = new PlayerID(5, false);
            fixture.Upload(identity, 77, new PlayerID(5, false));

            identity.owner = new PlayerID(6, false);
            identity.PrepareInput(true, false, 10, true);
            fixture.Capture(10);
            fixture.AssertInput(10, 831, 0);
        }

        [Test]
        public void IdentityUploadedBufferGrowsAndIsReusedForLargerInputs()
        {
            using var fixture = new Fixture();
            var identity = fixture.Add(816);
            using var source = BitPackerPool.Get();
            source.WriteBits(1, 1);
            identity.RecordUploadedInputBits(source, 0);
            var retained = typeof(PredictedIdentity).GetField("_uploadedInputBits", Members).GetValue(identity);
            source.ResetPositionAndMode(false);
            for (int i = 0; i < 32; i++) source.WriteBits((ulong)i, 32);
            identity.RecordUploadedInputBits(source, 0);
            identity.ConsumeUploadedInputBits(12, true);
            Assert.That(typeof(PredictedIdentity).GetField("_uploadedInputBits", Members).GetValue(identity), Is.SameAs(retained));
            Assert.That(identity.TryGetUploadedInputBits(12, out var uploaded), Is.True);
            Assert.That(uploaded.Equals(new BitData(source, 0, source.positionInBits)), Is.True);
        }

        [Test]
        public void GrowingInputRosterSizesNewSlotsWithoutDoublingOrChangingEarlierTicks()
        {
            using var fixture = new Fixture();
            var inputs = new List<ImmutableHistoryInputProbe>();
            for (uint id = 810; id < 814; id++)
            {
                var input = fixture.Add(id);
                inputs.Add(input);
                fixture.Seed(input, 10, (int)id);
            }
            fixture.Capture(10);
            var before = fixture.Snapshot(10);
            inputs.Add(fixture.Add(814));
            foreach (var input in inputs)
                fixture.Seed(input, 11, (int)input.id.objectId.instanceId.value);
            fixture.Capture(11);

            var entries = Get<PredictionManager.CachedInputEntries>(fixture.Block(11), "entries");
            Assert.That(entries.Count, Is.EqualTo(5));
            Assert.That(entries.Capacity, Is.EqualTo(5));
            Assert.That(fixture.Snapshot(10), Is.EqualTo(before));
            Assert.That(entries[4].id.objectId, Is.EqualTo(new PredictedObjectID(814)));
        }

        [TestCase(20, 32)]
        [TestCase(20, 128)]
        [TestCase(60, 32)]
        [TestCase(60, 128)]
        public void GrowingRosterReusesExpiredEntryTablesWithoutDoublingOrChangingRetainedTicks(int rate, int count)
        {
            using var fixture = new Fixture(rate);
            var inputs = new List<ImmutableHistoryInputProbe>();
            for (int i = 0; i < count; i++)
                inputs.Add(fixture.Add((uint)(1000 + i)));
            fixture.CaptureSeeded(10, inputs);
            int window = fixture.Capacity;
            ulong retainedTick = 10 + (ulong)window;
            for (ulong tick = 11; tick <= retainedTick; tick++)
                fixture.CaptureSeeded(tick, inputs);
            var retained = fixture.Snapshot(retainedTick);
            var scratch = Get<object>(fixture.manager, "_inputBlockScratch");
            var reused = Get<PredictionManager.CachedInputEntries>(scratch, "entries");
            int oldCapacity = reused.Capacity;
            Assert.That(oldCapacity, Is.EqualTo(count));

            inputs.Add(fixture.Add((uint)(1000 + count)));
            ulong changedTick = retainedTick + 1;
            fixture.CaptureSeeded(changedTick, inputs);
            var block = fixture.Block(changedTick);
            var entries = Get<PredictionManager.CachedInputEntries>(block, "entries");
            Assert.That(entries, Is.SameAs(reused));
            Assert.That(entries.Count, Is.EqualTo(count + 1));
            Assert.That(entries.Capacity, Is.InRange(count + 1, oldCapacity * 2 - 1));
            Assert.That(fixture.Snapshot(retainedTick), Is.EqualTo(retained));
            var bytes = Get<BitPacker>(block, "packer");
            using var reader = BitPackerPool.Get();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                Assert.That(entry.id, Is.EqualTo(inputs[i].id));
                reader.ResetPositionAndMode(false);
                reader.WriteBitDataWithoutConsumingIt(new BitData(bytes, entry.bitOrigin, entry.bitLength));
                reader.ResetPositionAndMode(true);
                Assert.That(Packer<bool>.Read(reader), Is.True);
                Assert.That(Packer<ImmutableHistoryInput>.Read(reader).value, Is.EqualTo((int)changedTick + i));
                Assert.That(reader.positionInBits, Is.EqualTo(entry.bitLength));
            }
        }

        [Test]
        public void ReorderingRemovingAndAddingInputsPreservesIdBasedRepeatDetection()
        {
            using var fixture = new Fixture();
            var first = fixture.Add(710);
            var middle = fixture.Add(720);
            var last = fixture.Add(730);
            fixture.Seed(first, 10, 111);
            fixture.Seed(middle, 10, 222);
            fixture.Seed(last, 10, 333);
            fixture.Capture(10);
            var original = fixture.Snapshot(10);

            Get<PredictedSystemList>(fixture.manager, "_systems").Reverse();
            fixture.Seed(first, 11, 111);
            fixture.Seed(middle, 11, 222);
            fixture.Seed(last, 11, 333);
            fixture.Capture(11);
            var reordered = Get<PredictionManager.CachedInputEntries>(fixture.Block(11), "entries");
            Assert.That(reordered[0].id, Is.EqualTo(last.id));
            Assert.That(reordered[1].id, Is.EqualTo(middle.id));
            Assert.That(reordered[2].id, Is.EqualTo(first.id));
            Assert.That(System.Linq.Enumerable.All(reordered, entry => entry.repeatsPrevious), Is.True,
                "both matching ordinals and lookup fallbacks must find each ID's own bits");
            var previous = fixture.Snapshot(11);

            fixture.manager.UnregisterInstance(middle);
            middle.ReleasePredictionStateForPool();
            var born = fixture.Add(740);
            fixture.Seed(first, 12, 444);
            fixture.Seed(last, 12, 333);
            fixture.Seed(born, 12, 222);
            fixture.Capture(12);
            var changed = Get<PredictionManager.CachedInputEntries>(fixture.Block(12), "entries");
            Assert.That(changed.Count, Is.EqualTo(3));
            Assert.That(System.Linq.Enumerable.First(changed, entry => entry.id.Equals(last.id)).repeatsPrevious, Is.True);
            Assert.That(System.Linq.Enumerable.First(changed, entry => entry.id.Equals(first.id)).repeatsPrevious, Is.False);
            Assert.That(System.Linq.Enumerable.First(changed, entry => entry.id.Equals(born.id)).repeatsPrevious, Is.False,
                "a new ID cannot repeat the removed input even when its bytes match");
            Assert.That(fixture.Snapshot(10), Is.EqualTo(original));
            Assert.That(fixture.Snapshot(11), Is.EqualTo(previous));
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

            internal void Upload(ImmutableHistoryInputProbe input, int value, PlayerID sender)
            {
                using var payload = BitPackerPool.Get();
                Packer<bool>.Write(payload, true);
                Packer<int>.Write(payload, value);
                payload.ResetPositionAndMode(true);
                input.QueueInput(payload, sender);
            }

            internal void Capture(ulong tick) => Invoke(manager, "CaptureInputHistory", tick);
            internal void CaptureSeeded(ulong tick, List<ImmutableHistoryInputProbe> inputs)
            {
                for (int i = 0; i < inputs.Count; i++)
                    Seed(inputs[i], tick, (int)tick + i);
                Capture(tick);
            }
            internal object Block(ulong tick) => Invoke(manager, "GetInputBlockForTick", tick);
            internal int Capacity => ((Array)Get<object>(manager, "_inputBlockCache")).Length;
            internal int EntryCount(ulong tick) => Get<PredictionManager.CachedInputEntries>(Block(tick), "entries").Count;
            internal PredictedObjectID FirstEntryRoot(ulong tick)
                => Get<PredictedObjectID>(Get<PredictionManager.CachedInputEntries>(Block(tick), "entries")[0], "rootId");
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
                foreach (var entry in Get<PredictionManager.CachedInputEntries>(block, "entries"))
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
                var entries = Get<PredictionManager.CachedInputEntries>(block, "entries");
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
