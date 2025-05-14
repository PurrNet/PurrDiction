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

        protected virtual void OnSpawned() {}
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

        internal abstract void WriteCurrentState(BitPacker packer);

        internal abstract void WriteInput(ulong localTick, BitPacker input);

        internal abstract void ReadState(ulong tick, BitPacker packer);

        internal abstract void ReadInput(ulong tick, BitPacker packer);

        internal abstract void QueueInput(BitPacker packer);

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
