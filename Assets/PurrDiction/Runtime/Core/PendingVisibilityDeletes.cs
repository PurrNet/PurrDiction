using System.Collections.Generic;

namespace PurrNet.Prediction
{
    // Hierarchy deletes are transient. Repeat each receiver's tombstone until a frame
    // carrying it is acknowledged.
    internal sealed class PlayerPendingVisibilityDeletes
    {
        struct Entry
        {
            public PredictedObjectID objectId;
            public PredictedObjectID rootId;
            public ulong preparedTick;
            public ulong firstSentTick;
        }

        readonly List<Entry> _entries = new ();

        public int Count => _entries.Count;

        public PredictedObjectID GetObjectId(int index)
        {
            return _entries[index].objectId;
        }

        public void Capture(
            PredictedObjectID objectId,
            PredictedObjectID rootId)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].objectId.Equals(objectId))
                    return;
            }

            _entries.Add(new Entry
            {
                objectId = objectId,
                rootId = rootId
            });
        }

        public void MarkPrepared(ulong tick)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                entry.preparedTick = tick;
                _entries[i] = entry;
            }
        }

        public void MarkSent(ulong tick)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.firstSentTick != 0 || entry.preparedTick != tick)
                    continue;

                entry.firstSentTick = tick;
                _entries[i] = entry;
            }
        }

        public void Acknowledge(ulong acknowledgedTick)
        {
            Acknowledge(acknowledgedTick, null);
        }

        public void Acknowledge(
            ulong acknowledgedTick,
            List<PredictedObjectID> acknowledgedRoots)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.firstSentTick == 0 || entry.firstSentTick > acknowledgedTick)
                    continue;

                acknowledgedRoots?.Add(entry.rootId);
                _entries.RemoveAt(i);
            }
        }

        public bool ContainsRoot(PredictedObjectID rootId)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].rootId.Equals(rootId))
                    return true;
            }

            return false;
        }

        public void Clear()
        {
            _entries.Clear();
        }
    }
}
