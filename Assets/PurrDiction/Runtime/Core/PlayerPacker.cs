using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal sealed class IdentityEstablishmentState
    {
        readonly Dictionary<PredictedComponentID, ulong> _sentTicks = new ();

        public void Reset()
        {
            _sentTicks.Clear();
        }

        public void MarkSent(PredictedComponentID id, ulong sentTick)
        {
            _sentTicks[id] = sentTick;
        }

        public bool Prepare(PredictedComponentID id, ulong ackedTick, ulong sentTick)
        {
            if (!_sentTicks.TryGetValue(id, out var identitySentTick))
            {
                _sentTicks.Add(id, sentTick);
                return true;
            }

            if (identitySentTick == 0)
                return false;

            if (ackedTick >= identitySentTick)
            {
                _sentTicks[id] = 0;
                return false;
            }

            return true;
        }

        public bool RequiresAbsolute(PredictedComponentID id)
        {
            return !_sentTicks.TryGetValue(id, out var sentTick) || sentTick != 0;
        }

        public bool Invalidate(PredictedComponentID id)
        {
            if (!_sentTicks.TryGetValue(id, out var sentTick) || sentTick != 0)
                return false;

            _sentTicks.Remove(id);
            return true;
        }

        public void Remove(PredictedComponentID id)
        {
            _sentTicks.Remove(id);
        }

        public void Clear()
        {
            _sentTicks.Clear();
        }
    }

    internal sealed class RootSendBaselineState
    {
        private sealed class RootHistory
        {
            public readonly List<ulong> sentTicks = new (16);
            public int firstPending;
            public ulong acknowledgedTick;
        }

        readonly Dictionary<PredictedObjectID, RootHistory> _roots = new ();

        public ulong GetBaseline(PredictedObjectID root, ulong ackedServerTick)
        {
            if (!_roots.TryGetValue(root, out var history))
                return ackedServerTick;

            while (history.firstPending < history.sentTicks.Count &&
                   history.sentTicks[history.firstPending] <= ackedServerTick)
            {
                history.acknowledgedTick = history.sentTicks[history.firstPending++];
            }

            if (history.firstPending == history.sentTicks.Count)
            {
                history.sentTicks.Clear();
                history.firstPending = 0;
            }
            else if (history.firstPending >= 32 && history.firstPending * 2 >= history.sentTicks.Count)
            {
                history.sentTicks.RemoveRange(0, history.firstPending);
                history.firstPending = 0;
            }

            return history.acknowledgedTick == 0 ? ackedServerTick : history.acknowledgedTick;
        }

        public void MarkSent(PredictedObjectID root, ulong sentTick)
        {
            if (!_roots.TryGetValue(root, out var history))
            {
                history = new RootHistory();
                _roots.Add(root, history);
            }

            int count = history.sentTicks.Count;
            if (count == 0 || history.sentTicks[count - 1] != sentTick)
                history.sentTicks.Add(sentTick);
        }

        public void Reset(PredictedObjectID root)
        {
            _roots.Remove(root);
        }

        public void PruneBefore(ulong oldestTick)
        {
            foreach (var history in _roots.Values)
            {
                while (history.firstPending < history.sentTicks.Count &&
                       history.sentTicks[history.firstPending] < oldestTick)
                {
                    history.firstPending++;
                }

                if (history.firstPending == 0)
                    continue;

                history.sentTicks.RemoveRange(0, history.firstPending);
                history.firstPending = 0;
            }
        }

        public void Clear()
        {
            _roots.Clear();
        }
    }

    internal readonly struct InterestTierChange
    {
        public readonly PredictedObjectID root;
        public readonly byte tier;

        public InterestTierChange(PredictedObjectID root, byte tier)
        {
            this.root = root;
            this.tier = tier;
        }
    }

    internal sealed class InterestControlState
    {
        readonly Dictionary<PredictedObjectID, byte> _deliveredTiers = new ();
        readonly Dictionary<PredictedObjectID, ulong> _absoluteUntilConfirmed = new ();
        readonly HashSet<PredictedObjectID> _baselineResets = new ();
        readonly List<InterestTierChange> _pending = new (8);
        readonly List<InterestTierChange> _prepared = new (8);

        public int count => _prepared.Count;

        public bool hasUnconfirmedReentries => _absoluteUntilConfirmed.Count > 0;

        public InterestTierChange this[int index] => _prepared[index];

        public void Queue(PredictedObjectID root, byte tier)
        {
            byte deliveredTier = GetTierAfterPrepared(root);

            for (var i = 0; i < _pending.Count; i++)
            {
                if (!_pending[i].root.Equals(root))
                    continue;

                if (tier == deliveredTier)
                    _pending.RemoveAt(i);
                else
                    _pending[i] = new InterestTierChange(root, tier);
                return;
            }

            if (tier != deliveredTier)
                _pending.Add(new InterestTierChange(root, tier));
        }

        public void PrepareForFrame(ulong sentTick)
        {
            _prepared.Clear();
            _prepared.AddRange(_pending);
            _pending.Clear();

            for (var i = 0; i < _prepared.Count; i++)
            {
                var change = _prepared[i];
                byte deliveredTier = _deliveredTiers.GetValueOrDefault(change.root, (byte)0);

                if (deliveredTier == NetworkLODProfile.CulledTier &&
                    change.tier != NetworkLODProfile.CulledTier &&
                    !_absoluteUntilConfirmed.ContainsKey(change.root))
                {
                    _absoluteUntilConfirmed.Add(change.root, sentTick);
                    _baselineResets.Add(change.root);
                }
                else if (change.tier == NetworkLODProfile.CulledTier)
                {
                    _absoluteUntilConfirmed.Remove(change.root);
                    _baselineResets.Remove(change.root);
                }
            }
        }

        public bool RequiresAbsolute(PredictedObjectID root)
        {
            return _absoluteUntilConfirmed.ContainsKey(root);
        }

        public bool ConfirmReentry(PredictedObjectID root, ulong serverTick)
        {
            if (!_absoluteUntilConfirmed.TryGetValue(root, out var sentTick) || serverTick < sentTick)
                return false;

            _absoluteUntilConfirmed.Remove(root);
            _baselineResets.Remove(root);
            return true;
        }

        public bool ConsumeBaselineReset(PredictedObjectID root)
        {
            return _baselineResets.Remove(root);
        }

        public void MarkDelivered()
        {
            for (var i = 0; i < _prepared.Count; i++)
            {
                var change = _prepared[i];
                if (change.tier == 0)
                    _deliveredTiers.Remove(change.root);
                else
                    _deliveredTiers[change.root] = change.tier;
            }

            _prepared.Clear();
        }

        public void Remove(PredictedObjectID root)
        {
            _deliveredTiers.Remove(root);
            _absoluteUntilConfirmed.Remove(root);
            _baselineResets.Remove(root);

            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].root.Equals(root))
                    _pending.RemoveAt(i);
            }

            for (var i = _prepared.Count - 1; i >= 0; i--)
            {
                if (_prepared[i].root.Equals(root))
                    _prepared.RemoveAt(i);
            }
        }

        private byte GetTierAfterPrepared(PredictedObjectID root)
        {
            for (var i = 0; i < _prepared.Count; i++)
            {
                if (_prepared[i].root.Equals(root))
                    return _prepared[i].tier;
            }

            return _deliveredTiers.GetValueOrDefault(root, (byte)0);
        }

        public void Clear()
        {
            _deliveredTiers.Clear();
            _absoluteUntilConfirmed.Clear();
            _baselineResets.Clear();
            _pending.Clear();
            _prepared.Clear();
        }
    }

    internal struct ReliableFrameDeliveryState
    {
        private ulong _sentTick;

        public bool ShouldSuppress(ulong acknowledgedTick)
        {
            if (_sentTick == 0)
                return false;

            if (acknowledgedTick < _sentTick)
                return true;

            _sentTick = 0;
            return false;
        }

        public void MarkSent(ulong sentTick)
        {
            _sentTick = sentTick;
        }

        public void Clear()
        {
            _sentTick = 0;
        }
    }

    internal struct PlayerPacker
    {
        public PlayerID player;
        public BitPacker packer;
        public bool fullFrame;
        public ulong preparedFrameTick;
        public int maxUnreliableFrameBytes;
        public ReliableFrameDeliveryState reliableFrame;
        public IdentityEstablishmentState identityEstablishment;
        public RootSendBaselineState sendBaselines;
        public InterestControlState interestControls;

        public void Dispose()
        {
            packer?.Dispose();
            preparedFrameTick = 0;
            reliableFrame.Clear();
            identityEstablishment?.Clear();
            sendBaselines?.Clear();
            interestControls?.Clear();
        }
    }
}
