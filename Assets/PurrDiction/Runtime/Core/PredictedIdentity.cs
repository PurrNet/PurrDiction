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
        public PredictedID id;

        internal bool isFreshSpawn = true;

        internal virtual bool isEventHandler => false;

        [UsedByIL]
        public bool IsSimulating()
        {
            return predictionManager.isSimulating;
        }

        /// <summary>
        /// Invoked immediately after the object is fully initialized and fresh spawned.
        /// </summary>
        protected virtual void OnSpawned() {}

        /// <summary>
        /// Invoked when the object is being despawned and cleaned up.
        /// Allows for any necessary teardown or resource release to be handled.
        /// </summary>
        protected virtual void OnDespawned() {}

        public bool isServer { get; private set; }

        internal virtual void Setup(NetworkManager manager, PredictionManager world, uint id)
        {
            isServer = manager.isServer;

            if (!isFreshSpawn)
                return;

            isFreshSpawn = false;

            owner = null;
            this.id = new PredictedID(id);
            predictionManager = world;

            OnSpawned();
        }

        protected virtual void OnDestroy()
        {
            OnDespawned();

            if (predictionManager)
                predictionManager.UnregisterInstance(this);
        }

        public bool isOwner => IsOwner();

        public bool isController => owner.HasValue ? owner == predictionManager.localPlayer : isServer;

        public bool IsOwner()
        {
            if (!predictionManager)
                return false;

            return owner == predictionManager.localPlayer;
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
                return owner == player;
            return asServer;
        }

        internal abstract void SimulateTick(ulong tick, float delta);

        public virtual void PostSimulate(ulong tick, float delta) {}

        internal abstract void PrepareInput(bool isServer, bool isLocal, ulong tick);

        internal abstract void SimulateRemote(ulong tick, float delta);

        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(float delta, bool accumulateError);

        public abstract void ResetInterpolation();

        internal abstract void UpdateView(float deltaTime);

        internal abstract void GetLatestUnityState();

        internal abstract void WriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule, ref PackedUInt cache);

        internal abstract void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, ref PackedUInt cache);

        internal abstract void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule, ref PackedUInt cache);

        internal abstract void ReadInput(ulong tick, BitPacker packer, DeltaModule deltaModule, ref PackedUInt cache);

        internal abstract void QueueInput(PlayerID sender, BitPacker packer, DeltaModule deltaModule, ref PackedUInt cache);

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
    }
}
