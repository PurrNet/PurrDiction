using System;
using System.Collections.Generic;

namespace PurrNet.Prediction
{
    internal readonly struct PredictionVisibilityTransition
    {
        public readonly ulong tick;
        public readonly bool visible;

        public PredictionVisibilityTransition(ulong tick, bool visible)
        {
            this.tick = tick;
            this.visible = visible;
        }
    }

    // Store only exceptions to default visibility and roots with actual transitions.
    internal sealed class PlayerVisibilityTimeline
    {
        readonly bool _defaultVisible;
        readonly HashSet<PredictedObjectID> _currentExceptions = new ();
        readonly Dictionary<PredictedObjectID, List<PredictionVisibilityTransition>> _transitions = new ();
        readonly HashSet<PredictedObjectID> _pruneCandidates = new ();
        readonly List<PredictedObjectID> _removedScratch = new ();

        ulong _lastPrunedTick;

        internal bool defaultVisible => _defaultVisible;
        internal bool isPassThrough =>
            _defaultVisible &&
            _currentExceptions.Count == 0 &&
            _transitions.Count == 0;
        internal int currentExceptionCount => _currentExceptions.Count;
        internal int trackedRootCount => _transitions.Count;
        internal int pruneCandidateCount => _pruneCandidates.Count;
        public ulong latestRecordedTick { get; private set; }
        public ulong latestTransitionTick { get; private set; }
        public uint revision { get; private set; }

        public PlayerVisibilityTimeline(bool defaultVisible = true)
        {
            _defaultVisible = defaultVisible;
        }

        public bool IsVisible(PredictedObjectID rootId)
        {
            return _defaultVisible != _currentExceptions.Contains(rootId);
        }

        internal void CollectCurrentExceptions(HashSet<PredictedObjectID> result)
        {
            foreach (var rootId in _currentExceptions)
                result.Add(rootId);
        }

        internal void CollectHiddenRootsAt(ulong tick, HashSet<PredictedObjectID> result)
        {
            if (!_defaultVisible)
                return;

            foreach (var rootId in _currentExceptions)
            {
                if (!result.Contains(rootId) && !WasVisibleAt(rootId, tick))
                    result.Add(rootId);
            }

            foreach (var rootId in _transitions.Keys)
            {
                if (!result.Contains(rootId) && !WasVisibleAt(rootId, tick))
                    result.Add(rootId);
            }
        }

        // Calls must use nondecreasing ticks. False also covers a canceled same-tick transition.
        public bool SetVisible(
            ulong tick,
            PredictedObjectID rootId,
            bool visible)
        {
            if (tick < latestRecordedTick)
            {
                throw new InvalidOperationException(
                    $"Visibility tick {tick} precedes the latest recorded tick " +
                    $"{latestRecordedTick}.");
            }

            latestRecordedTick = tick;
            if (IsVisible(rootId) == visible)
                return false;

            if (visible == _defaultVisible)
                _currentExceptions.Remove(rootId);
            else
                _currentExceptions.Add(rootId);

            bool hasNetTransition = AddTransition(rootId, tick, visible);
            revision++;
            return hasNetTransition;
        }

        public bool WasVisibleAt(PredictedObjectID rootId, ulong tick)
        {
            if (!_transitions.TryGetValue(rootId, out var transitions) ||
                transitions.Count == 0)
            {
                return _defaultVisible;
            }

            int low = 0;
            int high = transitions.Count - 1;
            int found = -1;

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (transitions[mid].tick <= tick)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return found >= 0 ? transitions[found].visible : _defaultVisible;
        }

        // Re-entry requires full state until a frame from the new visibility generation is ACKed.
        public bool HasContinuousVisibilityFrom(
            PredictedObjectID rootId,
            ulong baselineTick)
        {
            if (!IsVisible(rootId))
                return false;

            if (!_transitions.TryGetValue(rootId, out var transitions) ||
                transitions.Count == 0)
            {
                return _defaultVisible;
            }

            var latest = transitions[^1];
            return latest.visible && latest.tick <= baselineTick;
        }

        public bool HasTransitionAfter(ulong baselineTick)
        {
            return latestTransitionTick > baselineTick;
        }

        // Stable non-default exceptions add no per-tick pruning work.
        public void PruneThrough(ulong acknowledgedTick)
        {
            if (acknowledgedTick <= _lastPrunedTick ||
                _pruneCandidates.Count == 0)
            {
                return;
            }

            _lastPrunedTick = acknowledgedTick;
            _removedScratch.Clear();
            bool removedLatestTransition = false;

            foreach (var rootId in _pruneCandidates)
            {
                if (!_transitions.TryGetValue(rootId, out var transitions) ||
                    transitions.Count == 0)
                {
                    _removedScratch.Add(rootId);
                    continue;
                }

                int anchor = -1;
                for (var i = 0; i < transitions.Count; i++)
                {
                    if (transitions[i].tick > acknowledgedTick)
                        break;
                    anchor = i;
                }

                if (anchor < 0)
                    continue;

                if (transitions[anchor].visible == _defaultVisible)
                {
                    if (latestTransitionTick <= acknowledgedTick)
                        removedLatestTransition = true;
                    transitions.RemoveRange(0, anchor + 1);
                }
                else if (anchor > 0)
                {
                    transitions.RemoveRange(0, anchor);
                }

                if (transitions.Count == 0)
                {
                    _transitions.Remove(rootId);
                    _removedScratch.Add(rootId);
                }
                else if (transitions.Count == 1 &&
                         transitions[0].tick <= acknowledgedTick)
                {
                    _removedScratch.Add(rootId);
                }
            }

            for (var i = 0; i < _removedScratch.Count; i++)
                _pruneCandidates.Remove(_removedScratch[i]);

            if (removedLatestTransition)
                RecalculateLatestTransitionTick();
        }

        public void Clear()
        {
            _currentExceptions.Clear();
            _transitions.Clear();
            _pruneCandidates.Clear();
            _removedScratch.Clear();
            _lastPrunedTick = 0;
            latestRecordedTick = 0;
            latestTransitionTick = 0;
            revision = 0;
        }

        bool AddTransition(
            PredictedObjectID rootId,
            ulong tick,
            bool visible)
        {
            if (!_transitions.TryGetValue(rootId, out var transitions))
            {
                transitions = new List<PredictionVisibilityTransition>(4);
                _transitions.Add(rootId, transitions);
            }

            if (transitions.Count > 0 && transitions[^1].tick == tick)
            {
                bool stateBeforeTick = transitions.Count > 1
                    ? transitions[^2].visible
                    : _defaultVisible;

                if (stateBeforeTick == visible)
                {
                    transitions.RemoveAt(transitions.Count - 1);
                    if (transitions.Count == 0)
                    {
                        _transitions.Remove(rootId);
                        _pruneCandidates.Remove(rootId);
                    }
                    else if (transitions[^1].tick <= _lastPrunedTick)
                    {
                        _pruneCandidates.Remove(rootId);
                    }

                    RecalculateLatestTransitionTick();
                    return false;
                }

                transitions[^1] = new PredictionVisibilityTransition(tick, visible);
            }
            else
            {
                transitions.Add(new PredictionVisibilityTransition(tick, visible));
            }

            _pruneCandidates.Add(rootId);
            if (tick > latestTransitionTick)
                latestTransitionTick = tick;
            return true;
        }

        void RecalculateLatestTransitionTick()
        {
            ulong latest = 0;
            foreach (var transitions in _transitions.Values)
            {
                if (transitions.Count > 0 && transitions[^1].tick > latest)
                    latest = transitions[^1].tick;
            }

            latestTransitionTick = latest;
        }
    }
}
