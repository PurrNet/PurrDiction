using System.Collections.Generic;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity : MonoBehaviour
    {
        private readonly List<PredictedModule> _modules = new();

        public T RegisterModule<T>(T module) where T : PredictedModule
        {
            _modules.Add(module);
            if (predictionManager)
                module.Setup(this, predictionManager);
            return module;
        }

        protected void SimulateModules(ulong tick, float delta)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].Simulate(tick, delta);
        }

        protected void LateSimulateModules(float delta)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].LateSimulate(delta);
        }

        protected void RollbackModules(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].Rollback(tick);
        }

        protected void SaveModulesState(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].SaveState(tick);
        }

        protected void WriteModules(BitPacker packer, DeltaModule deltaModule)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].WriteState(packer, deltaModule);
        }

        protected void ReadModules(BitPacker packer, DeltaModule deltaModule)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].ReadState(packer, deltaModule);
        }

        protected void UpdateModulesInterpolation(float delta, bool accumulateError)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].UpdateInterpolation(delta, accumulateError);
        }

        protected void ResetModulesInterpolation()
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].ResetInterpolation();
        }
    }
}
