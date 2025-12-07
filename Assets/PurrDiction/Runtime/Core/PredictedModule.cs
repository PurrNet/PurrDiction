using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction
{
    public readonly struct ModuleDeltaKey<T> : IStableHashable
    {
        private readonly PredictedComponentID id;
        private readonly int moduleIndex;

        public ModuleDeltaKey(PredictedComponentID id, int moduleIndex)
        {
            this.id = id;
            this.moduleIndex = moduleIndex;
        }

        public uint GetStableHash()
        {
            return Hasher<T>.stableHash ^ id.componentId.value ^ id.objectId.instanceId.value ^ (uint)moduleIndex;
        }
    }

    public abstract class PredictedModule
    {
        public PredictedIdentity identity { get; private set; }
        public PredictionManager manager { get; private set; }
        
        /// <summary>
        /// The index of this module within the parent identity's module list.
        /// Used for network identification.
        /// </summary>
        public int moduleIndex { get; internal set; }

        public PredictedModule(PredictedIdentity identity)
        {
            this.identity = identity;
            this.manager = identity.predictionManager;
        
            identity.RegisterModule(this);
        
            InitializeLifecycle();
        }
        
        private void InitializeLifecycle()
        {
            OnCoreInitialize();
            OnInitialize();
        }
        
        internal virtual void OnCoreInitialize() { }

        /// <summary>
        /// Called immediately after the module is constructed and registered.
        /// Use this for initialization logic that requires the identity or manager to be set.
        /// </summary>
        protected virtual void OnInitialize() { }

        internal void SetupInternal(PredictedIdentity parent, PredictionManager world) => Setup(parent, world);
        
        /// <summary>
        /// Called when the parent Identity is being set up by the PredictionManager.
        /// </summary>
        protected virtual void Setup(PredictedIdentity parent, PredictionManager world) { }

        internal void SimulateInternal(ulong tick, float delta) => Simulate(tick, delta);
        
        /// <summary>
        /// Executes the main simulation logic for this module for a specific tick.
        /// This happens during the fixed time step.
        /// </summary>
        /// <param name="tick">The current tick being simulated.</param>
        /// <param name="delta">The fixed time delta for this tick.</param>
        protected virtual void Simulate(ulong tick, float delta) { }
        internal void LateSimulateInternal(float delta) => LateSimulate(delta);
        
        /// <summary>
        /// Executed after the main simulation and physics pass. 
        /// Use this for logic that reacts to the results of the physics step.
        /// </summary>
        protected virtual void LateSimulate(float delta) { }
        internal void RollbackInternal(ulong tick) => Rollback(tick);
        
        /// <summary>
        /// Restores the module's state to what it was at the specified tick.
        /// Used during prediction rollback to correct mispredictions.
        /// </summary>
        protected abstract void Rollback(ulong tick);
        internal void SaveStateInternal(ulong tick) => SaveState(tick);
        
        /// <summary>
        /// Saves the current state of the module into history for the specified tick.
        /// </summary>
        protected abstract void SaveState(ulong tick);
        internal bool WriteStateInternal(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)=> WriteState(receiver, packer, deltaModule);
        
        /// <summary>
        /// Serializes the current state of the module for network transmission.
        /// </summary>
        /// <returns>True if any data was written (i.e., state changed), otherwise False.</returns>
        protected abstract bool WriteState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule);
        internal void ReadStateInternal(ulong tick, BitPacker packer, DeltaModule deltaModule) => ReadState(tick, packer, deltaModule);
        
        /// <summary>
        /// Deserializes incoming network data and applies it to the module's state.
        /// </summary>
        protected abstract void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule);

        internal void WriteFirstStateInternal(ulong tick, BitPacker packer) => WriteFirstState(tick, packer);
        
        /// <summary>
        /// Writes the full initial state of the module for a new observer.
        /// Unlike WriteState, this does not use delta compression.
        /// </summary>
        protected abstract void WriteFirstState(ulong tick, BitPacker packer);
        internal void ReadFirstStateInternal(ulong tick, BitPacker packer) => ReadFirstState(tick, packer);
        
        /// <summary>
        /// Reads the full initial state of the module.
        /// </summary>
        protected abstract void ReadFirstState(ulong tick, BitPacker packer);
        internal void ClearFutureInternal(ulong tick) => ClearFuture(tick);
        
        /// <summary>
        /// Clears any history or state recorded after the specified tick.
        /// </summary>
        protected abstract void ClearFuture(ulong tick);
        internal void UpdateViewInternal(float delta) => UpdateView(delta);
        
        /// <summary>
        /// Called every frame (Update or LateUpdate) to handle visual interpolation or rendering logic.
        /// </summary>
        /// <param name="delta">Time since last frame.</param>
        protected virtual void UpdateView(float delta) { }
        internal void UpdateInterpolationInternal(float delta, bool accumulateError) => UpdateInterpolation(delta, accumulateError);
        
        /// <summary>
        /// Updates the interpolation state used for smooth visual rollback.
        /// </summary>
        /// <param name="accumulateError">If true, the difference between predicted and actual state is added to an error accumulator for smoothing.</param>
        protected virtual void UpdateInterpolation(float delta, bool accumulateError) { }
        internal void ResetInterpolationInternal() => ResetInterpolation();
        
        /// <summary>
        /// Snaps the visual state to the authoritative state, bypassing interpolation.
        /// Typically called on teleport or spawn.
        /// </summary>
        protected virtual void ResetInterpolation() { }
    }
}