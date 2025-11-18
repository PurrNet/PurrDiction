using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction.Profiler;
using PurrNet.Transports;
using PurrNet.Utils;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct InputQueueSettings
    {
        public bool extrapolateForMissing;
        public int minInputs;
        public int maxInputs;
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
        [SerializeField] private PredictedPrefabs _predictedPrefabs;
        [SerializeField] private InputQueueSettings _inputQueueSettings = new()
        {
            extrapolateForMissing = true,
            minInputs = 1,
            maxInputs = 4
        };

        [Header("Debugging")]
        [SerializeField] private bool _validateDeterministicData;

        public PredictedPrefabs predictedPrefabs
        {
            get => _predictedPrefabs;
            set
            {
                _predictedPrefabs = value;
                InitPooling();
            }
        }

        public bool validateDeterministicData => _validateDeterministicData;

        static readonly ProfilerMarker SimulateMarker = new("PredictionManager.Simulate");
        static readonly ProfilerMarker LateSimulateMarker = new("PredictionManager.LateSimulate");
        static readonly ProfilerMarker UpdateViewMarker = new("PredictionManager.UpdateView");
        static readonly ProfilerMarker SaveHistoryMarker = new("PredictionManager.SaveHistory");
        static readonly ProfilerMarker WriteFrameOnServerMarker = new("PredictionManager.WriteFrameOnServer");

        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();
        private int _systemsCount;

        GameObjectPoolCollection _pools;

        [UsedImplicitly]
        public static bool TryGetInstance(int sceneHandle, out PredictionManager world)
        {
            return _instances.TryGetValue(sceneHandle, out world);
        }

        [ContextMenu("Debug/Print all systems")]
        public void PrintAllSystems()
        {
            foreach (var system in _systems)
                Debug.Log(system, system);
        }

        private void Awake()
        {
            _instances[gameObject.scene.handle] = this;

#if UNITY_PHYSICS_2D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0)
                Physics2D.simulationMode = SimulationMode2D.Script;
#endif
#if UNITY_PHYSICS_3D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
                Physics.simulationMode = SimulationMode.Script;
#endif
            InitPooling();
        }

        [ServerRpc(requireOwnership: false)]
        public Task ClientRequestedToBeObserver(PredictedComponentID component, RPCInfo info = default)
        {
            if (component.TryGetIdentity<PredictedIdentitySpawner>(this, out var pidSpawner))
                pidSpawner.ClientRequestedToBeObserver(info.sender);
            return Task.CompletedTask;
        }

        private static GameObject _poolParent;

        private void InitPooling()
        {
            if (!_predictedPrefabs)
                return;

            if (!_poolParent)
            {
                _poolParent = new GameObject("PooledPrefabs");
#if !PURRNET_DEBUG_POOLING
                _poolParent.hideFlags = HideFlags.HideAndDontSave;
#endif
                _poolParent.SetActive(false);
            }

            _pools ??= new GameObjectPoolCollection(_poolParent.transform);
            for (var i = 0; i < _predictedPrefabs.prefabs.Count; i++)
            {
                var prefab = _predictedPrefabs.prefabs[i];
                if (prefab.pooling.usePooling)
                    _pools.Register(prefab.prefab, prefab.pooling.initialSize);
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

        private DeltaModule _deltaModuleState;

        bool ShouldRegisterSystem(BuiltInSystems system)
        {
            return (_builtInSystems & system) != 0;
        }

        protected override void OnEarlySpawn()
        {
            var deltaModule = networkManager.GetModule<DeltaModule>(isServer);
            _deltaModuleState = deltaModule;

            RegisterScene();

            tickRate = networkManager.tickModule.tickRate;
            tickDelta = 1f / tickRate;

            hierarchy = ShouldRegisterSystem(BuiltInSystems.Hierarchy) ? RegisterSystem<PredictedHierarchy>() : null;
            players = ShouldRegisterSystem(BuiltInSystems.Players) ? RegisterSystem<PredictedPlayers>() : null;
            physics3d = ShouldRegisterSystem(BuiltInSystems.Physics3D) ? RegisterSystem<Predicted3DPhysics>() : null;
            physics2d = ShouldRegisterSystem(BuiltInSystems.Physics2D) ? RegisterSystem<Predicted2DPhysics>() : null;
            time = ShouldRegisterSystem(BuiltInSystems.Time) ? RegisterSystem<PredictedTime>() : null;

            var roots = HashSetPool<GameObject>.Instantiate();
            var pid = -1;

            if (hierarchy)
            {
                for (var i = 0; i < _queue.Count; i++)
                {
                    var queued = _queue[i];
                    var root = queued.GetRoot();

                    if (roots.Add(root))
                    {
                        if (!_poolParent || root.transform.root != _poolParent.transform)
                            hierarchy.RegisterSceneObject(root, pid--);
                    }
                }
            }

            HashSetPool<GameObject>.Destroy(roots);

            _queue.Clear();

            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0 ||
                (_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
            {
                Time.fixedDeltaTime = tickDelta;
            }
        }

        private void RegisterScene()
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            SceneObjectsModule.GetScenePredictedIdentities(gameObject.scene, identities);

            int count = identities.Count;
            for (var i = 0; i < count; ++i)
            {
                var pid = identities[i];
                _queue.Add(pid);
            }
            ListPool<PredictedIdentity>.Destroy(identities);
        }

        private TickManager _tickManager;

        protected override void OnSpawned()
        {
            _tickManager = networkManager.tickModule;
            _tickManager.onPreTick += OnPreTick;
            _tickManager.onPostTick += OnPostTick;
        }

        protected override void OnDespawned()
        {
            if (_tickManager != null)
            {
                _tickManager.onPreTick -= OnPreTick;
                _tickManager.onPostTick -= OnPostTick;
                _tickManager = null;
            }

            CleanupAllSystems();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            foreach (var packer in _clientFrames)
                packer.Dispose();
            _clientFrames.Clear();

            if (_tickManager != null)
            {
                _tickManager.onPreTick -= OnPreTick;
                _tickManager.onPostTick -= OnPostTick;
            }
        }

        private void CleanupAllSystems()
        {
            if (hierarchy)
                hierarchy.Cleanup();

            for (var i = _systemsCount - 1; i >= 0; i--)
            {
                if (_systems[i])
                    Destroy(_systems[i]);
            }

            _instanceMap.Clear();
            _queue.Clear();
            _systems.Clear();
            _systemsCount = 0;
            _nextSystemId = 0;
            _clientTicks.Clear();
            _clientFrames.Clear();
            localTick = 1;
            _lastVerifiedTick = 1;
            localTickInContext = 1;
            _deltas.Clear();
        }

        private uint _nextSystemId;

        public T RegisterSystem<T>() where T : PredictedIdentity
        {
            var system = gameObject.AddComponent<T>();
            system.hideFlags = HideFlags.NotEditable;
            if (cachedIsServer)
                system.OnPreSetup();
            RegisterInstance(system, new PredictedObjectID(0), _nextSystemId++, null);
            return system;
        }

        public void RegisterInstance(GameObject go, PredictedObjectID objectID, PlayerID? owner, bool reset)
        {
            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);
            int count = components.Count;

            for (uint i = 0; i < count; i++)
            {
                var component = components[(int)i];

                if (!_systems.Contains(component))
                {
                    component.OnPreSetup();
                    if (reset)
                        component.ResetState();
                    RegisterInstance(component, objectID, i, owner);
                }
            }

            ListPool<PredictedIdentity>.Destroy(components);
        }

        public void UnregisterInstance(GameObject go, bool reset)
        {
            if (!go)
                return;

            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);

            for (var i = 0; i < components.Count; i++)
            {
                if (components[i].hideFlags != HideFlags.NotEditable)
                {
                    if (reset)
                        components[i].ResetState();
                    UnregisterInstance(components[i]);
                }
            }

            ListPool<PredictedIdentity>.Destroy(components);
        }

        public void UnregisterPooledInstance(GameObject go)
        {
            if (!go) return;

            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);

            for (var i = 0; i < components.Count; i++)
            {
                UnregisterInstance(components[i]);
                components[i].TriggerDestroyedEvent();
                components[i].TriggerOnPooledEvent();
            }

            ListPool<PredictedIdentity>.Destroy(components);
        }

        readonly Dictionary<PredictedComponentID, PredictedIdentity> _instanceMap = new ();

        public bool TryGetIdentity(PredictedComponentID id, out PredictedIdentity instance)
        {
            return _instanceMap.TryGetValue(id, out instance);
        }

        public PredictedIdentity GetIdentity(PredictedComponentID id)
        {
            return _instanceMap.GetValueOrDefault(id);
        }

        private void RegisterInstance(PredictedIdentity system, PredictedObjectID objectId, uint componentId, PlayerID? owner)
        {
            if (!isSpawned)
            {
                _queue.Add(system);
                return;
            }

            var pid = new PredictedComponentID(objectId, componentId);
            _instanceMap[pid] = system;
            system.Setup(networkManager, this, pid, owner);

            // i want to insert based on objectid first, then componet id such that I can guarantee that the order of the components is preserved
            var myObjId = pid.objectId.instanceId.value;
            int posToInsert = _systemsCount;

            for (int i = 0; i < _systemsCount; i++)
            {
                var curObjId = _systems[i].id.objectId.instanceId.value;
                if (curObjId > myObjId || curObjId == myObjId && _systems[i].id.componentId.value > pid.componentId.value)
                {
                    posToInsert = i;
                    break;
                }
            }

            _systems.Insert(posToInsert, system);
            ++_systemsCount;
        }

        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            _instanceMap.Remove(predictedIdentity.id);
            if (_systems.Remove(predictedIdentity))
                --_systemsCount;
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

        protected override void OnPreObserverAdded(PlayerID player)
        {
            if (player == localPlayer)
                return;

            if (localTick == 1)
                OnPreTick();

            _clientTicks[player] = new InputQueue();
            _clientFrames.Add(new PlayerPacker
            {
                player = player,
                packer = BitPackerPool.Get()
            });
        }

        protected override void OnObserverAdded(PlayerID player)
        {
            if (player == localPlayer)
                return;

            using var frame = BitPackerPool.Get();
            var tick = localTick - 1;
            RollbackToFrame(tick);

            Packer<Size>.Write(frame, _systemsCount);

            for (var i = 0; i < _systemsCount; i++)
            {
                if (!_systems[i].isEventHandler)
                    _systems[i].WriteFirstState(tick, frame);
            }

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].WriteFirstInput(tick, frame);

            for (var i = 0; i < _systemsCount; i++)
            {
                if (_systems[i].isEventHandler)
                    _systems[i].WriteFirstState(tick, frame);
            }

            SimulateFrame(localTick, false);
            SyncFullState(player, tickRate, tickDelta, frame);
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SyncFullState([UsedImplicitly] PlayerID target, int tickRate, float delta, BitPacker data)
        {
            isSimulating = true;

            tickDelta = delta;
            this.tickRate = tickRate;

            Size _count = default;
            Packer<Size>.Read(data, ref _count);
            int count = _count;

            for (var i = 0; i < count; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler)
                    continue;
                system.ReadFirstState(1, data);
                system.Rollback(1);
                system.ResetInterpolation();
            }

            for (var i = 0; i < count; i++)
                _systems[i].ReadFirstInput(1, data);

            for (var i = 0; i < count; i++)
            {
                if (_systems[i].isEventHandler)
                    _systems[i].ReadFirstState(1, data);
            }

            for (var i = 0; i < count; i++)
                _systems[i].SaveStateInHistory(1);

            SyncTransforms();

            isSimulating = false;

            ReplayToLatestTick(1);
        }

        readonly List<PlayerPacker> _clientFrames = new (16);

        public bool cachedIsServer { get; private set; }

        private void OnPreTick()
        {
            cachedIsServer = isServer;
            localTickInContext = localTick;

            var myPlayer = isSpawned ? localPlayer ?? default : default;
            var cachedIsClient = isClient;

            isSimulating = true;
            if (cachedIsServer)
                isVerified = true;

            if (cachedIsServer)
                PrepareInputs();

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                bool controller = system.IsOwner(myPlayer, cachedIsServer);
                system.PrepareInput(cachedIsServer, controller, localTick, _inputQueueSettings.extrapolateForMissing);
            }

            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (!system.isEventHandler)
                        system.SaveStateInHistory(localTick);
                }
            }

            if (cachedIsServer)
            {
                using (WriteFrameOnServerMarker.Auto())
                {
                    ResetAllPackers();
                    WriteInitialFrameToOthers();
                }
            }

            float delta = this.tickDelta;

            if (time)
                delta *= time.timeScale;

            using (SimulateMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].SimulateTick(localTick, delta);
            }

            using (LateSimulateMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].LateSimulateTick(delta);
            }

            DoPhysicsPass();

            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (system.isEventHandler)
                        system.SaveStateInHistory(localTick);
                }
            }

            if (cachedIsServer)
            {
                using (WriteFrameOnServerMarker.Auto())
                {
                    WriteEventHandles();
                    SendFrameToOthers();
                }
            }

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].PostSimulate();

            if (cachedIsServer)
                FinalizeTickOnServer(cachedIsClient);
            else FinalizeInputOnClient(myPlayer);

            isSimulating = false;

            localTick += 1;
            localTickInContext = localTick;
        }

        private void PrepareInputs()
        {
            foreach (var (player, queue) in _clientTicks)
            {
                if (queue.Count == 0)
                {
                    queue.waitForInput = true;
                    continue;
                }

                var dequeued = queue.inputQueue.Peek();
                HandleIncomingInput(dequeued.inputPacket, dequeued.count, player);
            }
        }

        private void FinalizeInputOnClient(PlayerID myPlayer)
        {
            const int MTU = 1024;

            using var frame = BitPackerPool.Get();
            uint writtenCount = 0;
            for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
            {
                var system = _systems[systemIdx];
                system.GetLatestUnityState();
                if (system.hasInput && system.IsOwner(myPlayer))
                {
                    Packer<PredictedComponentID>.Write(frame, system.id);
                    system.WriteInput(localTick, default, frame, _deltaModuleState, false);
                    writtenCount += 1;
                }
            }

            if (frame.positionInBytes >= MTU)
                SendInputToServerReliable(localTick, writtenCount, frame);
            else SendInputToServer(localTick,writtenCount, frame);
        }

        private void FinalizeTickOnServer(bool cachedIsClient)
        {
            if (cachedIsClient)
            {
                for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.GetLatestUnityState();
                    system.UpdateRollbackInterpolationState(tickDelta, false);
                }
            }
            else
            {
                for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                    _systems[systemIdx].GetLatestUnityState();
            }
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
            var fCount = _clientFrames.Count;

            for (var j = 0; j < fCount; j++)
            {
                var frame = _clientFrames[j].packer;
                var player = _clientFrames[j].player;

                Packer<PackedInt>.Write(frame, _systemsCount);

                for (var i = 0; i < _systemsCount; i++)
                {
                    if (_systems[i].isEventHandler)
                        continue;

                    _systems[i].WriteCurrentState(player, frame, _deltaModuleState);
                }

                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].WriteInput(localTick, player, frame, _deltaModuleState, true);
            }
        }

        private void WriteEventHandles()
        {
            var fCount = _clientFrames.Count;

            for (var i = 0; i < _systemsCount; i++)
            {
                if (!_systems[i].isEventHandler)
                    continue;

                var system = _systems[i];

                for (var j = 0; j < fCount; j++)
                {
                    var frame = _clientFrames[j];
                    var packer = frame.packer;
                    system.WriteCurrentState(frame.player, packer, _deltaModuleState);
                }
            }
        }

        private void SendFrameToOthers()
        {
            var fCount = _clientFrames.Count;

            for (var j = 0; j < fCount; j++)
            {
                var player = _clientFrames[j].player;
                var packer = _clientFrames[j].packer;
                var deltaLen = packer.ToByteData().length;

                if (!_clientTicks.TryGetValue(player, out var queue))
                {
                    SendFrameToRemote(player, 0, new BitPackerWithLength(deltaLen, packer));
                    continue;
                }

                ulong tick = 0;

                if (queue.Count > 0 && !queue.waitForInput)
                {
                    var dequeued = queue.inputQueue.Dequeue();
                    tick = dequeued.clientTick;
                    dequeued.inputPacket.Dispose();
                }

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

        public bool isVerifiedAndReplaying
        {
            get => isVerified && isReplaying;
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
            // ReSharper disable once NotAccessedVariable
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

#if UNITY_PHYSICS_2D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0)
            {
                var physicsScene = gameObject.scene.GetPhysicsScene2D();
                if (physicsScene.IsValid())
                    physicsScene.Simulate(delta);
            }
#endif
#if UNITY_PHYSICS_3D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
            {
                var physicsScene = gameObject.scene.GetPhysicsScene();
                if (physicsScene.IsValid())
                    physicsScene.Simulate(delta);
            }
#endif

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

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SendFrameToRemote([UsedImplicitly] PlayerID player, ulong localTick, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);
            _deltas.Enqueue(new FrameDelta
            {
                packer = delta.packer,
                clientTick = localTick
            });
        }

        private void RollbackToFrame(ulong stateTick)
        {
            for (var i = 0; i < _systemsCount; i++)
                _systems[i].Rollback(stateTick);
            SyncTransforms();
        }

        private void RollbackToFrame(BitPacker frame, ulong stateTick, ulong inputTick)
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
                if (_validateDeterministicData && system.isDeterministic)
                    system.Rollback(stateTick);
                system.ClearFuture(stateTick);
                system.ReadState(stateTick, frame, _deltaModuleState);
                system.Rollback(stateTick);
            }

            for (var i = 0; i < count; ++i)
                _systems[i].ReadInput(inputTick, default, frame, _deltaModuleState, true);

            for (var i = 0; i < count; ++i)
            {
                var system = _systems[i];
                if (!system.isEventHandler)
                    continue;
                system.ClearFuture(stateTick);
                system.ReadState(stateTick, frame, _deltaModuleState);
                system.Rollback(stateTick);
            }

            SyncTransforms();
        }

        private void SyncTransforms()
        {
#if UNITY_PHYSICS_2D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0)
                Physics2D.SyncTransforms();
#endif
#if UNITY_PHYSICS_3D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
                Physics.SyncTransforms();
#endif
        }

        public event Action onStartingToRollback;
        public event Action onRollbackFinished;

        private ulong _lastVerifiedTick = 1;
        private bool _playedFirst;

        private void OnPostTick()
        {
            if (cachedIsServer || _deltas.Count == 0 || localTick <= _lastVerifiedTick)
            {
                if (isClient)
                    UpdateInterpolation(false);
                TickBandwidthProfiler.MarkEndOfTick();
                return;
            }

            onStartingToRollback?.Invoke();
            UpdateInterpolation(false);

            isSimulating = true;
            isReplaying = true;

            while (_deltas.Count > 0)
            {
                isVerified = true;
                using var previousFrame = _deltas.Dequeue();
                bool inPlace = previousFrame.clientTick <= 1;
                var lastTick = _lastVerifiedTick;
                if (!inPlace)
                    _lastVerifiedTick = previousFrame.clientTick;

                ulong verifiedTick = _lastVerifiedTick;
                bool isJump = verifiedTick - lastTick > 1;

                var inPlaceTick = isJump ? lastTick : verifiedTick;

                if (inPlace || isJump)
                {
                    if (_playedFirst)
                    {
                        RollbackToFrame(inPlaceTick);
                        SimulateFrameInPlace(inPlaceTick);
                        SimulateFrame(inPlaceTick, true);
                    }

                    _playedFirst = true;
                }

                RollbackToFrame(previousFrame.packer, inPlaceTick, verifiedTick);
                SimulateFrame(verifiedTick, true);
                isVerified = false;
            }

            ReplayToLatestTick(_lastVerifiedTick + 1);
            SyncTransforms();
            UpdateInterpolation(true);

            isReplaying = false;
            isSimulating = false;

            TickBandwidthProfiler.MarkEndOfTick();
            onRollbackFinished?.Invoke();
        }

        private void UpdateInterpolation(bool accumulateError)
        {
            for (var j = 0; j < _systemsCount; j++)
                _systems[j].UpdateRollbackInterpolationState(tickDelta, accumulateError);
        }

        private void ReplayToLatestTick(ulong verifiedTick)
        {
            for (ulong simTick = verifiedTick; simTick < localTick; simTick++)
                SimulateFrame(simTick, true);
        }

        private void SimulateFrameInPlace(ulong verifiedTick)
        {
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            isSimulating = true;
            localTickInContext = verifiedTick;

            using (SimulateMarker.Auto())
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].SimulateTick(verifiedTick, delta);
            }

            using (LateSimulateMarker.Auto())
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].LateSimulateTick(delta);
            }

            DoPhysicsPass();

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].PostSimulate();
            for (var j = 0; j < _systemsCount; j++)
                _systems[j].GetLatestUnityState();

            isSimulating = false;
            localTickInContext = localTick;
        }

        private void SimulateFrame(ulong verifiedTick, bool saveState)
        {
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            isSimulating = true;
            localTickInContext = verifiedTick;

            if (saveState)
            {
                using (SaveHistoryMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                    {
                        var system = _systems[i];
                        if (!system.isEventHandler)
                            system.SaveStateInHistory(verifiedTick);
                    }
                }
            }

            using (SimulateMarker.Auto())
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].SimulateTick(verifiedTick, delta);
            }

            using (LateSimulateMarker.Auto())
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].LateSimulateTick(delta);
            }

            DoPhysicsPass();

            if (saveState)
            {
                using (SaveHistoryMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                    {
                        var system = _systems[i];
                        if (system.isEventHandler)
                            system.SaveStateInHistory(verifiedTick);
                    }
                }
            }

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].PostSimulate();

            for (var j = 0; j < _systemsCount; j++)
                _systems[j].GetLatestUnityState();

            isSimulating = false;
            localTickInContext = localTick;
        }

        public struct InputQueueValue
        {
            public PackedUInt count;
            public BitPacker inputPacket;
            public ulong clientTick;
        }

        public class InputQueue
        {
            public bool waitForInput;
            public readonly Queue<InputQueueValue> inputQueue = new ();
            public int Count => inputQueue.Count;
        }

        readonly Dictionary<PlayerID, InputQueue> _clientTicks = new ();

        [ServerRpc(requireOwnership: false)]
        private void SendInputToServerReliable(ulong tick, PackedUInt count, BitPacker inputPacket, RPCInfo info = default)
        {
            ReceivedInput(tick, count, inputPacket, info);
        }

        [ServerRpc(requireOwnership: false, channel: Channel.UnreliableSequenced)]
        private void SendInputToServer(ulong tick, PackedUInt count, BitPacker inputPacket, RPCInfo info = default)
        {
            ReceivedInput(tick, count, inputPacket, info);
        }

        private void ReceivedInput(ulong tick, PackedUInt count, BitPacker inputPacket, RPCInfo info)
        {
            if (!_clientTicks.TryGetValue(info.sender, out var ticks))
            {
                ticks = new InputQueue
                {
                    waitForInput = true
                };
                _clientTicks[info.sender] = ticks;
            }

            // if we are past the max inputs, let's remove until we are at the min inputs
            if (ticks.Count > _inputQueueSettings.maxInputs)
            {
                while (ticks.Count > _inputQueueSettings.minInputs)
                {
                    var oldInput = ticks.inputQueue.Dequeue();
                    oldInput.inputPacket.Dispose();
                }
            }

            ticks.inputQueue.Enqueue(new InputQueueValue
            {
                count = count,
                inputPacket = inputPacket,
                clientTick = tick
            });

            if (ticks.waitForInput && ticks.inputQueue.Count >= _inputQueueSettings.minInputs)
                ticks.waitForInput = false;
        }

        private void HandleIncomingInput(BitPacker inputPacket, PackedUInt count, PlayerID sender)
        {
            try
            {
                bool senderIsServer = sender == default;

                for (var i = 0; i < count; i++)
                {
                    PredictedComponentID pid = default;
                    Packer<PredictedComponentID>.Read(inputPacket, ref pid);

                    if (_instanceMap.TryGetValue(pid, out var system) && system.IsOwner(sender, senderIsServer))
                    {
                        system.QueueInput(inputPacket, sender, _deltaModuleState, false);
                    }
                    else break;
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
            using (UpdateViewMarker.Auto())
            {
                var dt = Time.unscaledDeltaTime;
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].UpdateView(dt);
            }
        }

        public bool TryGetPrefab(int pid, out GameObject prefab)
        {
            if (pid < 0 || pid >= _predictedPrefabs.prefabs.Count)
            {
                prefab = null;
                return false;
            }

            prefab = _predictedPrefabs.prefabs[pid].prefab;
            return true;
        }

        public bool TryGetPrefab(GameObject prefab, out int id)
        {
            if (!_predictedPrefabs)
            {
                PurrLogger.LogError($"No predicted prefabs scriptable found on prediction manager! Make sure you've populated the field.", this);
                id = -1;
                return false;
            }

            var prefabs = _predictedPrefabs.prefabs;
            for (id = 0; id < prefabs.Count; id++)
            {
                if (prefabs[id].prefab == prefab)
                    return true;
            }

            id = -1;
            return false;
        }

        public static void ProperlySetPosAndRot(Transform transform, Vector3 position, Quaternion rotation)
        {
#if UNITY_PHYSICS_2D
            if (transform.TryGetComponent(out Rigidbody2D rb2d))
            {
                rb2d.position = position;
                rb2d.rotation = rotation.eulerAngles.z;
                transform.SetPositionAndRotation(position, rotation);
                return;
            }
#endif
#if UNITY_PHYSICS_3D
            if  (transform.TryGetComponent(out Rigidbody rb))
            {
                rb.position = position;
                rb.rotation = rotation;
                transform.SetPositionAndRotation(position, rotation);
                return;
            }

            if (transform.TryGetComponent(out CharacterController ctrler) && ctrler.enabled)
            {
                ctrler.enabled = false;
                transform.SetPositionAndRotation(position, rotation);
                ctrler.enabled = true;
                return;
            }
#endif
            transform.SetPositionAndRotation(position, rotation);
        }

        internal GameObject InternalCreate(GameObject prefab, Vector3 position, Quaternion rotation, PredictedObjectID objectId, PlayerID? owner)
        {
            if (_pools.TryGetPool(prefab, out var pool))
            {
                var go = pool.Allocate();
                var trs = go.transform;
                ProperlySetPosAndRot(trs, position, rotation);
                trs.SetParent(null);
                RegisterInstance(go, objectId, owner, true);
                return go;
            }
            else
            {
                var go = UnityProxy.InstantiateDirectly(prefab, position, rotation);
                RegisterInstance(go, objectId, owner, false);
                return go;
            }
        }

        internal void InternalDelete(PackedInt prefabId, GameObject instance)
        {
            int pid = prefabId;

            if (!_predictedPrefabs || pid < 0 || pid >= _predictedPrefabs.prefabs.Count)
            {
                UnityProxy.DestroyImmediateDirectly(instance);
                return;
            }

            var prefabsInfo = _predictedPrefabs.prefabs[pid];

            if (!prefabsInfo.pooling.usePooling)
            {
                UnityProxy.DestroyImmediateDirectly(instance);
                return;
            }

            if (_pools.TryGetPool(prefabsInfo.prefab, out var pool))
            {
                UnregisterPooledInstance(instance);
                pool.Delete(instance);
            }
            else UnityProxy.DestroyImmediateDirectly(instance);
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

        public static bool TryGetClosestPredictedID(GameObject go, out PredictedComponentID pid)
        {
            if (go.TryGetComponent<PredictedIdentity>(out var identity))
            {
                pid = identity.id;
                return true;
            }

            var parent = go.GetComponentInParent<PredictedIdentity>();
            if (parent != null)
            {
                pid = parent.id;
                return true;
            }

            pid = default;
            return false;
        }
    }
}
