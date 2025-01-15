using System;
using System.Collections.Generic;
using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Transports;
using UnityEngine;

namespace PurrNet.Prediction
{
    [DefaultExecutionOrder(-1000)]
    public class PredictionManager : NetworkIdentity, ITick, IServerSceneEvents
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();
        
        static readonly Dictionary<int, PredictionManager> _instances = new ();
        
        [SerializeField] private GameObject[] _prefabs;
        
        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();
        
        public static bool TryGetInstance(int sceneHandle, out PredictionManager world)
        {
            return _instances.TryGetValue(sceneHandle, out world);
        }

        private void Awake()
        {
            _instances[gameObject.scene.handle] = this;
        }

        private ulong _localTick;
        
        public Fix64 tickDelta { get; private set; }

        public int tickRate { get; private set; }
        
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
                SyncFullState(player, _localTick, tickRate, tickDelta.RawValue, state);
        }
        
        [TargetRpc]
        private void SyncFullState([UsedImplicitly] PlayerID target, ulong tick, int tickRate, long delta, BitPacker data)
        {
            _localTick = tick;
            tickDelta = Fix64.FromRaw(delta);
            this.tickRate = tickRate;
            
            for (var systemIdx = 0; systemIdx < _systems.Count; systemIdx++)
            {
                var system = _systems[systemIdx];
                system.ReadState(tick, data);
                system.Rollback(tick);
                system.ResetInterpolation();
            }
        }

        public void OnTick(float delta)
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
                    system.EvaluateLocalInput(_localTick);
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
                    system.PostSimulate(_localTick);
                    system.WriteState(_localTick, frame);
                }
                
                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.WriteInput(_localTick, frame);
                }
            }
            else
            {
                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    if (system.IsOwner(myPlayer))
                        system.WriteInput(_localTick, frame);
                }
            }

            if (frame.positionInBits > 0)
            {
                if (cachedIsServer)
                     SendFrameToOthers(_localTick, frame);
                else SendInputToServer(_localTick, frame);
            }
            
            _localTick += 1;
        }

        [ObserversRpc(excludeSender: true)]
        private void SendFrameToOthers(ulong tick, BitPacker frame)
        {
            using (frame)
            {
                for (var i = 0; i < _systems.Count; i++)
                {
                    var system = _systems[i];
                    system.ReadState(tick, frame);
                    system.Rollback(tick);
                }
                
                for (var i = 0; i < _systems.Count; i++)
                {
                    var system = _systems[i];
                    system.ReadInput(tick, frame);
                }
            }
            
            CatchupFromTick(tick);
        }

        private void CatchupFromTick(ulong tick)
        {
            var roundTripTime = networkManager.tickModule.rtt;
            ulong ticksToCatchUp = (ulong)(roundTripTime / (double)tickDelta);

            for (ulong i = 1; i <= ticksToCatchUp; i++)
            {
                var simTick = tick + i;
                
                for (var j = 0; j < _systems.Count; j++)
                    _systems[j].SimulateTick(simTick, tickDelta);

                var count = _systems.Count;

                for (var j = 0; j < count; j++)
                    _systems[j].PostSimulate(simTick);
            }
            
            _localTick = tick + ticksToCatchUp;
        }

        [ServerRpc(Channel.Unreliable)]
        private void SendInputToServer(ulong clientTick, BitPacker inputPacket, RPCInfo info = default)
        {
            HandleIncomingInput(clientTick, inputPacket, info);
            inputPacket.Dispose();
        }

        private void HandleIncomingInput(ulong localTick, BitPacker inputPacket,
            RPCInfo info = default)
        {
            try
            {
                bool senderIsServer = info.sender == default;
                
                for (var i = 0; i < _systems.Count; i++)
                {
                    var system = _systems[i];
                    if (system.IsOwner(info.sender, senderIsServer))
                        system.QueueInput(localTick, inputPacket);
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
