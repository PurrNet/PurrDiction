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
    public enum PredictionPhysicsProvider : byte
    {
        None,
        UnityPhysics3D,
        UnityPhysics2D,
        BEPUPhysics
    }

    public enum UpdateViewMode : byte
    {
        None,
        Update,
        LateUpdate
    }

    [DefaultExecutionOrder(1000)][AddComponentMenu("PurrDiction/Prediction Manager")]
    public class PredictionManager : NetworkIdentity
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();

        static readonly Dictionary<int, PredictionManager> _instances = new ();

        [SerializeField] private PredictionPhysicsProvider _physicsProvider;
        [SerializeField] private UpdateViewMode _updateViewMode = UpdateViewMode.Update;
        [SerializeField] private GameObject[] _prefabs;

        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();

        public BEPUphysics.Space physics { get; private set; }
        internal Action onPhysicsSet;

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
                case PredictionPhysicsProvider.BEPUPhysics:
                    physics = new BEPUphysics.Space
                    {
                        ForceUpdater =
                        {
                            Gravity = new BEPUutilities.FPVector3(0, -9.81M, 0)
                        },
                        BufferedStates =
                        {
                            Enabled = false
                        }
                    };
                    onPhysicsSet?.Invoke();
                    break;
            }
        }

        public FP tickDelta { get; private set; }

        public int tickRate { get; private set; }

        public ulong localTick { get; private set; } = 1;

        [UsedImplicitly]
        public ulong localTickInContext { get; private set; } = 1;

        public PredictedHierarchy hierarchy { get; private set; }

        protected override void OnEarlySpawn()
        {
            RegisterScene();

            tickRate = networkManager.tickModule.tickRate;
            tickDelta = 1 / (FP)tickRate;
            hierarchy = RegisterSystem<PredictedHierarchy>();

            var roots = HashSetPool<GameObject>.Instantiate();
            var pid = -1;

            for (var i = 0; i < _queue.Count; i++)
            {
                var queued = _queue[i];
                var root = queued.GetRoot();

                if (roots.Add(root))
                    hierarchy.RegisterSceneObject(root, pid--);

                RegisterInstance(queued);
            }

            _queue.Clear();

            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.UnityPhysics3D:
                case PredictionPhysicsProvider.UnityPhysics2D:
                    Time.fixedDeltaTime = (float)tickDelta;
                    break;
            }
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

            _lastServerFrame?.Dispose();
            _lastServerFrame = null;

            CleanupAllSystems();
        }

        private void CleanupAllSystems()
        {
            hierarchy.Cleanup();

            for (var i = 0; i < _systems.Count; i++)
            {
                if (_systems[i])
                    DestroyImmediate(_systems[i]);
            }

            _nextInstanceId = 0;
            _instanceMap.Clear();
            _queue.Clear();
            _systems.Clear();
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

        readonly Dictionary<uint, PredictedIdentity> _instanceMap = new ();

        public bool TryGetIdentity(uint id, out PredictedIdentity instance)
        {
            return _instanceMap.TryGetValue(id, out instance);
        }

        public PredictedIdentity GetIdentity(uint id)
        {
            return _instanceMap[id];
        }

        private void RegisterInstance(PredictedIdentity system)
        {
            if (!isSpawned)
            {
                _queue.Add(system);
                return;
            }

            _instanceMap[_nextInstanceId] = system;
            system.Setup(networkManager, this, _nextInstanceId++);
            _systems.Add(system);
        }

        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            _instanceMap.Remove(predictedIdentity.id.value);
            _systems.Remove(predictedIdentity);
        }

        protected override void OnObserverRemoved(PlayerID player)
        {
            _clientTicks.Remove(player);
        }

        private BitPacker _lastServerFrame;

        protected override void OnObserverAdded(PlayerID player)
        {
            if (player == localPlayer)
                return;

            _clientTicks[player] = new Queue<ulong>();
            MakeSureWeHaveLastFrame();
            SyncFullState(player, tickRate, tickDelta.RawValue, _lastServerFrame);
        }

        private void MakeSureWeHaveLastFrame()
        {
            if (_lastServerFrame == null)
            {
                _lastServerFrame = BitPackerPool.Get();
                int count = _systems.Count;

                Packer<PackedInt>.Write(_lastServerFrame, count);

                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                    _systems[systemIdx].WriteLatestState(_lastServerFrame);

                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                    _systems[systemIdx].WriteInput(localTick, _lastServerFrame);
            }
        }

        [TargetRpc]
        private void SyncFullState([UsedImplicitly] PlayerID target, int tickRate, long delta, BitPacker data)
        {
            tickDelta = FP.FromRaw(delta);
            this.tickRate = tickRate;

            PackedInt _count = default;
            Packer<PackedInt>.Read(data, ref _count);
            int count = _count;

            for (var systemIdx = 0; systemIdx < count; systemIdx++)
            {
                var system = _systems[systemIdx];
                system.ReadState(localTick, data);
                system.Rollback(localTick);
                system.ResetInterpolation();
            }

            for (var systemIdx = 0; systemIdx < count; systemIdx++)
                _systems[systemIdx].ReadInput(localTick, data);

            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.UnityPhysics3D:
                    Physics.SyncTransforms();
                    break;
                case PredictionPhysicsProvider.UnityPhysics2D:
                    Physics2D.SyncTransforms();
                    break;
            }

            _lastFrame?.Dispose();
            _lastFrame = data;
        }

        void OnTick()
        {
            var myPlayer = localPlayer ?? default;
            var cachedIsServer = isServer;
            var cachedIsClient = isClient;

            isSimulating = true;

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

            if (cachedIsServer)
            {
                MakeSureWeHaveLastFrame();

                var frame = BitPackerPool.Get();

                var count = _systems.Count;
                Packer<PackedInt>.Write(frame, count);

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
                    {
                        _systems[systemIdx].UpdateRollbackInterpolationState(tickDelta, false);
                    }
                }

                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.WriteInput(localTick, frame);
                }

                using var delta = BitPackerPool.Get();
                BitPackerDeltaUtils.CreateDelta(_lastServerFrame, frame, delta);

                var deltaLen = delta.ToByteData().length;
                foreach (var (player, queue) in _clientTicks)
                {
                    if (player == localPlayer)
                        continue;

                    SendFrameToRemote(player, queue.Count > 0 ? queue.Dequeue() : 0, delta, deltaLen);
                }

                _lastServerFrame?.Dispose();
                _lastServerFrame = frame;
            }
            else
            {
                using var frame = BitPackerPool.Get();

                for (var systemIdx = 0; systemIdx < _systems.Count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.GetLatestUnityState();
                    system.UpdateRollbackInterpolationState(tickDelta, false);

                    if (system.IsOwner(myPlayer))
                        system.WriteInput(localTick, frame);
                }

                SendInputToServer(localTick, frame);
            }

            isSimulating = false;

            localTick += 1;
            localTickInContext = localTick;
        }

        /// <summary>
        /// Is the prediction manager currently replaying a frame?
        /// </summary>
        [UsedImplicitly]
        public bool isReplaying
        {
            get; private set;
        }

        /// <summary>
        /// Is the prediction manager currently simulating a frame?
        /// This includes replaying frames.
        /// If this is false nothing should act on the state of the game and expect it to be correct.
        /// </summary>
        [UsedImplicitly]
        public bool isSimulating
        {
            get; private set;
        }

        /// <summary>
        /// True if the prediction manager is currently in the physics pass.
        /// </summary>
        [UsedImplicitly]
        public bool isInPhysicsPass
        {
            get; private set;
        }

        private void DoPhysicsPass()
        {
            isInPhysicsPass = true;
            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.None:
                    break;
                case PredictionPhysicsProvider.BEPUPhysics:
                    physics.Update(tickDelta);
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
            isInPhysicsPass = false;
        }

        BitPacker _lastFrame;

        [TargetRpc]
        private void SendFrameToRemote([UsedImplicitly] PlayerID player, ulong clientLocalTick, BitPacker delta, int deltaLen)
        {
            using (delta)
            {
                var result = BitPackerPool.Get();
                delta.SkipBytes(deltaLen);

                BitPackerDeltaUtils.ApplyDelta(_lastFrame, delta, result);

                _lastFrame?.Dispose();
                _lastFrame = result;

                if (clientLocalTick == 0)
                    return;

                _lastVerifiedTick = clientLocalTick;
                _tickToRollbackFrom = clientLocalTick;
            }
        }

        private void CatchupFromTick(ulong clientTick)
        {
            isReplaying = true;
            _lastFrame.ResetPositionAndMode(true);

            PackedInt _count = default;
            Packer<PackedInt>.Read(_lastFrame, ref _count);
            int count = _count;

            for (var i = 0; i < count; ++i)
            {
                var system = _systems[i];
                system.ReadState(clientTick, _lastFrame);
                system.Rollback(clientTick);
            }

            for (var i = 0; i < count; ++i)
                _systems[i].ReadInput(clientTick, _lastFrame);

            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.UnityPhysics3D:
                    Physics.SyncTransforms();
                    break;
                case PredictionPhysicsProvider.UnityPhysics2D:
                    Physics2D.SyncTransforms();
                    break;
            }

            isSimulating = true;
            for (ulong simTick = clientTick + 1; simTick < localTick; simTick++)
            {
                localTickInContext = simTick;

                for (var j = 0; j < _systems.Count; j++)
                    _systems[j].SimulateTick(simTick, tickDelta);

                DoPhysicsPass();

                for (var j = 0; j < _systems.Count; j++)
                    _systems[j].GetLatestUnityState();
            }
            isSimulating = false;

            localTickInContext = localTick;
            isReplaying = false;

            var scount = _systems.Count;
            for (var j = 0; j < scount; j++)
            {
                _systems[j].UpdateRollbackInterpolationState(tickDelta, true);
            }
        }

        private ulong? _tickToRollbackFrom;
        private ulong _lastVerifiedTick;

        private void OnPostTick()
        {
            if (_tickToRollbackFrom.HasValue)
            {
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
            if (_updateViewMode != UpdateViewMode.Update)
                return;

            if (!isClient)
                return;

            UpdateView();
        }

        private void LateUpdate()
        {
            if (_updateViewMode != UpdateViewMode.LateUpdate)
                return;

            if (!isClient)
                return;

            UpdateView();
        }

        private void UpdateView()
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
