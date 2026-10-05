using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    internal readonly struct PooledPiece
    {
        public readonly PredictedObjectID id;
        public readonly uint pieceIndex;
        public readonly GameObject gameObject;

        public PooledPiece(PredictedObjectID id, uint pieceIndex, GameObject gameObject)
        {
            this.id = id;
            this.pieceIndex = pieceIndex;
            this.gameObject = gameObject;
        }
    }

    internal readonly struct SpawnKey
    {
        public readonly ulong tick;
        public readonly PredictedComponentID creator;
        public readonly int ordinal;
        public readonly bool isValid;

        public SpawnKey(ulong tick, PredictedComponentID creator, int ordinal)
        {
            this.tick = tick;
            this.creator = creator;
            this.ordinal = ordinal;
            isValid = true;
        }

        public bool Matches(in SpawnKey other) =>
            isValid && other.isValid && tick == other.tick && ordinal == other.ordinal && creator.Equals(other.creator);
    }

    internal sealed class PredictedPiecePool
    {
        sealed class Entry
        {
            public GameObject rootGo;
            public PredictedObjectID rootPieceId;
            public PackedInt prefabId;
            public ulong addedTick;
            public Vector3 rootSpawnPosition;
            public bool isComplete;
            public SpawnKey spawnKey;
            public readonly List<PooledPiece> pieces = new ();

            public bool IsReservedAt(ulong tick) => spawnKey.isValid && spawnKey.tick >= tick;
        }

        sealed class PieceIndexComparer : IComparer<PooledPiece>
        {
            public static readonly PieceIndexComparer instance = new ();

            public int Compare(PooledPiece a, PooledPiece b) => a.pieceIndex.CompareTo(b.pieceIndex);
        }

        readonly Dictionary<PredictedObjectID, Entry> _byPieceId = new ();
        readonly List<Entry> _entries = new ();
        readonly HashSet<GameObject> _entryPieceScratch = new ();
        readonly Stack<Entry> _freeEntries = new ();

        Entry RentEntry(GameObject rootGo, PredictedObjectID rootPieceId, PackedInt prefabId, ulong addedTick,
            Vector3 rootSpawnPosition, bool isComplete, in SpawnKey spawnKey)
        {
            var entry = _freeEntries.Count > 0 ? _freeEntries.Pop() : new Entry();
            entry.rootGo = rootGo;
            entry.rootPieceId = rootPieceId;
            entry.prefabId = prefabId;
            entry.addedTick = addedTick;
            entry.rootSpawnPosition = rootSpawnPosition;
            entry.isComplete = isComplete;
            entry.spawnKey = spawnKey;
            return entry;
        }

        void ReturnEntry(Entry entry)
        {
            entry.rootGo = null;
            entry.spawnKey = default;
            entry.pieces.Clear();
            _freeEntries.Push(entry);
        }

        public void PutTree(PackedInt prefabId, PredictedObjectID rootPieceId, Vector3 rootSpawnPosition,
            GameObject rootGo, List<PooledPiece> pieces, ulong tick, bool isComplete, in SpawnKey spawnKey = default)
        {
            var entry = RentEntry(rootGo, rootPieceId, prefabId, tick, rootSpawnPosition, isComplete, spawnKey);

            for (var i = 0; i < pieces.Count; i++)
            {
                entry.pieces.Add(pieces[i]);
                MapPiece(pieces[i].id, entry);
            }

            _entries.Add(entry);
        }

        public void PutPiece(PackedInt prefabId, PredictedObjectID pieceId, uint pieceIndex, GameObject go, ulong tick)
        {
            var entry = RentEntry(go, pieceId, prefabId, tick, go.transform.position, false, default);

            entry.pieces.Add(new PooledPiece(pieceId, pieceIndex, go));
            MapPiece(pieceId, entry);
            _entries.Add(entry);
        }

        void MapPiece(PredictedObjectID id, Entry entry)
        {
            if (_byPieceId.TryGetValue(id, out var stale) && stale != entry)
            {
                for (var i = stale.pieces.Count - 1; i >= 0; i--)
                {
                    if (stale.pieces[i].id.Equals(id))
                        stale.pieces.RemoveAt(i);
                }

                stale.isComplete = false;

                if (stale.pieces.Count == 0)
                    _entries.Remove(stale);
            }

            _byPieceId[id] = entry;
        }

        void UnmapPiece(PredictedObjectID id, Entry entry)
        {
            if (_byPieceId.TryGetValue(id, out var mapped) && mapped == entry)
                _byPieceId.Remove(id);
        }

        /// <summary>
        /// Releases the id lookup after a piece materializes elsewhere, preventing stale reuse.
        /// The entry retains ownership of its GameObject for cleanup.
        /// </summary>
        public void ReleaseClaim(PredictedObjectID id)
        {
            _byPieceId.Remove(id);
        }

        public bool Contains(PredictedObjectID pieceId)
        {
            return _byPieceId.ContainsKey(pieceId);
        }

        /// <summary>
        /// Takes the complete tree that showed the same spawn, whatever id it had then. Prefers the one
        /// that also kept its id.
        /// </summary>
        public bool TryTakeSameSpawn(PackedInt prefabId, in SpawnKey spawnKey, PredictedObjectID rootPieceId,
            List<PooledPiece> resultPieces, out GameObject rootGo)
        {
            rootGo = null;
            if (!spawnKey.isValid)
                return false;

            Entry match = null;
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (!entry.isComplete || entry.prefabId != prefabId || !entry.spawnKey.Matches(spawnKey))
                    continue;

                match = entry;
                if (entry.rootPieceId.Equals(rootPieceId))
                    break;
            }

            if (match == null)
                return false;

            TakeEntry(match, resultPieces, out rootGo);
            return true;
        }

        public bool TryTakeTree(PredictedObjectID rootPieceId, PackedInt prefabId, Vector3 expectedSpawnPosition, bool checkDrift,
            List<PooledPiece> resultPieces, out GameObject rootGo, out bool foundButDrifted, ulong currentTick = ulong.MaxValue)
        {
            foundButDrifted = false;

            if (!_byPieceId.TryGetValue(rootPieceId, out var entry) || entry.rootPieceId.Equals(rootPieceId) == false ||
                entry.prefabId != prefabId || entry.IsReservedAt(currentTick))
            {
                rootGo = null;
                return false;
            }

            if (checkDrift && Vector3.Distance(entry.rootSpawnPosition, expectedSpawnPosition) > 0.1f)
            {
                rootGo = null;
                foundButDrifted = true;
                return false;
            }

            TakeEntry(entry, resultPieces, out rootGo);
            return true;
        }

        public bool TryTakeExactCompleteTree(PredictedObjectID rootPieceId, PackedInt prefabId,
            List<PooledPiece> resultPieces, out GameObject rootGo, ulong currentTick = ulong.MaxValue)
        {
            if (!_byPieceId.TryGetValue(rootPieceId, out var entry) ||
                !entry.rootPieceId.Equals(rootPieceId) || entry.prefabId != prefabId || !entry.isComplete ||
                entry.IsReservedAt(currentTick))
            {
                rootGo = null;
                return false;
            }

            TakeEntry(entry, resultPieces, out rootGo);
            return true;
        }

        public bool TryTakeNearestCompleteTree(PackedInt prefabId, Vector3 spawnPosition,
            List<PooledPiece> resultPieces, out GameObject rootGo, ulong currentTick = ulong.MaxValue)
        {
            Entry closest = null;
            float closestError = float.MaxValue;

            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];

                if (!entry.isComplete || entry.prefabId != prefabId || entry.IsReservedAt(currentTick))
                    continue;

                float posError = Vector3.Distance(entry.rootSpawnPosition, spawnPosition);

                if (posError < closestError)
                {
                    closestError = posError;
                    closest = entry;
                }
            }

            if (closest == null)
            {
                rootGo = null;
                return false;
            }

            TakeEntry(closest, resultPieces, out rootGo);
            return true;
        }

        public bool TryTakePiece(PredictedObjectID pieceId, PackedInt prefabId, out GameObject go)
        {
            if (!_byPieceId.TryGetValue(pieceId, out var entry) || entry.prefabId != prefabId)
            {
                go = null;
                return false;
            }

            int pieceIdx = -1;
            for (var i = 0; i < entry.pieces.Count; i++)
            {
                if (entry.pieces[i].id.Equals(pieceId))
                {
                    pieceIdx = i;
                    break;
                }
            }

            if (pieceIdx == -1)
            {
                go = null;
                return false;
            }

            var piece = entry.pieces[pieceIdx];
            go = piece.gameObject;

            if (!go)
            {
                entry.pieces.RemoveAt(pieceIdx);
                UnmapPiece(pieceId, entry);
                go = null;
                return false;
            }

            SplitOffChildSubtrees(entry, go);

            entry.pieces.RemoveAt(pieceIdx);
            UnmapPiece(pieceId, entry);
            entry.isComplete = false;

            if (go.transform.parent != null)
                go.transform.SetParent(null, false);

            if (entry.pieces.Count == 0)
            {
                _entries.Remove(entry);
                ReturnEntry(entry);
            }
            else if (go == entry.rootGo)
            {
                var newRoot = entry.pieces[0];
                entry.rootGo = newRoot.gameObject;
                entry.rootPieceId = newRoot.id;
            }

            return true;
        }

        void SplitOffChildSubtrees(Entry entry, GameObject piece)
        {
            _entryPieceScratch.Clear();
            for (var i = 0; i < entry.pieces.Count; i++)
            {
                var go = entry.pieces[i].gameObject;
                if (go && go != piece)
                    _entryPieceScratch.Add(go);
            }

            var subtreeRoots = ListPool<Transform>.Instantiate();
            CollectTopmostEntryPieces(piece.transform, subtreeRoots);

            for (var i = 0; i < subtreeRoots.Count; i++)
            {
                var subRoot = subtreeRoots[i];
                subRoot.SetParent(null, false);

                var subEntry = RentEntry(subRoot.gameObject, default, entry.prefabId, entry.addedTick,
                    subRoot.position, false, default);

                MovePiecesInSubtree(entry, subEntry, subRoot);
                subEntry.rootPieceId = subEntry.pieces.Count > 0 ? subEntry.pieces[0].id : default;
                _entries.Add(subEntry);
            }

            ListPool<Transform>.Destroy(subtreeRoots);
        }

        void CollectTopmostEntryPieces(Transform current, List<Transform> results)
        {
            int childCount = current.childCount;
            for (var i = 0; i < childCount; i++)
            {
                var child = current.GetChild(i);

                if (_entryPieceScratch.Contains(child.gameObject))
                    results.Add(child);
                else
                    CollectTopmostEntryPieces(child, results);
            }
        }

        void MovePiecesInSubtree(Entry from, Entry to, Transform subRoot)
        {
            for (var i = from.pieces.Count - 1; i >= 0; i--)
            {
                var candidate = from.pieces[i];
                if (!candidate.gameObject)
                    continue;

                var t = candidate.gameObject.transform;
                if (t == subRoot || t.IsChildOf(subRoot))
                {
                    from.pieces.RemoveAt(i);
                    to.pieces.Add(candidate);
                    MapPiece(candidate.id, to);
                }
            }

            to.pieces.Sort(PieceIndexComparer.instance);
        }

        void TakeEntry(Entry entry, List<PooledPiece> resultPieces, out GameObject rootGo)
        {
            for (var i = 0; i < entry.pieces.Count; i++)
                UnmapPiece(entry.pieces[i].id, entry);
            _entries.Remove(entry);
            resultPieces.AddRange(entry.pieces);
            rootGo = entry.rootGo;
            ReturnEntry(entry);
        }

        public void ClearOld(PredictionManager predictionManager)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];

                if (entry.prefabId.value < 0)
                    continue;

                var delta = predictionManager.localTick - entry.addedTick;

                if (delta <= (uint)predictionManager.tickRate * 2)
                    continue;

                DestroyEntry(predictionManager, entry);
            }
        }

        public void Clear(PredictionManager predictionManager)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
                DestroyEntry(predictionManager, _entries[i]);
        }

        void DestroyEntry(PredictionManager predictionManager, Entry entry)
        {
            for (var i = 0; i < entry.pieces.Count; i++)
                UnmapPiece(entry.pieces[i].id, entry);

            _entries.Remove(entry);

            if (entry.rootGo)
            {
                if (entry.isComplete)
                    predictionManager.InternalDelete(entry.prefabId, entry.rootGo);
                else
                    UnityProxy.DestroyImmediateDirectly(entry.rootGo);
            }

            ReturnEntry(entry);
        }
    }
}
