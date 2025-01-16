using System;
using System.Collections.Generic;
using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Transports;
using UnityEngine;

namespace PurrNet.Prediction
{
    [DefaultExecutionOrder(-1000)]
    public class PredictionManager : NetworkIdentity, IServerSceneEvents
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();
        
        static readonly Dictionary<int, PredictionManager> _instances = new ();
        
        [SerializeField] private int _maxInputQueue = 4;
        [SerializeField] private GameObject[] _prefabs;
        
        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();
        
        public int maxInputQueue => _maxInputQueue;

        public static bool TryGetInstance(int sceneHandle, out PredictionManager world)
        {
            return _instances.TryGetValue(sceneHandle, out world);
        }

        private void Awake()
        {
            _instances[gameObject.scene.handle] = this;
        }

        public Fix64 tickDelta { get; private set; }

        public int tickRate { get; private set; }
        
        public ulong localTick { get; private set; } = 1;

        public PredictedHierarchy hierarchy { get; private set; }
        
        readonly Dictionary<PlayerID, PredictedObjectID> _objectIds = new ();
        
        public void OnPlayerLoadedScene(PlayerID playerId)
        {
            // if (playerId == localPlayer) return;
            var gid = hierarchy.Create(0);
            if (gid.HasValue)
            {
                _objectIds[playerId] = gid.Value;
                SetOwnership(gid, playerId);
            }
        }

        public void OnPlayerUnloadedScene(PlayerID playerId)
        {
            if (_objectIds.Remove(playerId, out var gid))
                hierarchy.Delete(gid);
        }
        
        protected override void OnEarlySpawn()
        {
            tickRate = networkManager.tickModule.tickRate;
            tickDelta = 1 / (Fix64)tickRate;
            hierarchy = RegisterSystem<PredictedHierarchy>();
            
            for (var i = 0; i < _queue.Count; i++)
            {
                var queued = _queue[i];
                RegisterInstance(queued);
            }
            
            _queue.Clear();
        }

        protected override void OnSpawned()
        {
            networkManager.tickModule.onPreTick += OnTick;
        }

        protected override void OnDespawned()
        {
            networkManager.tickModule.onPreTick -= OnTick;
        }

        public T RegisterSystem<T>() where T : PredictedIdentity
        {
            var system = gameObject.AddComponent<T>();
            system.hideFlags = HideFlags.NotEditable;
            return system;
        }
        
        internal void RegisterInstance(PredictedIdentity system)
        {
            if (!isSpawned)
            {
                _queue.Add(system);
                return;
            }
            
            system.Setup(networkManager, this);
            _systems.Add(system);
        }
        
        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            _systems.Remove(predictedIdentity);
        }

        protected override void OnObserverAdded(PlayerID player)
        {
            if (player == localPlayer)
                return;
            
            using var state = BitPackerPool.Get();
            int count = _systems.Count;

            for (var systemIdx = 0; systemIdx < count; systemIdx++)
            {
                var system = _systems[systemIdx];
                system.WriteLatestState(state);
            }

            if (state.positionInBits > 0)
                SyncFullState(player, tickRate, tickDelta.RawValue, state);
        }
        
        [TargetRpc]
        private void SyncFullState([UsedImplicitly] PlayerID target, int tickRate, long delta, BitPacker data)
        {
            tickDelta = Fix64.FromRaw(delta);
            this.tickRate = tickRate;
            
            for (var systemIdx = 0; systemIdx < _systems.Count; systemIdx++)
            {
                var system = _systems[systemIdx];
                system.ReadState(localTick, data);
                system.Rollback(localTick);
                system.ResetInterpolation();
            }
        }

        void OnTick()
        {
            using var frame = BitPackerPool.Get();
            var myPlayer = localPlayer ?? default;
            var cachedIsServer = isServer;
            
            for (var systemIdx = 0; systemIdx < _systems.Count; systemIdx++)
            {
                var system = _systems[systemIdx];
                bool controller = system.IsOwner(myPlayer, cachedIsServer);
                
                // prepare and simulate the system
                if (controller)
                {
                    system.EvaluateAndRegisterLocalInput(localTick);
                    system.SimulateLocal(tickDelta);
                }
                else
                {
                     system.SimulateRemote(tickDelta);
                }
            }

            var count = _systems.Count;
            
            if (cachedIsServer)
            {
                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.PostSimulate(localTick);
                    system.UpdateInterpolationState();
                    system.WriteState(localTick, frame);
                }
                
                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.WriteInput(localTick, frame);
                }
            }
            else
            {
                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    
                    if (system.IsOwner(myPlayer))
                    {
                        system.UpdateInterpolationState();
                        system.WriteInput(localTick, frame);
                    }
                }
            }

            if (cachedIsServer)
            {
                if (frame.positionInBits > 0)
                {
                    foreach (var (player, queue) in _clientTicks)
                    {
                        if (player == localPlayer)
                            continue;
                        
                        SendFrameToRemote(player, queue.Count > 0 ? queue.Dequeue() : 0, frame);
                    }
                }
            }
            else SendInputToServer(localTick, frame);
            
            localTick += 1;
        }
        
        [TargetRpc]
        private void SendFrameToRemote([UsedImplicitly] PlayerID player, ulong clientLocalTick, BitPacker frame)
        {
            if (clientLocalTick == 0)
                return;
            
            using (frame)
            {
                for (var i = 0; i < _systems.Count; i++)
                {
                    var system = _systems[i];
                    system.ReadState(clientLocalTick, frame);
                    system.Rollback(clientLocalTick);
                    system.UpdateInterpolationState();
                }
                
                for (var i = 0; i < _systems.Count; i++)
                    _systems[i].ReadInput(clientLocalTick, frame);
            }
            
            CatchupFromTick(clientLocalTick);
        }

        private void CatchupFromTick(ulong clientTick)
        {
            for (ulong simTick = clientTick + 1; simTick < localTick; simTick++)
            {
                for (var j = 0; j < _systems.Count; j++)
                    _systems[j].SimulateTick(simTick, tickDelta);

                var count = _systems.Count;

                for (var j = 0; j < count; j++)
                {
                    var system = _systems[j];
                    if (system.IsOwner())
                        system.UpdateInterpolationState();
                }
            }
        }

        readonly Dictionary<PlayerID, Queue<ulong>> _clientTicks = new ();

        [ServerRpc]
        private void SendInputToServer(ulong clientTick, BitPacker inputPacket, RPCInfo info = default)
        {
            using (inputPacket)
                HandleIncomingInput(inputPacket, info);
            
            if (!_clientTicks.TryGetValue(info.sender, out var ticks))
            {
                ticks = new Queue<ulong>();
                _clientTicks[info.sender] = ticks;
            }

            ticks.Enqueue(clientTick);

            while (ticks.Count > _maxInputQueue)
                ticks.Dequeue();
        }

        private void HandleIncomingInput(BitPacker inputPacket,
            RPCInfo info = default)
        {
            try
            {
                bool senderIsServer = info.sender == default;
                
                for (var i = 0; i < _systems.Count; i++)
                {
                    var system = _systems[i];
                    if (system.IsOwner(info.sender, senderIsServer))
                        system.QueueInput(inputPacket);
                }
            }
            catch
            {
                // ignored
            }
        }

        private void LateUpdate()
        {
            int count = _systems.Count;
            
            for (var i = 0; i < count; i++)
                _systems[i].UpdateView(Time.deltaTime);
        }

        public bool TryGetPrefab(int pid, out GameObject prefab)
        {
            if (pid < 0 || pid >= _prefabs.Length)
            {
                prefab = null;
                return false;
            }
            
            prefab = _prefabs[pid];
            return true;
        }
        
        public bool TryGetPrefab(GameObject prefab, out int id)
        {
            id = Array.IndexOf(_prefabs, prefab);
            return id != -1;
        }

        internal GameObject InternalCreate(GameObject prefab)
        {
            return UnityProxy.InstantiateDirectly(prefab);
        }

        internal void InternalDelete(GameObject instance)
        {
            UnityProxy.DestroyDirectly(instance);
        }
        
        public void SetOwnership(PredictedObjectID? root, PlayerID? player)
        {
            if (!hierarchy.TryGetGameObject(root, out var rootGo))
                return;
            
            var children = ListPool<PredictedIdentity>.Instantiate();
            
            rootGo.GetComponentsInChildren(true, children);
            
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                child.owner = player;
            }
            
            ListPool<PredictedIdentity>.Destroy(children);
        }
    }
}
