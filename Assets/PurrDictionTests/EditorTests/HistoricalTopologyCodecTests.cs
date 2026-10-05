using System;
using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class HistoricalTopologyCodecTests
    {
        [OneTimeSetUp]
        public void RegisterPackers() => NetworkManager.CallAllRegisters();

        [Test]
        public void OrderedRemovalInsertionAndReorderPreserveEveryTopologyField()
        {
            using var baseline = State(new[] { Record(10), Record(11), Record(12), Record(13) }, 70, 13, 10);
            using var current = State(new[]
            {
                Record(13),
                new InstanceDetails(9, 2, new PredictedObjectID(12), new Vector3(4, 5, 6),
                    new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), new PlayerID(4, true),
                    new PredictedComponentID(new PredictedObjectID(13), 3)),
                Record(90), Record(10)
            }, 1234, 90, 12, 90);
            using var before = Serialize(baseline);
            var decoded = RoundTrip(baseline, current);
            AssertExact(current, decoded);
            decoded.spawnedPrefabs.RemoveAt(0);
            decoded.toDelete.Clear();
            decoded.Dispose();
            using var after = Serialize(baseline);
            AssertBits("decoded ownership must not alias the borrowed baseline", before, after);

            using var empty = State(Array.Empty<InstanceDetails>(), 99);
            using var cleared = RoundTrip(current, empty);
            AssertExact(empty, cleared);
        }

        [TestCase("tinyPosition")]
        [TestCase("signedZeroPosition")]
        [TestCase("nanPosition")]
        [TestCase("tinyRotation")]
        [TestCase("signedZeroRotation")]
        [TestCase("nanRotation")]
        [TestCase("ownerBot")]
        [TestCase("ownerNull")]
        [TestCase("parentNull")]
        public void NumericalEqualityCannotDiscardAnExactRecordChange(string change)
        {
            float nanA = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001));
            float nanB = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00002));
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            Vector3 oldPosition = new Vector3(0, 1, nanA), position = oldPosition;
            Quaternion oldRotation = new Quaternion(0, 1, nanA, 1), rotation = oldRotation;
            PlayerID? owner = new PlayerID(7, false);
            PredictedComponentID? parent = new PredictedComponentID(new PredictedObjectID(9), 2);
            switch (change)
            {
                case "tinyPosition": position.y += 0.00000011920928955078125f; break;
                case "signedZeroPosition": position.x = negativeZero; break;
                case "nanPosition": position.z = nanB; break;
                case "tinyRotation": rotation.y += 0.00000011920928955078125f; break;
                case "signedZeroRotation": rotation.x = negativeZero; break;
                case "nanRotation": rotation.z = nanB; break;
                case "ownerBot": owner = new PlayerID(7, true); break;
                case "ownerNull": owner = null; break;
                case "parentNull": parent = null; break;
            }
            using var baseline = State(new[] { new InstanceDetails(3, 0, new PredictedObjectID(10),
                oldPosition, oldRotation, new PlayerID(7, false),
                new PredictedComponentID(new PredictedObjectID(9), 2)) }, 11);
            using var current = State(new[] { new InstanceDetails(3, 0, new PredictedObjectID(10),
                position, rotation, owner, parent) }, 11);
            using var wire = Encode(baseline, current);
            int end = wire.positionInBits;
            wire.ResetPositionAndMode(true);
            Packer<PackedUInt>.Read(wire); // next ID
            Assert.That(Packer<PackedUInt>.Read(wire).value, Is.Zero); // deletes
            Assert.That(Packer<PackedUInt>.Read(wire).value, Is.EqualTo(1)); // records
            Assert.That(Packer<PackedUInt>.Read(wire).value, Is.Zero,
                "this record must be sent literally, even when approximate/numeric equality says unchanged");
            wire.ResetPositionAndMode(true);
            using var decoded = HistoricalTopologyCodec.Read(baseline, wire, end);
            AssertExact(current, decoded);
            AssertRecordBits(current.spawnedPrefabs[0], decoded.spawnedPrefabs[0]);
        }

        [Test]
        public void UnchangedSignedZeroAndNanRecordsCanStillUseAReferenceRun()
        {
            var record = new InstanceDetails(1, new PredictedObjectID(8),
                new Vector3(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
                    BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00002)), 0),
                Quaternion.identity, null);
            using var baseline = State(new[] { record, Record(9) }, 10);
            using var wire = Encode(baseline, baseline);
            int end = wire.positionInBits;
            wire.ResetPositionAndMode(true);
            Packer<PackedUInt>.Read(wire);
            Packer<PackedUInt>.Read(wire);
            Assert.That(Packer<PackedUInt>.Read(wire).value, Is.EqualTo(2));
            Assert.That(Packer<PackedUInt>.Read(wire).value, Is.EqualTo(1));
            Assert.That(Packer<PackedUInt>.Read(wire).value, Is.EqualTo(2));
            Assert.That(wire.positionInBits, Is.EqualTo(end));
            wire.ResetPositionAndMode(true);
            using var decoded = HistoricalTopologyCodec.Read(baseline, wire, end);
            AssertExact(baseline, decoded);
        }

        [TestCase("index")]
        [TestCase("zeroRun")]
        [TestCase("sourceRun")]
        [TestCase("targetRun")]
        [TestCase("duplicate")]
        [TestCase("duplicateLiteral")]
        [TestCase("hugeCount")]
        [TestCase("trailing")]
        public void MalformedReferencesAndCountsFailWithoutMutatingBaseline(string fault)
        {
            using var baseline = State(new[] { Record(10), Record(11), Record(12) }, 13, 12);
            using var before = Serialize(baseline);
            using var wire = BitPackerPool.Get();
            Packer<PackedUInt>.Write(wire, 13u);
            Packer<PackedUInt>.Write(wire, 0u);
            Packer<PackedUInt>.Write(wire, fault == "hugeCount" ? uint.MaxValue : 2u);
            Packer<PackedUInt>.Write(wire, fault == "index" ? 4u : fault == "sourceRun" ? 3u : 1u);
            Packer<PackedUInt>.Write(wire, fault == "zeroRun" ? 0u : fault == "targetRun" ? 3u :
                fault == "duplicate" || fault == "duplicateLiteral" ? 1u : 2u);
            if (fault == "duplicate")
            {
                Packer<PackedUInt>.Write(wire, 1u);
                Packer<PackedUInt>.Write(wire, 1u);
            }
            if (fault == "duplicateLiteral")
            {
                Packer<PackedUInt>.Write(wire, 0u);
                Packer<InstanceDetails>.Write(wire, Record(10));
            }
            if (fault == "trailing") Packer<bool>.Write(wire, true);
            int end = wire.positionInBits;
            wire.ResetPositionAndMode(true);
            Assert.Throws<MissingPredictionBaselineException>(() =>
            {
                using var unexpected = HistoricalTopologyCodec.Read(baseline, wire, end);
            });
            using var after = Serialize(baseline);
            AssertBits("failed decode must leave every borrowed baseline list intact", before, after);
        }

        [Test]
        public void EveryTruncatedPrefixOfMixedLiteralAndRunPayloadIsRejected()
        {
            using var baseline = State(new[] { Record(10), Record(11) }, 12);
            using var current = State(new[] { Record(10), Record(11), Record(20) }, 21, 22);
            using var before = Serialize(baseline);
            using var wire = Encode(baseline, current);
            int end = wire.positionInBits;
            for (int bits = 0; bits < end; bits++)
            {
                wire.ResetPositionAndMode(true);
                int bound = bits;
                Assert.Throws<MissingPredictionBaselineException>(() =>
                {
                    using var unexpected = HistoricalTopologyCodec.Read(baseline, wire, bound);
                }, $"truncated at bit {bound}/{end}");
            }
            using var after = Serialize(baseline);
            AssertBits(null, before, after);
        }

        [Test]
        public void ChurnAmongAnExistingRosterCostsChangedRecordsAndReferenceRuns()
        {
            var records = new List<InstanceDetails>();
            for (uint id = 10; id < 266; id++) records.Add(Record(id));
            using var baseline = State(records.ToArray(), 300);
            records.RemoveRange(80, 3);
            records.Insert(80, Record(310));
            records.Insert(81, Record(311));
            var moved = records[160];
            records.RemoveAt(160);
            records.Insert(20, moved);
            using var current = State(records.ToArray(), 312, 90, 91, 92);
            using var wire = Encode(baseline, current);
            using var full = Serialize(current);
            Assert.That(wire.positionInBits, Is.LessThan(full.positionInBits / 4),
                "small churn must not retransmit the entire historical roster each tick");
            using var decoded = RoundTrip(baseline, current);
            AssertExact(current, decoded);
        }

        private static InstanceDetails Record(uint id) => new InstanceDetails(2, new PredictedObjectID(id),
            new Vector3(id, id + 1, id + 2), Quaternion.identity, new PlayerID(2, false));

        private static PredictedHierarchyState State(InstanceDetails[] records, uint next, params uint[] deletes)
        {
            var result = new PredictedHierarchyState(DisposableList<InstanceDetails>.Create(records.Length),
                DisposableList<PredictedObjectID>.Create(deletes.Length), next);
            foreach (var record in records) result.spawnedPrefabs.Add(record);
            foreach (uint id in deletes) result.toDelete.Add(new PredictedObjectID(id));
            return result;
        }

        private static BitPacker Encode(in PredictedHierarchyState baseline, in PredictedHierarchyState current)
        {
            var wire = BitPackerPool.Get();
            HistoricalTopologyCodec.Write(baseline, current, wire, new Dictionary<PredictedObjectID, int>());
            return wire;
        }

        private static PredictedHierarchyState RoundTrip(in PredictedHierarchyState baseline,
            in PredictedHierarchyState current)
        {
            using var wire = Encode(baseline, current);
            int end = wire.positionInBits;
            wire.ResetPositionAndMode(true);
            return HistoricalTopologyCodec.Read(baseline, wire, end);
        }

        private static BitPacker Serialize(in PredictedHierarchyState state)
        {
            var wire = BitPackerPool.Get();
            Packer<PredictedHierarchyState>.Write(wire, state);
            return wire;
        }

        private static void AssertExact(in PredictedHierarchyState expected, in PredictedHierarchyState actual)
        {
            using var first = Serialize(expected);
            using var second = Serialize(actual);
            AssertBits(null, first, second);
            Assert.That(actual.nextInstanceId, Is.EqualTo(expected.nextInstanceId));
            Assert.That(actual.spawnedPrefabs.Count, Is.EqualTo(expected.spawnedPrefabs.Count));
            for (int i = 0; i < actual.spawnedPrefabs.Count; i++)
                AssertRecordBits(expected.spawnedPrefabs[i], actual.spawnedPrefabs[i]);
        }

        private static void AssertRecordBits(in InstanceDetails expected, in InstanceDetails actual)
        {
            for (int i = 0; i < 3; i++)
                Assert.That(BitConverter.SingleToInt32Bits(actual.spawnPosition[i]),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(expected.spawnPosition[i])));
            for (int i = 0; i < 4; i++)
                Assert.That(BitConverter.SingleToInt32Bits(actual.spawnRotation[i]),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(expected.spawnRotation[i])));
            Assert.That(actual.owner.HasValue, Is.EqualTo(expected.owner.HasValue));
            if (expected.owner.HasValue)
            {
                Assert.That(actual.owner.Value.id.value, Is.EqualTo(expected.owner.Value.id.value));
                Assert.That(actual.owner.Value.isBot, Is.EqualTo(expected.owner.Value.isBot));
            }
        }

        private static void AssertBits(string message, BitPacker first, BitPacker second)
        {
            Assert.That(second.positionInBits, Is.EqualTo(first.positionInBits), message);
            Assert.That(new BitData(first, 0, first.positionInBits).Equals(
                new BitData(second, 0, second.positionInBits)), Is.True, message);
        }
    }
}
