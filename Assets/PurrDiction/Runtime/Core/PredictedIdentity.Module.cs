using System.Collections.Generic;
using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity : MonoBehaviour
    {
        private readonly List<PredictedModule> _modules = new();

        protected void ModuleSetup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            for (int i = 0; i < _modules.Count; i++)
                _modules[i].Setup(this, world);
        }
        
        public T RegisterModule<T>(T module) where T : PredictedModule
        {
            module.moduleIndex = _modules.Count;
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

        protected bool WriteModules(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            bool didWriteAny = false;
            for (int i = 0; i < _modules.Count; i++)
            {
                didWriteAny |= _modules[i].WriteState(receiver, packer, deltaModule); 
            }
            return didWriteAny;
        }

        protected void ReadModules(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            for (int i = 0; i < _modules.Count; i++) 
            {
                _modules[i].ReadState(tick, packer, deltaModule);
            }
        }

        protected void UpdateModulesInterpolation(float delta, bool accumulateError)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].UpdateInterpolation(delta, accumulateError);
        }

        protected void ResetModulesInterpolation()
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].ResetInterpolation();
        }
        
        protected void WriteFirstStateModules(ulong tick, BitPacker packer)
        {
            for (int i = 0; i < _modules.Count; i++) 
            {
                _modules[i].WriteFirstState(packer); 
            }
        }

        protected void ReadFirstStateModules(ulong tick, BitPacker packer)
        {
            for (int i = 0; i < _modules.Count; i++) 
            {
                _modules[i].ReadFirstState(tick, packer); 
            }
        }
        
        protected void ClearFutureModules(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++) 
            {
                _modules[i].ClearFuture(tick); // Assuming modules have history to clear
            }
        }
    }
}