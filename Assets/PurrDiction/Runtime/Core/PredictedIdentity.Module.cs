using System.Collections.Generic;
using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity
    {
        private readonly List<PredictedModule> _modules = new();

        protected void ModuleSetup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            for (int i = 0; i < _modules.Count; i++)
                _modules[i].SetupInternal(this, world);
        }

        public T RegisterModule<T>(T module) where T : PredictedModule
        {
            module.moduleIndex = _modules.Count;
            _modules.Add(module);
            if (predictionManager)
                module.SetupInternal(this, predictionManager);
            return module;
        }

        internal void UpdateModuleView(float deltaTime)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].UpdateViewInternal(deltaTime);
        }

        protected void SimulateModules(ulong tick, float delta)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].SimulateInternal(tick, delta);
        }

        protected void LateSimulateModules(float delta)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].LateSimulateInternal(delta);
        }

        protected void RollbackModules(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].RollbackInternal(tick);
        }

        protected void SaveModulesState(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].SaveStateInternal(tick);
        }

        protected bool WriteModules(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            bool didWriteAny = false;
            for (int i = 0; i < _modules.Count; i++)
            {
                didWriteAny |= _modules[i].WriteStateInternal(receiver, packer, deltaModule);
            }
            return didWriteAny;
        }

        protected void ReadModules(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].ReadStateInternal(tick, packer, deltaModule);
            }
        }

        protected void UpdateModulesInterpolation(float delta, bool accumulateError)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].UpdateInterpolationInternal(delta, accumulateError);
        }

        protected void ResetModulesInterpolation()
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].ResetInterpolationInternal();
        }

        protected void WriteFirstStateModules(ulong tick, BitPacker packer)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].WriteFirstStateInternal(tick, packer);
            }
        }

        protected void ReadFirstStateModules(ulong tick, BitPacker packer)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].ReadFirstStateInternal(tick, packer);
            }
        }

        protected void ClearFutureModules(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].ClearFutureInternal(tick);
            }
        }
    }
}
