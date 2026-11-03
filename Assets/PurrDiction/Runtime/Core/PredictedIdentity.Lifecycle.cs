using System;
using System.Collections.Generic;
using System.Reflection;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity
    {
        internal const byte LateSimulatePhase = 1;
        internal const byte PostSimulatePhase = 2;
        internal const byte AllSimulationPhases = LateSimulatePhase | PostSimulatePhase;

        private static readonly Dictionary<Type, byte> s_typePhases = new ();

        // The simulation hooks that do something for this identity: those its type implements,
        // plus LateSimulate for its modules. Phases outside this mask run nothing.
        internal byte simulationPhases
        {
            get
            {
                var type = GetType();
                if (!s_typePhases.TryGetValue(type, out byte phases))
                {
                    phases = ResolveSimulationPhases(type);
                    s_typePhases[type] = phases;
                }
                return _modules.Count > 0 ? (byte)(phases | LateSimulatePhase) : phases;
            }
        }

        private bool _hadModules;

        // Called after every change to _modules; the mask only depends on whether there are any.
        private void OnModulesChanged()
        {
            bool hasModules = _modules.Count > 0;
            if (hasModules == _hadModules)
                return;
            _hadModules = hasModules;
            if (predictionManager is { } manager)
                manager.RefreshSystemPhases(this);
        }

        private static byte ResolveSimulationPhases(Type type)
        {
            byte phases = 0;
            var post = type.GetMethod(nameof(PostSimulate), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (post == null || post.DeclaringType != typeof(PredictedIdentity))
                phases |= PostSimulatePhase;
            if (!HasEmptyLateSimulate(type))
                phases |= LateSimulatePhase;
            return phases;
        }

        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // True only for the built-in LateSimulateTick implementations whose user hook keeps its
        // empty default; any other or unrecognized implementation runs.
        private static bool HasEmptyLateSimulate(Type type)
        {
            var tick = type.GetMethod(nameof(LateSimulateTick), AnyInstance, null, new[] { typeof(float) }, null);
            if (tick == null)
                return false;
            var owner = tick.DeclaringType;
            if (owner == null)
                return false;
            if (owner == typeof(StaticPredictedIdentity))
                return true;
            if (owner == typeof(StatelessPredictedIdentity))
                return DeclaredBy(type, "LateSimulate", owner, typeof(float));
            if (!owner.IsGenericType)
                return false;

            var definition = owner.GetGenericTypeDefinition();
            var arguments = owner.GetGenericArguments();
            if (definition == typeof(PredictedIdentity<>))
                return DeclaredBy(type, "LateSimulate", owner, arguments[0].MakeByRefType(), typeof(float));
            if (definition == typeof(PredictedIdentity<,>))
                return DeclaredBy(type, "LateSimulate", owner, arguments[0], arguments[1].MakeByRefType(), typeof(float));
            if (definition == typeof(DeterministicIdentity<>))
                return DeclaredBy(type, "LateSimulate", owner, arguments[0].MakeByRefType(), typeof(sfloat));
            if (definition == typeof(DeterministicIdentity<,>))
                return DeclaredBy(type, "LateSimulate", owner, arguments[0], arguments[1].MakeByRefType(), typeof(sfloat));
            return false;
        }

        // The most derived override of name(parameters) is still the empty one on owner.
        private static bool DeclaredBy(Type type, string name, Type owner, params Type[] parameters)
        {
            var method = type.GetMethod(name, AnyInstance, null, parameters, null);
            return method != null && method.DeclaringType == owner;
        }

        // A skip only ownership, the runtime policy, the manager or its stable local player can
        // lift. Each of those advances SimulationSkipEpoch, so a pass that saw this identity
        // skipped need not ask again until the epoch moves.
        internal bool HasStableSimulationSkip()
        {
            var policy = predictionPolicy;
            if (policy == PredictionPolicy.ServerRelay)
                return true;
            var manager = predictionManager;
            return policy == PredictionPolicy.PredictedIfOwned && manager is not null && manager.hasStableLocalPlayer;
        }

        internal void RunSimulateTick(ulong tick, float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            var manager = predictionManager;
            var previousCreator = manager.EnterSpawnCreator(id);
            var previousViewer = manager.EnterLagCompensationViewer(owner);
            try
            {
                SimulateModules(tick, delta);
                SimulateTick(tick, delta);
            }
            finally
            {
                manager.ExitLagCompensationViewer(previousViewer);
                manager.ExitSpawnCreator(previousCreator);
            }
        }

        internal void RunLateSimulateTick(float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            var manager = predictionManager;
            var previousCreator = manager.EnterSpawnCreator(id);
            var previousViewer = manager.EnterLagCompensationViewer(owner);
            try
            {
                LateSimulateModules(delta);
                LateSimulateTick(delta);
            }
            finally
            {
                manager.ExitLagCompensationViewer(previousViewer);
                manager.ExitSpawnCreator(previousCreator);
            }
        }

        internal void RunPrepareSimulationInputs(ulong tick, float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            OnPrepareSimulationInputs(tick, delta);
        }

        internal void RunPostSimulate()
        {
            if (SkipsCurrentSimulationPhase())
                return;

            PostSimulate();
        }

        internal void RunGetLatestUnityState()
        {
            if (SkipsCurrentSimulationPhase())
                return;

            GetLatestUnityState();
        }

        internal void RunUpdateView(float deltaTime)
        {
            UpdateView(deltaTime);
            UpdateModuleView(deltaTime);
        }

        internal void RunLateUpdateView(float deltaTime)
        {
            LateUpdateView(deltaTime);
            LateUpdateModuleView(deltaTime);
        }

        internal void RunRollback(ulong tick)
        {
            RollbackDynamicModules(tick);
            RollbackModules(tick);
            Rollback(tick);
        }

        internal void RunSaveState(ulong tick)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            RunSaveStateUnchecked(tick);
        }

        internal void RunSaveStateUnchecked(ulong tick)
        {
            PredictionHistoryTelemetry.RecordSave(isEventHandler);
            SaveModulesState(tick);
            SaveStateInHistory(tick);
            SaveDynamicModuleSnapshot(tick);
        }

        internal void RunUpdateRollbackInterpolation(float delta, bool accumulateError)
        {
            bool shouldAccumulateError = accumulateError && AccumulatesRollbackInterpolationError();
            UpdateModulesInterpolation(delta, shouldAccumulateError);
            UpdateRollbackInterpolationState(delta, shouldAccumulateError);
        }

        internal void RunResetInterpolation()
        {
            ResetModulesInterpolation();
            ResetInterpolation();
        }

        internal bool RunWriteCurrentState(PlayerID receiver, BitPacker packer, ulong baselineTick)
        {
            bool moduleSetChanged = WriteDynamicModuleSnapshot(receiver, packer, baselineTick);
            bool modulesChanged = WriteModules(receiver, packer, baselineTick);
            bool identityChanged = WriteCurrentState(receiver, packer, baselineTick);

            return moduleSetChanged || modulesChanged || identityChanged;
        }

        internal bool RunCanOmitUnchangedState(ulong baselineTick)
        {
            return HasUnchangedStateBaseline(baselineTick) &&
                   HasUnchangedStateBaselineModules(baselineTick);
        }

        internal bool RunCanReadUnchangedState(ulong baselineTick)
        {
            return lastVerifiedTick.HasValue &&
                   lastVerifiedTick.Value >= baselineTick &&
                   HasUnchangedStateBaseline(baselineTick) &&
                   HasUnchangedStateBaselineModules(baselineTick);
        }

        internal void RunReadUnchangedState(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            ReadUnchangedDynamicModuleSnapshot(tick, baselineTick, serverTick);
            ReadUnchangedModules(tick, baselineTick, serverTick);
            ReadUnchangedState(tick, baselineTick, serverTick);
        }

        internal void RunReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            ReadDynamicModuleSnapshot(tick, packer, baselineTick, serverTick);
            ReadModules(tick, packer, baselineTick, serverTick);
            ReadState(tick, packer, baselineTick, serverTick);
        }

        internal void RunWriteFirstState(ulong tick, BitPacker packer)
        {
            WriteFirstDynamicModuleSnapshot(tick, packer);
            WriteFirstStateModules(tick, packer);
            WriteFirstState(tick, packer);
        }

        internal void RunReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            ReadFirstDynamicModuleSnapshot(tick, packer, serverTick);
            ReadFirstStateModules(tick, packer, serverTick);
            ReadFirstState(tick, packer, serverTick);
        }

        internal void RunClearFuture(ulong tick)
        {
            ClearFutureDynamicModules(tick);
            ClearFutureModules(tick);
            ClearFuture(tick);
        }
    }

    // Advances whenever an input to an identity's simulation-phase skip changes.
    internal static class SimulationSkipEpoch
    {
        internal static int value;
    }
}
