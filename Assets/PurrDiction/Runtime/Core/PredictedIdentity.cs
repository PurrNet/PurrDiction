using PurrNet.Modules;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity : MonoBehaviour
    {
        public virtual string GetExtraString()
        {
            return string.Empty;
        }

        public PredictionManager predictionManager { get; protected set; }

        /// <summary>
        /// Represents the identifier of the owner associated with this object.
        /// Used to track ownership, enabling control over inputs.
        /// </summary>
        public PlayerID? owner;

        /// <summary>
        /// The unique identifier for this object.
        /// Can be used to identify the object across the network.
        /// </summary>
        public PredictedComponentID id;

        internal bool isFreshSpawn = true;

        public virtual bool hasInput => false;

        internal virtual bool isEventHandler => false;

        [UsedByIL]
        public bool IsSimulating()
        {
            return predictionManager.isSimulating;
        }

        public virtual void OnPreSetup() {  }

        public virtual void ResetState()
        {
            isServer = false;
            isFreshSpawn = true;
            owner = null;
            id = default;
            OnRemovedFromPool();
        }

        protected virtual void OnRemovedFromPool() {}

        protected virtual void OnAddedToPool() {}

        /// <summary>
        /// Invoked immediately after the object is fully initialized and fresh spawned.
        /// </summary>
        protected virtual void LateAwake() {}

        /// <summary>
        /// Invoked when the object is being despawned and cleaned up.
        /// Allows for any necessary teardown or resource release to be handled.
        /// </summary>
        protected virtual void Destroyed() {}

        internal void TriggerDestroyedEvent()
        {
            Destroyed();
        }

        public bool isServer { get; private set; }

        internal virtual void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            isServer = manager.isServer;
            this.owner = owner;
            this.id = id;

            if (!isFreshSpawn)
                return;

            isFreshSpawn = false;
            predictionManager = world;

            LateAwake();
        }

        protected virtual void OnDestroy()
        {
            Destroyed();

            if (predictionManager)
                predictionManager.UnregisterInstance(this);
        }

        public bool isOwner => IsOwner();

        public bool isController
        {
            get
            {
                if (owner.HasValue && predictionManager.isSpawned)
                    return owner == predictionManager.localPlayer;
                return predictionManager.cachedIsServer;
            }
        }

        public bool IsOwner()
        {
            if (predictionManager && predictionManager.isSpawned && owner == predictionManager.localPlayer)
                return true;
            return false;
        }

        public bool IsOwner(PlayerID player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID? player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID player, bool asServer)
        {
            if (owner.HasValue)
            {
                if (owner.Value.isBot)
                    return asServer;
                return owner == player;
            }
            return asServer;
        }

        internal abstract void SimulateTick(ulong tick, float delta);

        internal abstract void LateSimulateTick(float delta);

        public virtual void PostSimulate() {}

        internal abstract void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate);

        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(float delta, bool accumulateError);

        public abstract void ResetInterpolation();

        private PlayerID? _lastOwner;

        public virtual bool isDeterministic => false;

        /// <summary>
        /// Called once when owner changes
        /// This is meant to be used for view/visuals only and not part of the simulation
        /// </summary>
        public virtual void OnViewOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner) { }

        internal virtual void UpdateView(float deltaTime)
        {
            if (owner != _lastOwner)
            {
                OnViewOwnerChanged(_lastOwner, owner);
                _lastOwner = owner;
            }
        }

        internal abstract void GetLatestUnityState();

        internal abstract void WriteFirstState(ulong tick, BitPacker packer);

        internal abstract bool WriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule);

        internal abstract void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable);

        internal abstract void ReadFirstState(ulong tick, BitPacker packer);

        internal abstract void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule);

        internal abstract void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable);

        internal abstract void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable);

        public GameObject GetRoot()
        {
            // get the farthest root with a predicted identity
            var current = transform;

            while (current.parent != null)
            {
                if (current.parent.GetComponent<PredictedIdentity>() == null)
                    break;

                current = current.parent;
            }

            return current.gameObject;
        }

        internal void TriggerOnPooledEvent()
        {
            OnAddedToPool();
        }

        public abstract void WriteFirstInput(ulong localTick, BitPacker packer);

        public abstract void ReadFirstInput(ulong localTick, BitPacker packer);

        internal abstract void ClearFuture(ulong stateTick);
    }
}
