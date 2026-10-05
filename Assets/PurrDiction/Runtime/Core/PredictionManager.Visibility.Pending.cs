using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        readonly Dictionary<PlayerID, PlayerPendingVisibilityDeletes> _pendingVisibilityDeletes = new ();
        readonly List<PredictedObjectID> _incomingVisibilityDeletes = new ();
        readonly Stack<PlayerPendingVisibilityDeletes> _freePendingVisibilityDeletes = new ();

        internal void CapturePendingVisibilityDelete(PredictedObjectID objectId)
        {
            if (!hierarchy || _clientFrames.Count == 0)
                return;

            if (!hierarchy.TryGetRootId(objectId, out var rootId))
                rootId = objectId;

            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                var player = frame.player;
                if (!_playerVisibility.TryGetValue(player, out var timeline))
                    continue;

                ulong acknowledgedTick = 0;
                if (_clientTicks.TryGetValue(player, out var queue))
                    acknowledgedTick = queue.ackedServerTick;

                if (!HasSentVisibilityRoot(
                        player,
                        timeline,
                        rootId,
                        acknowledgedTick,
                        frame))
                {
                    continue;
                }

                if (!_pendingVisibilityDeletes.TryGetValue(player, out var receiverPending))
                {
                    receiverPending = _freePendingVisibilityDeletes.Count > 0
                        ? _freePendingVisibilityDeletes.Pop()
                        : new PlayerPendingVisibilityDeletes();
                    _pendingVisibilityDeletes.Add(player, receiverPending);
                }

                receiverPending.Capture(objectId, rootId);
            }
        }

        void AcknowledgePendingVisibilityDeletes(
            PlayerID player,
            PlayerVisibilityTimeline timeline,
            ulong acknowledgedTick)
        {
            if (!_pendingVisibilityDeletes.TryGetValue(player, out var pending))
                return;

            _visibilityRootScratch.Clear();
            pending.Acknowledge(acknowledgedTick, _visibilityRootScratch);
            for (var i = 0; i < _visibilityRootScratch.Count; i++)
            {
                var rootId = _visibilityRootScratch[i];
                bool rootStillProjected =
                    timeline.IsVisible(rootId) &&
                    hierarchy &&
                    hierarchy.ContainsSpawnedRoot(rootId);
                if (!pending.ContainsRoot(rootId) && !rootStillProjected)
                    RetireVisibilityRoot(player, rootId);
            }
            _visibilityRootScratch.Clear();

            if (pending.Count == 0)
            {
                _pendingVisibilityDeletes.Remove(player);
                _freePendingVisibilityDeletes.Push(pending);
            }
        }

        void WritePendingVisibilityDeleteSection(
            PlayerID player,
            BitPacker frame,
            ulong tick)
        {
            if (!_pendingVisibilityDeletes.TryGetValue(player, out var pending) ||
                pending.Count == 0)
            {
                AddressedPredictionRecords.WriteSectionCount(0, frame);
                return;
            }

            AddressedPredictionRecords.WriteSectionCount(pending.Count, frame);
            for (var i = 0; i < pending.Count; i++)
                Packer<PredictedObjectID>.Write(frame, pending.GetObjectId(i));
            pending.MarkPrepared(tick);
        }

        void ReadVisibilityDeleteSection(BitPacker frame)
        {
            _incomingVisibilityDeletes.Clear();

            PackedUInt count = default;
            Packer<PackedUInt>.Read(frame, ref count);

            for (uint i = 0; i < count.value; i++)
            {
                PredictedObjectID objectId = default;
                Packer<PredictedObjectID>.Read(frame, ref objectId);
                _incomingVisibilityDeletes.Add(objectId);
            }
        }

        void ApplyPendingRemoteVisibilityDeletes(ulong stateTick)
        {
            if (_incomingVisibilityDeletes.Count == 0)
                return;

            if (!hierarchy ||
                !hierarchy.TryGetVerifiedState(
                    stateTick,
                    out _,
                    out PredictedHierarchyState state))
            {
                return;
            }

            for (var i = 0; i < _incomingVisibilityDeletes.Count; i++)
            {
                var objectId = _incomingVisibilityDeletes[i];
                if (PredictedHierarchy.StateContainsInstance(state, objectId))
                    continue;

                hierarchy.ApplyRemoteVisibilityDelete(objectId);
            }

            _incomingVisibilityDeletes.Clear();
        }

        void MarkPendingVisibilityDeletesSent(PlayerID player, ulong tick)
        {
            if (_pendingVisibilityDeletes.TryGetValue(player, out var pending))
                pending.MarkSent(tick);
        }

        void RemovePendingVisibilityDeletes(PlayerID player)
        {
            if (_pendingVisibilityDeletes.Remove(player, out var pending))
                pending.Clear();
        }

        void ClearPendingVisibilityDeletes()
        {
            foreach (var pending in _pendingVisibilityDeletes.Values)
                pending.Clear();
            _pendingVisibilityDeletes.Clear();
            _incomingVisibilityDeletes.Clear();
        }
    }
}
