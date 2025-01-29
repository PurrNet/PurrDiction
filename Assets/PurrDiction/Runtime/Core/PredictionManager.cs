using System;
using System.Collections.Generic;
using FixMath.NET;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public enum PredictionPhysicsProvider
    {
        None,
        UnityPhysics3D,
        UnityPhysics2D,
        BEPUPhysics
    }
    
    [DefaultExecutionOrder(-1000), RegisterNetworkType(typeof(AnimationClip))]
    public class PredictionManager : NetworkIdentity
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();
        
        static readonly Dictionary<int, PredictionManager> _instances = new ();

        [SerializeField] private PredictionPhysicsProvider _physicsProvider;
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

            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.UnityPhysics3D:
                    Physics.simulationMode = SimulationMode.Script;
                    break;
                case PredictionPhysicsProvider.UnityPhysics2D:
                    Physics2D.simulationMode = SimulationMode2D.Script;
                    break;
            }
        }

        public Fix64 tickDelta { get; private set; }

        public int tickRate { get; private set; }
        
        public ulong localTick { get; private set; } = 1;
        
        [UsedImplicitly]
        public ulong localTickInContext { get; private set; } = 1;

        public PredictedHierarchy hierarchy { get; private set; }
        
        protected override void OnEarlySpawn()
        {
            RegisterScene();

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

        private void RegisterScene()
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            var rootGameObjects = gameObject.scene.GetRootGameObjects();

            foreach (var rootObject in rootGameObjects)
            {
                rootObject.GetComponentsInChildren(true, identities);
                
                if (identities.Count == 0) continue;
                
                rootObject.MakeSureAwakeIsCalled();

                int count = identities.Count;
                for (var i = 0; i < count; ++i)
                {
                    var pid = identities[i];
                    _queue.Add(pid);
                }
            }
            
            ListPool<PredictedIdentity>.Destroy(identities);
        }

        protected override void OnSpawned()
        {
            networkManager.tickModule.onPreTick += OnTick;
            networkManager.tickModule.onPostTick += OnPostTick;
        }

        protected override void OnDespawned()
        {
            networkManager.tickModule.onPreTick -= OnTick;
            networkManager.tickModule.onPostTick -= OnPostTick;
        }

        public T RegisterSystem<T>() where T : PredictedIdentity
        {
            var system = gameObject.AddComponent<T>();
            system.hideFlags = HideFlags.NotEditable;
            RegisterInstance(system);
            return system;
        }

        public void RegisterInstance(GameObject go)
        {
            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);
            
            for (var i = 0; i < components.Count; i++)
                RegisterInstance(components[i]);
            
            ListPool<PredictedIdentity>.Destroy(components);
        }
        
        public void UnregisterInstance(GameObject go)
        {
            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);
            
            for (var i = 0; i < components.Count; i++)
                UnregisterInstance(components[i]);
            
            ListPool<PredictedIdentity>.Destroy(components);
        }
        
        private uint _nextInstanceId;

        private void RegisterInstance(PredictedIdentity system)
        {
            if (!isSpawned)
            {
                _queue.Add(system);
                return;
            }
            
            system.Setup(networkManager, this, _nextInstanceId++);
            _systems.Add(system);
        }
        
        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            _systems.Remove(predictedIdentity);
        }

        protected override void OnObserverRemoved(PlayerID player)
        {
            _clientTicks.Remove(player);
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

            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.UnityPhysics3D:
                    Physics.SyncTransforms();
                    break;
                case PredictionPhysicsProvider.UnityPhysics2D:
                    Physics2D.SyncTransforms();
                    break;
            }
        }
        
        void OnTick()
        {
            using var frame = BitPackerPool.Get();
            var myPlayer = localPlayer ?? default;
            var cachedIsServer = isServer;
            var cachedIsClient = isClient;
            
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
                     system.SimulateRemote(localTick, tickDelta);
                }
            }

            DoPhysicsPass();

            var count = _systems.Count;
            
            if (cachedIsServer)
            {
                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    
                    system.GetLatestUnityState();
                    system.SaveStateInHistory(localTick);
                    system.WriteLatestState(frame);
                }

                if (cachedIsClient)
                {
                    for (var systemIdx = 0; systemIdx < count; systemIdx++)
                        _systems[systemIdx].UpdateRollbackInterpolationState(tickDelta, false);
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
                    system.GetLatestUnityState();
                    system.UpdateRollbackInterpolationState(tickDelta, false);

                    if (system.IsOwner(myPlayer))
                        system.WriteInput(localTick, frame);
                }
            }
            
            if (!cachedIsServer)
            {
                SendInputToServer(localTick, frame);
            }
            else if (frame.positionInBits > 0)
            {
                foreach (var (player, queue) in _clientTicks)
                {
                    if (player == localPlayer)
                        continue;

                    SendFrameToRemote(player, queue.Count > 0 ? queue.Dequeue() : 0, frame);
                }
            }

            localTick += 1;
            localTickInContext = localTick;
        }

        [UsedImplicitly]
        public bool isReplaying
        {
            get; private set;
        }

        private void DoPhysicsPass()
        {
            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.None:
                    break;
                case PredictionPhysicsProvider.UnityPhysics3D:
                {
                    var physicsScene = gameObject.scene.GetPhysicsScene();
                    if (physicsScene.IsValid())
                        physicsScene.Simulate((float)tickDelta);
                    break;
                }
                case PredictionPhysicsProvider.UnityPhysics2D:
                {
                    var physicsScene = gameObject.scene.GetPhysicsScene2D();
                    if (physicsScene.IsValid())
                        physicsScene.Simulate((float)tickDelta);
                    break;
                }
                default:
                    throw new NotImplementedException();
            }
        }

        [TargetRpc]
        private void SendFrameToRemote([UsedImplicitly] PlayerID player, ulong clientLocalTick, BitPacker frame)
        {
            if (clientLocalTick == 0)
                return;
            
            using (frame)
            {
                for (var i = 0; i < _systems.Count; ++i)
                {
                    var system = _systems[i];
                    system.ReadState(clientLocalTick, frame);
                    system.Rollback(clientLocalTick);
                }
                
                var sysCount = _systems.Count;
                for (var i = 0; i < sysCount; ++i)
                    _systems[i].ReadInput(clientLocalTick, frame);
            }
            
            _tickToRollbackFrom = clientLocalTick;
        }

        private void CatchupFromTick(ulong clientTick)
        {
            isReplaying = true;
            for (ulong simTick = clientTick + 1; simTick < localTick; simTick++)
            {
                localTickInContext = simTick;
                
                for (var j = 0; j < _systems.Count; j++)
                    _systems[j].SimulateTick(simTick, tickDelta);

                DoPhysicsPass();
                
                var count = _systems.Count;

                for (var j = 0; j < count; j++)
                    _systems[j].GetLatestUnityState();
            }
            
            localTickInContext = localTick;
            isReplaying = false;
            
            var scount = _systems.Count;
            for (var j = 0; j < scount; j++)
                _systems[j].UpdateRollbackInterpolationState(tickDelta, true);
        }
        
        private ulong? _tickToRollbackFrom;
        
        private void OnPostTick()
        {
            if (_tickToRollbackFrom.HasValue)
            {
                switch (_physicsProvider)
                {
                    case PredictionPhysicsProvider.UnityPhysics3D:
                        Physics.SyncTransforms();
                        break;
                    case PredictionPhysicsProvider.UnityPhysics2D:
                        Physics2D.SyncTransforms();
                        break;
                }
                
                CatchupFromTick(_tickToRollbackFrom.Value);
                _tickToRollbackFrom = null;
            }
        }

        readonly Dictionary<PlayerID, Queue<ulong>> _clientTicks = new ();

        [ServerRpc(requireOwnership: false)]
        private void SendInputToServer(ulong clientTick, BitPacker inputPacket, RPCInfo info = default)
        {
            if (!_clientTicks.TryGetValue(info.sender, out var ticks))
            {
                ticks = new Queue<ulong>();
                _clientTicks[info.sender] = ticks;
            }

            if (ticks.Count > 4)
            {
                ClearAllInputs();
                ticks.Clear();
            }

            ticks.Enqueue(clientTick);
            
            using (inputPacket)
                HandleIncomingInput(inputPacket, info);
        }
        
        private void ClearAllInputs()
        {
            for (var i = 0; i < _systems.Count; i++)
                _systems[i].ClearInput();
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

        private void Update()
        {
            if (!isClient)
                return;
            
            int count = _systems.Count;
            
            for (var i = 0; i < count; i++)
                _systems[i].UpdateView(Time.unscaledDeltaTime);
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
            var go = UnityProxy.InstantiateDirectly(prefab);
            RegisterInstance(go);
            return go;
        }

        internal GameObject InternalCreate(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            var go = UnityProxy.InstantiateDirectly(prefab, position, rotation);
            RegisterInstance(go);
            return go;
        }

        internal void InternalDelete(GameObject instance)
        {
            UnityProxy.DestroyImmediateDirectly(instance);
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
