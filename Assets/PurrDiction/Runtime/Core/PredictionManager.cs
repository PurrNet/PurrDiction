using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    internal struct PlayerPacker
    {
        public PlayerID player;
        public BitPacker packer;

        public void Dispose()
        {
            packer?.Dispose();
        }
    }

    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("PurrDiction/Prediction Manager")]
    public class PredictionManager : NetworkIdentity
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();

        static readonly Dictionary<int, PredictionManager> _instances = new ();

        [SerializeField] private PredictionPhysicsProvider _physicsProvider;
        [SerializeField] private UpdateViewMode _updateViewMode = UpdateViewMode.Update;
        [SerializeField, PurrLock] private BuiltInSystems _builtInSystems =
            BuiltInSystems.Physics3D |
            BuiltInSystems.Physics2D |
            BuiltInSystems.Time |
            BuiltInSystems.Hierarchy |
            BuiltInSystems.Players;
        [SerializeField, PurrLock] private DeltaCompression _deltaCompression =
            DeltaCompression.Input |
            DeltaCompression.State;
        [SerializeField] private bool _validateDeltaCompression;

        [SerializeField] private GameObject[] _prefabs;

        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();

        public bool validateDeltaCompression
        {
            get => _validateDeltaCompression;
            set => _validateDeltaCompression = value;
        }

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
                case PredictionPhysicsProvider.None:
                default:
                    break;
            }
        }

        public float tickDelta { get; private set; }

        public int tickRate { get; private set; }

        public ulong localTick { get; private set; } = 1;

        [UsedImplicitly]
        public ulong localTickInContext { get; private set; } = 1;

        public PredictedHierarchy hierarchy { get; private set; }

        public PredictedPlayers players { get; private set; }

        internal Predicted3DPhysics physics3d { get; private set; }

        internal Predicted2DPhysics physics2d { get; private set; }

        public PredictedTime time { get; private set; }

        private DeltaModule _deltaModuleInput;
        private DeltaModule _deltaModuleState;

        bool ShouldRegisterSystem(BuiltInSystems system)
        {
            return (_builtInSystems & system) != 0;
        }

        protected override void OnEarlySpawn()
        {
            var deltaModule = networkManager.GetModule<DeltaModule>(isServer);

            bool shouldDeltaInput = (_deltaCompression & DeltaCompression.Input) != 0;
            bool shouldDeltaState = (_deltaCompression & DeltaCompression.State) != 0;

            if (shouldDeltaInput)
                _deltaModuleInput = deltaModule;

            if (shouldDeltaState)
                _deltaModuleState = deltaModule;

            RegisterScene();

            tickRate = networkManager.tickModule.tickRate;
            tickDelta = 1f / tickRate;
            _lastVerifiedTick = 0;

            if (ShouldRegisterSystem(BuiltInSystems.Hierarchy))
                hierarchy = RegisterSystem<PredictedHierarchy>();

            if (ShouldRegisterSystem(BuiltInSystems.Players))
                players = RegisterSystem<PredictedPlayers>();

            if (ShouldRegisterSystem(BuiltInSystems.Physics3D))
                physics3d = RegisterSystem<Predicted3DPhysics>();

            if (ShouldRegisterSystem(BuiltInSystems.Physics2D))
                physics2d = RegisterSystem<Predicted2DPhysics>();

            if (ShouldRegisterSystem(BuiltInSystems.Time))
                time = RegisterSystem<PredictedTime>();

            var roots = HashSetPool<GameObject>.Instantiate();
            var pid = -1;

            if (hierarchy != null)
            {
                for (var i = 0; i < _queue.Count; i++)
                {
                    var queued = _queue[i];
                    var root = queued.GetRoot();

                    if (roots.Add(root))
                        hierarchy.RegisterSceneObject(root, pid--);

                    RegisterInstance(queued);
                }
            }

            _queue.Clear();

            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.UnityPhysics3D:
                case PredictionPhysicsProvider.UnityPhysics2D:
                    Time.fixedDeltaTime = tickDelta;
                    break;
                case PredictionPhysicsProvider.None:
                default:
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
            networkManager.tickModule.onReliablePostTick += OnPreTick;
            networkManager.tickModule.onReliablePostTick += OnPostTick;
        }

        protected override void OnDespawned()
        {
            networkManager.tickModule.onReliablePostTick -= OnPreTick;
            networkManager.tickModule.onReliablePostTick -= OnPostTick;

            CleanupAllSystems();
        }

        private void CleanupAllSystems()
        {
            if (hierarchy)
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
            int count = components.Count;

            for (var i = 0; i < count; i++)
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

        readonly Dictionary<PredictedID, PredictedIdentity> _instanceMap = new ();

        public bool TryGetIdentity(PredictedID id, out PredictedIdentity instance)
        {
            return _instanceMap.TryGetValue(id, out instance);
        }

        public PredictedIdentity GetIdentity(PredictedID id)
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

            _instanceMap[new PredictedID(_nextInstanceId)] = system;
            system.Setup(networkManager, this, _nextInstanceId++);
            _systems.Add(system);
        }

        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            _instanceMap.Remove(predictedIdentity.id);
            _systems.Remove(predictedIdentity);
        }

        protected override void OnObserverRemoved(PlayerID player)
        {
            _clientTicks.Remove(player);

            var frames = _clientFrames.Count;
            for (var i = 0; i < frames; i++)
            {
                if (_clientFrames[i].player == player)
                {
                    _clientFrames[i].Dispose();
                    _clientFrames.RemoveAt(i);
                    break;
                }
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            foreach (var packer in _clientFrames)
                packer.Dispose();
            _clientFrames.Clear();
        }

        protected override void OnObserverAdded(PlayerID player)
        {
            if (player == localPlayer)
                return;

            _clientTicks[player] = new Queue<ulong>();
            _clientFrames.Add(new PlayerPacker
            {
                player = player,
                packer = BitPackerPool.Get()
            });

            using var frame = BitPackerPool.Get();
            WriteFullFrame(frame, player);
            SyncFullState(player, tickRate, tickDelta, frame);
        }

        private void WriteFullFrame(BitPacker packer, PlayerID target)
        {
            int count = _systems.Count;

            Packer<PackedInt>.Write(packer, count);

            for (var i = 0; i < count; i++)
            {
                if (_systems[i].isEventHandler)
                    continue;

                _systems[i].WriteCurrentState(target, packer, _deltaModuleState);
            }


            for (var i = 0; i < count; i++)
                _systems[i].WriteInput(localTick, target, packer, _deltaModuleInput);

            for (var i = 0; i < count; i++)
            {
                if (!_systems[i].isEventHandler)
                    continue;
                _systems[i].WriteCurrentState(target, packer, _deltaModuleState);
            }
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SyncFullState([UsedImplicitly] PlayerID target, int tickRate, float delta, BitPacker data)
        {
            tickDelta = delta;
            this.tickRate = tickRate;

            PackedInt _count = default;
            Packer<PackedInt>.Read(data, ref _count);
            int count = _count;

            for (var i = 0; i < count; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler)
                    continue;
                system.ReadState(localTick, data, _deltaModuleState);
                system.Rollback(localTick);
                system.ResetInterpolation();
            }

            for (var i = 0; i < count; i++)
                _systems[i].ReadInput(localTick, data, _deltaModuleInput);

            for (var i = 0; i < count; i++)
            {
                if (!_systems[i].isEventHandler)
                    continue;

                _systems[i].ReadState(localTick, data, _deltaModuleState);
            }

            SyncTransforms();
        }

        readonly List<PlayerPacker> _clientFrames = new ();

        void OnPreTick()
        {
            localTickInContext = localTick;

            var myPlayer = localPlayer ?? default;
            var cachedIsServer = isServer;
            var cachedIsClient = isClient;

            isSimulating = true;

            var scount = _systems.Count;
            for (var i = 0; i < scount; i++)
            {
                var system = _systems[i];
                bool controller = system.IsOwner(myPlayer, cachedIsServer);
                system.PrepareInput(cachedIsServer, controller, localTick);
            }

            bool hasClients = _clientTicks.Count > 0;
            if (cachedIsServer && hasClients)
                WriteInitialFrameToOthers();

            for (var i = 0; i < _systems.Count; i++)
                _systems[i].SimulateTick(localTick, tickDelta);

            DoPhysicsPass();

            if (cachedIsServer && hasClients)
            {
                WriteEventHandles();
                SendFrameToOthers();
            }

            for (var i = 0; i < _systems.Count; i++)
                _systems[i].PostSimulate(localTick, tickDelta);

            if (cachedIsServer)
            {
                var count = _systems.Count;

                for (var systemIdx = 0; systemIdx < count; systemIdx++)
                {
                    var system = _systems[systemIdx];

                    system.GetLatestUnityState();
                    system.SaveStateInHistory(localTick);
                }

                if (cachedIsClient)
                {
                    for (var systemIdx = 0; systemIdx < count; systemIdx++)
                        _systems[systemIdx].UpdateRollbackInterpolationState(tickDelta, false);
                }
            }
            else
            {
                using var frame = BitPackerPool.Get();
                uint writtenCount = 0;
                for (var systemIdx = 0; systemIdx < _systems.Count; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.GetLatestUnityState();
                    if (system.IsOwner(myPlayer))
                    {
                        Packer<PredictedID>.Write(frame, system.id);
                        system.WriteInput(localTick, default, frame, _deltaModuleInput);
                        writtenCount += 1;
                    }
                }

                SendInputToServer(localTick, writtenCount, frame);
            }

            isSimulating = false;

            localTick += 1;
            localTickInContext = localTick;
        }

        private void ResetAllPackers()
        {
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var packer = _clientFrames[i];
                packer.packer.ResetPositionAndMode(false);
            }
        }

        private void WriteInitialFrameToOthers()
        {
            ResetAllPackers();

            var count = _systems.Count;
            var fCount = _clientFrames.Count;

            for (var j = 0; j < fCount; j++)
            {
                var frame = _clientFrames[j].packer;
                var player = _clientFrames[j].player;

                Packer<PackedInt>.Write(frame, count);

                for (var i = 0; i < count; i++)
                {
                    if (_systems[i].isEventHandler)
                        continue;

                    _systems[i].WriteCurrentState(player, frame, _deltaModuleState);
                }

                for (var i = 0; i < count; i++)
                    _systems[i].WriteInput(localTick, player, frame, _deltaModuleInput);
            }
        }

        private void WriteEventHandles()
        {
            var fCount = _clientFrames.Count;
            int count = _systems.Count;

            for (var i = 0; i < count; i++)
            {
                if (!_systems[i].isEventHandler)
                    continue;

                for (var j = 0; j < fCount; j++)
                    _systems[i].WriteCurrentState(_clientFrames[j].player, _clientFrames[j].packer, _deltaModuleState);
            }
        }

        private void SendFrameToOthers()
        {
            var fCount = _clientFrames.Count;

            for (var j = 0; j < fCount; j++)
            {
                var player = _clientFrames[j].player;
                var packer = _clientFrames[j].packer;

                if (!_clientTicks.TryGetValue(player, out var queue))
                    continue;

                ulong tick = queue.Count > 0 ? queue.Dequeue() : 0;
                var deltaLen = packer.ToByteData().length;

                SendFrameToRemote(player, tick, new BitPackerWithLength(deltaLen, packer));
            }
        }

        /// <summary>
        /// Is the prediction manager currently replaying a frame?
        /// </summary>
        [UsedImplicitly]
        public bool isReplaying { get; private set; }

        /// <summary>
        /// Is the prediction manager currently replaying a verified frame?
        /// </summary>
        [UsedImplicitly]
        public bool isVerified { get; private set; }

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
                case PredictionPhysicsProvider.UnityPhysics3D:
                {
                    var physicsScene = gameObject.scene.GetPhysicsScene();
                    if (physicsScene.IsValid())
                        physicsScene.Simulate(tickDelta);
                    break;
                }
                case PredictionPhysicsProvider.UnityPhysics2D:
                {
                    var physicsScene = gameObject.scene.GetPhysicsScene2D();
                    if (physicsScene.IsValid())
                        physicsScene.Simulate(tickDelta);
                    break;
                }
                default:
                    throw new NotImplementedException();
            }
            isInPhysicsPass = false;
        }

        struct FrameDelta : IDisposable
        {
            public BitPacker packer;
            public ulong clientTick;

            public void Dispose()
            {
                packer?.Dispose();
            }
        }

        readonly Queue<FrameDelta> _deltas = new ();

        [TargetRpc]
        private void SendFrameToRemote([UsedImplicitly] PlayerID player, ulong clientLocalTick, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);
            _deltas.Enqueue(new FrameDelta
            {
                packer = delta.packer,
                clientTick = clientLocalTick
            });
        }

        private void RollbackToFrame(BitPacker frame, ulong inputTick, ulong stateTick)
        {
            frame.ResetPositionAndMode(true);

            PackedInt _count = default;
            Packer<PackedInt>.Read(frame, ref _count);
            int count = _count;

            for (var i = 0; i < count; ++i)
            {
                var system = _systems[i];
                if (system.isEventHandler)
                    continue;
                system.ReadState(stateTick, frame, _deltaModuleState);
                system.Rollback(stateTick);
            }

            for (var i = 0; i < count; ++i)
                _systems[i].ReadInput(inputTick, frame, _deltaModuleInput);

            for (var i = 0; i < count; ++i)
            {
                if (!_systems[i].isEventHandler)
                    continue;
                _systems[i].ReadState(inputTick, frame, _deltaModuleState);
                _systems[i].Rollback(inputTick);
            }

            SyncTransforms();
        }

        private void SyncTransforms()
        {
            switch (_physicsProvider)
            {
                case PredictionPhysicsProvider.UnityPhysics3D:
                    Physics.SyncTransforms();
                    break;
                case PredictionPhysicsProvider.UnityPhysics2D:
                    Physics2D.SyncTransforms();
                    break;
                case PredictionPhysicsProvider.None:
                default:
                    break;
            }
        }

        private ulong _lastVerifiedTick;

        private void OnPostTick()
        {
            if (_deltas.Count == 0)
            {
                if (isClient)
                    UpdateInterpolation(false);
                return;
            }

            UpdateInterpolation(false);

            isReplaying = true;
            isVerified = true;

            bool hasRollback = false;
            ulong verifiedTick = 0;
            while (_deltas.Count > 0)
            {
                using var previousFrame = _deltas.Dequeue();

                if (previousFrame.clientTick != 0)
                    _lastVerifiedTick = previousFrame.clientTick;

                hasRollback = true;
                verifiedTick = _lastVerifiedTick;
                localTickInContext = verifiedTick - 1;
                RollbackToFrame(previousFrame.packer, verifiedTick, verifiedTick - 1);
                localTickInContext = verifiedTick;
                SimulateFrame(verifiedTick);
            }

            isVerified = false;

            if (hasRollback)
            {
                ReplayToLatestTick(verifiedTick + 1);
                SyncTransforms();
                UpdateInterpolation(true);
            }
            else
            {
                localTickInContext = localTick;
            }

            isReplaying = false;
        }

        private void UpdateInterpolation(bool accumulateError)
        {
            var scount = _systems.Count;
            for (var j = 0; j < scount; j++)
                _systems[j].UpdateRollbackInterpolationState(tickDelta, accumulateError);
        }

        private void ReplayToLatestTick(ulong verifiedTick)
        {
            isSimulating = true;
            for (ulong simTick = verifiedTick; simTick < localTick; simTick++)
            {
                localTickInContext = simTick;

                for (var j = 0; j < _systems.Count; j++)
                    _systems[j].SimulateTick(simTick, tickDelta);

                DoPhysicsPass();

                for (var i = 0; i < _systems.Count; i++)
                    _systems[i].PostSimulate(simTick, tickDelta);

                var count = _systems.Count;
                for (var j = 0; j < count; j++)
                    _systems[j].GetLatestUnityState();
            }
            isSimulating = false;
            localTickInContext = localTick;
        }

        private void SimulateFrame(ulong verifiedTick)
        {
            isSimulating = true;
            for (var j = 0; j < _systems.Count; j++)
                _systems[j].SimulateTick(verifiedTick, tickDelta);

            DoPhysicsPass();

            for (var i = 0; i < _systems.Count; i++)
                _systems[i].PostSimulate(verifiedTick, tickDelta);

            var count = _systems.Count;
            for (var j = 0; j < count; j++)
                _systems[j].GetLatestUnityState();
            isSimulating = false;
        }

        readonly Dictionary<PlayerID, Queue<ulong>> _clientTicks = new ();

        [ServerRpc(requireOwnership: false)]
        private void SendInputToServer(ulong clientTick, PackedUInt count, BitPacker inputPacket, RPCInfo info = default)
        {
            if (!_clientTicks.TryGetValue(info.sender, out var ticks))
            {
                ticks = new Queue<ulong>();
                _clientTicks[info.sender] = ticks;
            }

            ticks.Clear();
            ticks.Enqueue(clientTick);

            using (inputPacket)
                HandleIncomingInput(inputPacket, count, info);
        }

        private void HandleIncomingInput(BitPacker inputPacket, PackedUInt count, RPCInfo info = default)
        {
            try
            {
                bool senderIsServer = info.sender == default;

                for (var i = 0; i < count; i++)
                {
                    PredictedID pid = default;
                    Packer<PredictedID>.Read(inputPacket, ref pid);

                    if (!_instanceMap.TryGetValue(pid, out var system))
                        continue;

                    if (system.IsOwner(info.sender, senderIsServer))
                    {
                        system.QueueInput(info.sender, inputPacket, _deltaModuleInput);
                    }
                    else
                    {
                        PackedUInt dataSize = default;
                        Packer<PackedUInt>.Read(inputPacket, ref dataSize);
                        inputPacket.SkipBits((int)dataSize.value);
                    }
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

        internal static void InternalDelete(GameObject instance)
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
