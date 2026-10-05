using System;
using System.Collections.Generic;
using PurrNet.Packing;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        static readonly ProfilerMarker BuildVisibilityPhysics3DProjectionMarker =
            new("PredictionManager.BuildVisibilityPhysics3DProjection");
        static readonly ProfilerMarker BuildVisibilityPhysics2DProjectionMarker =
            new("PredictionManager.BuildVisibilityPhysics2DProjection");

        sealed class HiddenPiecesScratch
        {
            public ulong cachedAtLocalTick = ulong.MaxValue;
            public ulong tickA = ulong.MaxValue;
            public ulong tickB = ulong.MaxValue;
            public readonly HashSet<PredictedObjectID> piecesA = new ();
            public readonly HashSet<PredictedObjectID> piecesB = new ();
        }

        readonly Dictionary<PlayerID, HiddenPiecesScratch> _hiddenPiecesScratchByPlayer = new ();

        // The 3D and 2D writers share hidden-piece scans for the current and baseline ticks.
        HashSet<PredictedObjectID> GetHiddenPiecesAt(
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            in PredictedHierarchyState hierarchyState,
            ulong tick)
        {
            if (!_hiddenPiecesScratchByPlayer.TryGetValue(receiver, out var scratch))
            {
                scratch = new HiddenPiecesScratch();
                _hiddenPiecesScratchByPlayer[receiver] = scratch;
            }

            if (scratch.cachedAtLocalTick != localTick)
            {
                scratch.cachedAtLocalTick = localTick;
                scratch.tickA = ulong.MaxValue;
                scratch.tickB = ulong.MaxValue;
            }

            if (scratch.tickA == tick)
                return scratch.piecesA;
            if (scratch.tickB == tick)
                return scratch.piecesB;

            HashSet<PredictedObjectID> result;
            if (scratch.tickA == ulong.MaxValue)
            {
                scratch.tickA = tick;
                result = scratch.piecesA;
            }
            else
            {
                scratch.tickB = tick;
                result = scratch.piecesB;
            }

            result.Clear();
            CollectHiddenPieces(hierarchyState, timeline, tick, result);
            return result;
        }

        bool TryWriteAggregateVisibilityState(
            PredictedIdentity system,
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            BitPacker payload,
            ulong tick,
            ulong baselineTick,
            ref bool writeFull,
            out bool changed)
        {
            changed = false;
            if (!hierarchy)
                return false;

#if UNITY_PHYSICS_3D
            if (system == physics3d)
            {
                changed = WritePhysics3DVisibilityState(
                    receiver,
                    timeline,
                    payload,
                    tick,
                    baselineTick,
                    ref writeFull);
                return true;
            }
#endif

#if UNITY_PHYSICS_2D
            if (system == physics2d)
            {
                changed = WritePhysics2DVisibilityState(
                    receiver,
                    timeline,
                    payload,
                    tick,
                    baselineTick,
                    ref writeFull);
                return true;
            }
#endif

            return false;
        }

        static void CollectHiddenPieces(
            in PredictedHierarchyState hierarchyState,
            PlayerVisibilityTimeline timeline,
            ulong tick,
            HashSet<PredictedObjectID> result)
        {
            if (!hierarchyState.spawnedPrefabs.isDisposed)
            {
                bool hasPreviousRoot = false;
                bool previousRootVisible = false;
                PredictedObjectID previousRoot = default;

                for (var i = 0; i < hierarchyState.spawnedPrefabs.Count; i++)
                {
                    var record = hierarchyState.spawnedPrefabs[i];
                    var rootId = record.rootId;
                    if (!hasPreviousRoot || !rootId.Equals(previousRoot))
                    {
                        previousRoot = rootId;
                        previousRootVisible = timeline.WasVisibleAt(rootId, tick);
                        hasPreviousRoot = true;
                    }

                    if (!previousRootVisible)
                        result.Add(record.instanceId);
                }
            }

            timeline.CollectHiddenRootsAt(tick, result);
        }

#if UNITY_PHYSICS_3D
        bool WritePhysics3DVisibilityState(
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            BitPacker payload,
            ulong tick,
            ulong baselineTick,
            ref bool writeFull)
        {
            physics3d.RefreshVerifiedFromLive(tick);
            if (!physics3d.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedPhysicsData currentGlobal))
            {
                throw new InvalidOperationException(
                    $"No authoritative 3D physics state exists for tick {tick}.");
            }

            if (timeline.isPassThrough)
            {
                if (!writeFull &&
                    physics3d.TryGetVerifiedState(
                        baselineTick,
                        out var directBaselinePrediction,
                        out PredictedPhysicsData directBaselineGlobal))
                {
                    return physics3d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        directBaselinePrediction,
                        directBaselineGlobal,
                        currentGlobal);
                }

                writeFull = true;
                physics3d.RunWriteFirstProjectedState(
                    tick,
                    payload,
                    currentGlobal);
                return true;
            }

            if (!hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState currentHierarchy))
            {
                throw new InvalidOperationException(
                    $"No authoritative hierarchy state exists for 3D physics tick {tick}.");
            }

            PredictedPhysicsData currentProjection;
            using (BuildVisibilityPhysics3DProjectionMarker.Auto())
            {
                currentProjection = PredictionPhysicsVisibility.Project(
                    currentGlobal,
                    GetHiddenPiecesAt(receiver, timeline, currentHierarchy, tick));
            }
            PredictedPhysicsData baselineProjection = default;
            bool changed;

            try
            {
                if (!writeFull &&
                    physics3d.TryGetVerifiedState(
                        baselineTick,
                        out var baselinePrediction,
                        out PredictedPhysicsData baselineGlobal) &&
                    hierarchy.TryGetVerifiedState(
                        baselineTick,
                        out _,
                        out PredictedHierarchyState baselineHierarchy))
                {
                    using (BuildVisibilityPhysics3DProjectionMarker.Auto())
                    {
                        baselineProjection = PredictionPhysicsVisibility.Project(
                            baselineGlobal,
                            GetHiddenPiecesAt(
                                receiver,
                                timeline,
                                baselineHierarchy,
                                baselineTick));
                    }
                    changed = physics3d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        baselinePrediction,
                        baselineProjection,
                        currentProjection);
                }
                else
                {
                    writeFull = true;
                    physics3d.RunWriteFirstProjectedState(
                        tick,
                        payload,
                        currentProjection);
                    changed = true;
                }
            }
            finally
            {
                baselineProjection.Dispose();
                currentProjection.Dispose();
            }

            return changed;
        }
#endif

#if UNITY_PHYSICS_2D
        bool WritePhysics2DVisibilityState(
            PlayerID receiver,
            PlayerVisibilityTimeline timeline,
            BitPacker payload,
            ulong tick,
            ulong baselineTick,
            ref bool writeFull)
        {
            physics2d.RefreshVerifiedFromLive(tick);
            if (!physics2d.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedPhysics2DData currentGlobal))
            {
                throw new InvalidOperationException(
                    $"No authoritative 2D physics state exists for tick {tick}.");
            }

            if (timeline.isPassThrough)
            {
                if (!writeFull &&
                    physics2d.TryGetVerifiedState(
                        baselineTick,
                        out var directBaselinePrediction,
                        out PredictedPhysics2DData directBaselineGlobal))
                {
                    return physics2d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        directBaselinePrediction,
                        directBaselineGlobal,
                        currentGlobal);
                }

                writeFull = true;
                physics2d.RunWriteFirstProjectedState(
                    tick,
                    payload,
                    currentGlobal);
                return true;
            }

            if (!hierarchy.TryGetVerifiedState(
                    tick,
                    out _,
                    out PredictedHierarchyState currentHierarchy))
            {
                throw new InvalidOperationException(
                    $"No authoritative hierarchy state exists for 2D physics tick {tick}.");
            }

            PredictedPhysics2DData currentProjection;
            using (BuildVisibilityPhysics2DProjectionMarker.Auto())
            {
                currentProjection = PredictionPhysicsVisibility.Project(
                    currentGlobal,
                    GetHiddenPiecesAt(receiver, timeline, currentHierarchy, tick));
            }
            PredictedPhysics2DData baselineProjection = default;
            bool changed;

            try
            {
                if (!writeFull &&
                    physics2d.TryGetVerifiedState(
                        baselineTick,
                        out var baselinePrediction,
                        out PredictedPhysics2DData baselineGlobal) &&
                    hierarchy.TryGetVerifiedState(
                        baselineTick,
                        out _,
                        out PredictedHierarchyState baselineHierarchy))
                {
                    using (BuildVisibilityPhysics2DProjectionMarker.Auto())
                    {
                        baselineProjection = PredictionPhysicsVisibility.Project(
                            baselineGlobal,
                            GetHiddenPiecesAt(
                                receiver,
                                timeline,
                                baselineHierarchy,
                                baselineTick));
                    }
                    changed = physics2d.RunWriteProjectedState(
                        receiver,
                        payload,
                        baselineTick,
                        baselinePrediction,
                        baselineProjection,
                        currentProjection);
                }
                else
                {
                    writeFull = true;
                    physics2d.RunWriteFirstProjectedState(
                        tick,
                        payload,
                        currentProjection);
                    changed = true;
                }
            }
            finally
            {
                baselineProjection.Dispose();
                currentProjection.Dispose();
            }

            return changed;
        }
#endif
    }
}
