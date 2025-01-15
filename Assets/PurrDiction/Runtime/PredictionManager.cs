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
    [Serializable]
    public class PredictionManagerSettings
    {
        [Tooltip("The maximum number of ticks that can be ahead or behind of the server.\n" +
                 "This is from the prespective of the server, used to validate incoming ticks.")]
        public uint maxTickDifference = 10;
    }
    
    [DefaultExecutionOrder(-1000)]
    public class PredictionManager : NetworkIdentity, ITick, IServerSceneEvents
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();
        
        static readonly Dictionary<int, PredictionManager> _instances = new ();
        
        [SerializeField] private PredictionManagerSettings _settings;
        [SerializeField] private GameObject[] _prefabs;
        
        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();
        
        public static bool TryGetInstance(int sceneHandle, out PredictionManager world)
        {
            return _instances.TryGetValue(sceneHandle, out world);
        }

        private void Awake()
        {
            _settings ??= new PredictionManagerSettings();
            _instances[gameObject.scene.handle] = this;
        }

        private ulong _localTick;
        
        public Fix64 tickDelta { get; private set; }

        public int tickRate { get; private set; }
        
        public PredictedHierarchy hierarchy { get; private set; }
        
        readonly Dictionary<PlayerID, PredictedObjectID> _objectIds = new ();
        
        public void OnPlayerLoadedScene(PlayerID playerId)
        {
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
            }
        }

        public void OnTick(float delta)
        {
            using var input = BitPackerPool.Get();
            var myPlayer = localPlayer ?? default;
            var cachedIsServer = isServer;

            int count = _systems.Count;
            for (var systemIdx = 0; systemIdx < count; systemIdx++)
            {
                var system = _systems[systemIdx];
                bool controller = system.IsOwner(myPlayer, cachedIsServer);
                
                // prepare and send input
                if (controller)
                {
                    system.EvaluateLocalInput();

                    if (!cachedIsServer)
                    {
                        system.WriteLocalInput(input);
                        if (input.positionInBits > 0)
                        {
                            SendInputToServer(_localTick, (uint)systemIdx, input);
                            input.ResetPosition();
                        }
                    }
                    
                    system.SimulateLocal(tickDelta);
                }
                else
                {
                    system.SimulateRemote(tickDelta);
                }

                // post-simulation update state
                system.PostSimulate(_localTick);
            }
            
            _localTick += 1;
        }
        
        private bool ValidateTick(ulong tick)
        {
            return tick <= _localTick + _settings.maxTickDifference;
        }

        [ServerRpc(Channel.Unreliable)]
        private void SendInputToServer(ulong clientTick, PackedUint systemIndex, BitPacker inputPacket, RPCInfo info = default)
        {
            if (ValidateTick(clientTick))
                HandleIncomingInput(clientTick, systemIndex, inputPacket, info);
            inputPacket.Dispose();
        }

        private void HandleIncomingInput(ulong localTick, uint systemIndex, BitPacker inputPacket,
            RPCInfo info = default)
        {
            if (systemIndex >= _systems.Count)
                return;

            var system = _systems[(int)systemIndex];

            try
            {
                if (system.IsOwner(info.sender))
                    system.QueueInput(localTick, inputPacket);
            }
            catch
            {
                PurrLogger.LogError($"Failed to handle input for system {system.GetType().Name}");
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
