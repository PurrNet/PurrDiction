using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity
    {
        internal void RunSimulateTick(ulong tick, float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            var previousCreator = predictionManager.EnterSpawnCreator(id);
            try
            {
                SimulateModules(tick, delta);
                SimulateTick(tick, delta);
            }
            finally
            {
                predictionManager.ExitSpawnCreator(previousCreator);
            }
        }

        internal void RunLateSimulateTick(float delta)
        {
            if (SkipsCurrentSimulationPhase())
                return;

            var previousCreator = predictionManager.EnterSpawnCreator(id);
            try
            {
                LateSimulateModules(delta);
                LateSimulateTick(delta);
            }
            finally
            {
                predictionManager.ExitSpawnCreator(previousCreator);
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
}
