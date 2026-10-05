using System;
using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    // An exact ordered projection of a topology onto a previous topology. References
    // borrow record values only; decoding always creates independently owned lists.
    internal static class HistoricalTopologyCodec
    {
        internal static void Write(in PredictedHierarchyState baseline, in PredictedHierarchyState current,
            BitPacker destination, Dictionary<PredictedObjectID, int> scratch)
        {
            scratch.Clear();
            int baselineCount = baseline.spawnedPrefabs.isDisposed ? 0 : baseline.spawnedPrefabs.Count;
            for (int i = 0; i < baselineCount; i++)
                scratch.Add(baseline.spawnedPrefabs[i].instanceId, i);

            Packer<PackedUInt>.Write(destination, current.nextInstanceId);
            int deleteCount = current.toDelete.isDisposed ? 0 : current.toDelete.Count;
            Packer<PackedUInt>.Write(destination, (uint)deleteCount);
            for (int i = 0; i < deleteCount; i++)
                Packer<PredictedObjectID>.Write(destination, current.toDelete[i]);

            int count = current.spawnedPrefabs.isDisposed ? 0 : current.spawnedPrefabs.Count;
            Packer<PackedUInt>.Write(destination, (uint)count);
            for (int i = 0; i < count;)
            {
                var record = current.spawnedPrefabs[i];
                bool reference = scratch.TryGetValue(record.instanceId, out int index) &&
                                 record.Equals(baseline.spawnedPrefabs[index]);
                Packer<PackedUInt>.Write(destination, reference ? (uint)index + 1 : 0u);
                if (reference)
                {
                    int run = 1;
                    while (i + run < count && index + run < baselineCount &&
                           current.spawnedPrefabs[i + run].Equals(baseline.spawnedPrefabs[index + run]))
                        run++;
                    Packer<PackedUInt>.Write(destination, (uint)run);
                    i += run;
                }
                else
                {
                    Packer<InstanceDetails>.Write(destination, record);
                    i++;
                }
            }
        }

        internal static PredictedHierarchyState Read(in PredictedHierarchyState baseline,
            BitPacker boundedSource, int endBit)
        {
            var result = new PredictedHierarchyState();
            var ids = HashSetPool<PredictedObjectID>.Instantiate();
            try
            {
                if (endBit < boundedSource.positionInBits || endBit > (long)boundedSource.buffer.Length * 8)
                    throw new MissingPredictionBaselineException("Invalid lifecycle topology bounds.");
                result.nextInstanceId = ReadValue<PackedUInt>(endBit, boundedSource);
                int deletes = ReadCount(endBit, boundedSource);
                result.toDelete = DisposableList<PredictedObjectID>.Create(deletes);
                for (int i = 0; i < deletes; i++)
                    result.toDelete.Add(ReadValue<PredictedObjectID>(endBit, boundedSource));

                int baselineCount = baseline.spawnedPrefabs.isDisposed ? 0 : baseline.spawnedPrefabs.Count;
                uint encodedCount = ReadValue<PackedUInt>(endBit, boundedSource);
                // References can expand a short payload, but each baseline identity can
                // appear only once; every additional literal consumes payload bits.
                if (encodedCount > int.MaxValue ||
                    encodedCount > (long)baselineCount + endBit - boundedSource.positionInBits)
                    throw new MissingPredictionBaselineException("Invalid lifecycle topology collection length.");
                int count = (int)encodedCount;
                result.spawnedPrefabs = DisposableList<InstanceDetails>.Create(Math.Min(count, 256));
                while (result.spawnedPrefabs.Count < count)
                {
                    uint reference = ReadValue<PackedUInt>(endBit, boundedSource);
                    if (reference == 0)
                    {
                        var record = ReadValue<InstanceDetails>(endBit, boundedSource);
                        if (!ids.Add(record.instanceId))
                            throw new MissingPredictionBaselineException("Duplicate lifecycle topology identity.");
                        result.spawnedPrefabs.Add(record);
                    }
                    else
                    {
                        if (reference > baselineCount)
                            throw new MissingPredictionBaselineException("Invalid lifecycle topology record reference.");
                        uint run = ReadValue<PackedUInt>(endBit, boundedSource);
                        int index = (int)reference - 1;
                        if (run == 0 || run > baselineCount - index || run > count - result.spawnedPrefabs.Count)
                            throw new MissingPredictionBaselineException("Invalid lifecycle topology reference run.");
                        for (int i = 0; i < (int)run; i++)
                        {
                            var record = baseline.spawnedPrefabs[index + i];
                            if (!ids.Add(record.instanceId))
                                throw new MissingPredictionBaselineException("Duplicate lifecycle topology identity.");
                            result.spawnedPrefabs.Add(record);
                        }
                    }
                }
                if (boundedSource.positionInBits != endBit)
                    throw new MissingPredictionBaselineException("Incomplete lifecycle topology payload.");
                return result;
            }
            catch (Exception error)
            {
                result.Dispose();
                if (error is MissingPredictionBaselineException)
                    throw;
                throw new MissingPredictionBaselineException($"Invalid lifecycle topology payload: {error.Message}");
            }
            finally
            {
                HashSetPool<PredictedObjectID>.Destroy(ids);
            }
        }

        private static int ReadCount(int endBit, BitPacker source)
        {
            uint count = ReadValue<PackedUInt>(endBit, source);
            // Every encoded element needs at least one bit. Bound allocations by the
            // supplied payload before interpreting untrusted collection lengths.
            if (count > int.MaxValue || count > endBit - source.positionInBits)
                throw new MissingPredictionBaselineException("Invalid lifecycle topology collection length.");
            return (int)count;
        }

        private static T ReadValue<T>(int endBit, BitPacker source)
        {
            if (source.positionInBits >= endBit)
                throw new MissingPredictionBaselineException("Truncated lifecycle topology payload.");
            var value = Packer<T>.Read(source);
            if (source.positionInBits > endBit)
                throw new MissingPredictionBaselineException("Truncated lifecycle topology payload.");
            return value;
        }

    }
}
