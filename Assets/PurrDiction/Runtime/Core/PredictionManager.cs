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
using UnityEngine.SceneManagement;

namespace PurrNet.Prediction
{
    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("PurrDiction/Prediction Manager")]
    public partial class PredictionManager : NetworkIdentity
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();

#if UNITY_6000_3_OR_NEWER
        static readonly Dictionary<SceneHandle, PredictionManager> _instances = new ();
#else
        static readonly Dictionary<int, PredictionManager> _instances = new ();
#endif

#if UNITY_6000_3_OR_NEWER
        public static event Action<SceneHandle, PredictionManager> OnInstanceAdded;
#else
        public static event Action<int, PredictionManager> OnInstanceAdded;
#endif

        [SerializeField] private PredictionPhysicsProvider _physicsProvider;
        [SerializeField] private UpdateViewMode _updateViewMode = UpdateViewMode.Update;
        [SerializeField, PurrLock] private BuiltInSystems _builtInSystems =
            BuiltInSystems.Physics3D |
            BuiltInSystems.Physics2D |
            BuiltInSystems.Time |
            BuiltInSystems.Hierarchy |
            BuiltInSystems.Players |
            BuiltInSystems.Random;
        [SerializeField] private PredictedPrefabs _predictedPrefabs;
        [Tooltip("When a client's input for the current tick has not arrived, reuse its last known input instead of simulating with default input.")]
        [SerializeField] private bool _extrapolateMissingInputs = true;

        [Header("Determinism")]
        [Tooltip("How the server responds when a client's deterministic state diverges. Per-identity overrides on DeterministicIdentity take precedence. Ignore has zero overhead.")]
        [SerializeField] private DesyncPolicy _desyncPolicy = DesyncPolicy.Ignore;
        [Tooltip("How often clients report deterministic state hashes to the server, in seconds. Only applies when the resolved policy of at least one identity is not Ignore.")]
        [SerializeField, Min(0.05f)] private float _desyncCheckIntervalSeconds = 0.25f;

        public PredictedPrefabs predictedPrefabs
        {
            get => _predictedPrefabs;
            set
            {
                _predictedPrefabs = value;
                InitPooling();
            }
        }

        public DesyncPolicy desyncPolicy => _desyncPolicy;

        static readonly ProfilerMarker SimulateMarker = new("PredictionManager.Simulate");
        static readonly ProfilerMarker SimulateInputsMarker = new("PredictionManager.PrepareSimulationInputs");
        static readonly ProfilerMarker LateSimulateMarker = new("PredictionManager.LateSimulate");
        static readonly ProfilerMarker UpdateViewMarker = new("PredictionManager.UpdateView");
        static readonly ProfilerMarker SaveHistoryMarker = new("PredictionManager.SaveHistory");
        static readonly ProfilerMarker WriteFrameOnServerMarker = new("PredictionManager.WriteFrameOnServer");
        static readonly ProfilerMarker WriteInputHistoryMarker = new("PredictionManager.WriteInputHistory");
        static readonly ProfilerMarker WriteStateDeltasMarker = new("PredictionManager.WriteStateDeltas");
        static readonly ProfilerMarker WriteFullFrameMarker = new("PredictionManager.WriteFullFrame");
        static readonly ProfilerMarker WriteEventHandlesMarker = new("PredictionManager.WriteEventHandles");
        static readonly ProfilerMarker SendFrameMarker = new("PredictionManager.SendFrame");
        static readonly ProfilerMarker RollbackToFrameMarker = new("PredictionManager.RollbackToFrame");
        static readonly ProfilerMarker ReadInputHistoryMarker = new("PredictionManager.ReadInputHistory");
        static readonly ProfilerMarker ReplayToLatestTickMarker = new("PredictionManager.ReplayToLatestTick");

        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();
        private int _systemsCount;

        GameObjectPoolCollection _pools;

        [UsedImplicitly]
#if UNITY_6000_3_OR_NEWER
        public static bool TryGetInstance(SceneHandle sceneHandle, out PredictionManager world)
#else
        public static bool TryGetInstance(int sceneHandle, out PredictionManager world)
#endif
        {
            return _instances.TryGetValue(sceneHandle, out world);
        }

        [ContextMenu("Debug/Print all systems")]
        public void PrintAllSystems()
        {
            foreach (var system in _systems)
                Debug.Log(system, system);
        }

        private uint _sessionSeed;

        /// <summary>
        /// The session seed for this prediction manager instance.
        /// This is randomly generated on Awake and is used to seed any predicted random number generators.
        ///
        /// </summary>
        public uint sessionSeed => _sessionSeed;

        private void Awake()
        {
            var sceneHandle = gameObject.scene.handle;
            _instances[sceneHandle] = this;
            OnInstanceAdded?.Invoke(sceneHandle, this);
            _sessionSeed = (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);

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

        private GameObject _poolParent;

        private void InitPooling()
        {
            if (!_predictedPrefabs)
                return;

            if (!_poolParent)
            {
                _poolParent = new GameObject("PooledPrefabs");
                SceneManager.MoveGameObjectToScene(_poolParent, gameObject.scene);
#if !PURRNET_DEBUG_POOLING
                _poolParent.hideFlags = HideFlags.HideAndDontSave;
#endif
                _poolParent.SetActive(false);
            }

            _pools ??= new GameObjectPoolCollection(_poolParent.transform);
            for (var i = 0; i < _predictedPrefabs.prefabs.Count; i++)
            {
                var prefab = _predictedPrefabs.prefabs[i];
                if (prefab.pooled)
                    _pools.Register(prefab.prefab, prefab.warmupCount);
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

        public PredictedRandomSystem random { get; private set; }

        internal interface IVerifiedStateStore
        {
            void Clear();
        }

        private sealed class VerifiedStateStore<T> : IVerifiedStateStore where T : struct, IDisposable
        {
            public readonly History<T> history;

            public VerifiedStateStore(int capacity)
            {
                history = new History<T>(capacity);
            }

            public void Clear() => history.Clear();
        }

        readonly Dictionary<(uint, PredictedComponentID, int), IVerifiedStateStore> _verifiedStores = new ();

        internal History<T> GetVerifiedHistory<T>(PredictedComponentID componentId, out bool created) where T : struct, IDisposable
        {
            return GetVerifiedHistory<T>(componentId, 0, out created);
        }

        internal History<T> GetVerifiedHistory<T>(PredictedComponentID componentId, int subKey, out bool created) where T : struct, IDisposable
        {
            var key = (Hasher<T>.stableHash, componentId, subKey);

            if (_verifiedStores.TryGetValue(key, out var store))
            {
                created = false;
                return ((VerifiedStateStore<T>)store).history;
            }

            var newStore = new VerifiedStateStore<T>(tickRate * 10);
            _verifiedStores[key] = newStore;
            created = true;
            return newStore.history;
        }

        private void ClearVerifiedStores()
        {
            foreach (var store in _verifiedStores.Values)
                store.Clear();
            _verifiedStores.Clear();
        }

        bool ShouldRegisterSystem(BuiltInSystems system)
        {
            return (_builtInSystems & system) != 0;
        }

        protected override void OnEarlySpawn()
        {
            RegisterScene();

            tickRate = networkManager.tickModule.tickRate;
            tickDelta = 1f / tickRate;

            hierarchy = ShouldRegisterSystem(BuiltInSystems.Hierarchy) ? RegisterSystem<PredictedHierarchy>() : null;
            players = ShouldRegisterSystem(BuiltInSystems.Players) ? RegisterSystem<PredictedPlayers>() : null;
            physics3d = ShouldRegisterSystem(BuiltInSystems.Physics3D) ? RegisterSystem<Predicted3DPhysics>() : null;
            physics2d = ShouldRegisterSystem(BuiltInSystems.Physics2D) ? RegisterSystem<Predicted2DPhysics>() : null;
            time = ShouldRegisterSystem(BuiltInSystems.Time) ? RegisterSystem<PredictedTime>() : null;
            random = ShouldRegisterSystem(BuiltInSystems.Random) ? RegisterSystem<PredictedRandomSystem>() : null;

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
                            hierarchy.ReserveSceneObject(root, pid--);
                    }
                }

                hierarchy.RegisterReservedSceneObjects();
            }

            HashSetPool<GameObject>.Destroy(roots);

            WarnAboutUndiscoveredIdentities();

            _queue.Clear();

            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0 ||
                (_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
            {
                Time.fixedDeltaTime = tickDelta;
            }
        }

        /// <summary>
        /// Identities that live in the manager's scene but were skipped by scene discovery
        /// never get Setup: their state stays uninitialized and their physics events resolve
        /// to id 0 (null 'other' in callbacks). This is always a setup mistake, so surface it.
        /// </summary>
        private void WarnAboutUndiscoveredIdentities()
        {
            var known = HashSetPool<PredictedIdentity>.Instantiate();
            for (var i = 0; i < _queue.Count; i++)
                known.Add(_queue[i]);

            var all = UnityEngine.Object.FindObjectsByType<PredictedIdentity>(FindObjectsSortMode.None);
            for (var i = 0; i < all.Length; i++)
            {
                var identity = all[i];

                if (!identity ||
                    identity.gameObject.scene.handle != gameObject.scene.handle ||
                    identity.transform.root == transform.root ||
                    identity.predictionManager ||
                    known.Contains(identity))
                {
                    continue;
                }

                PurrLogger.LogWarning(
                    $"PredictedIdentity '{identity.name}' ({identity.GetType().Name}) is in the scene but was not discovered during registration. " +
                    "It will never simulate or sync, its state stays uninitialized, and physics events involving it pass a null 'other' to callbacks. " +
                    "Spawn it with PredictionManager.hierarchy.Create, keep it in the scene file, or enable 'includeInstantiatedSceneObjects' in NetworkRules.",
                    identity);
            }

            HashSetPool<PredictedIdentity>.Destroy(known);
        }

        private void RegisterScene()
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();

#if HAS_DISCOVERY_RULE
            SceneObjectsModule.GetScenePredictedIdentities(gameObject.scene, identities, networkManager.networkRules.ShouldIncludeInstantiatedSceneObjects());
#else
            SceneObjectsModule.GetScenePredictedIdentities(gameObject.scene, identities);
#endif

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
                _tickManager.tickPacingScale = 1d;
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
                _tickManager.tickPacingScale = 1d;
            }

            if (_pools != null)
            {
                _pools.Dispose();
                _pools = null;
            }

            DisposeCachedInputPayload();
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
            _replayFrozenSystems.Clear();
            _speculativeRelayLocks.Clear();
            _systemsCount = 0;
            _guaranteedInputHistorySystems = 0;
            DisposeInputBlockCache();
            InvalidateInputBlockCache();
            DisposeNewestInputRing();
            DisposeCachedInputPayload();
            _nextSystemId = 0;
            foreach (var queue in _clientTicks.Values)
                queue.Clear();
            _clientTicks.Clear();
            foreach (var packer in _clientFrames)
                packer.Dispose();
            _clientFrames.Clear();
            localTick = 1;
            localTickInContext = 1;
            _verifiedServerTick = 0;
            _lastPerformanceReconcileTime = double.NegativeInfinity;
            _ackedServerTick = 0;
            _recordDecodeQuarantine.Clear();
            _recordFailureLogAt.Clear();
            _pauseAdvanceTicks = 0;
            _latestFrameServerTick = 0;
            _inputStarved = false;
            _inputAckTick = 0;
            _frameInputMargin = 0;
            _frameInputMarginTick = 0;
            _hasFrameInputMargin = false;
            _leadAdjustGateTick = 0;
            _frameInputSlackMs = 0;
            _frameInputSlackServerTick = 0;
            _frameInputSlackInputTick = 0;
            _serverSendsInputSlack = false;
            _serverTickBoundaryTime = 0;
            lastInputSlackMs = 0;
            leadJumpsTotal = 0;
            leadPausesTotal = 0;
            minLeadSnapsTotal = 0;
            starvationJumpsTotal = 0;
            ResetSlackController();
            reliableFramesSentTotal = 0;
            fullFramesSentTotal = 0;
            deltaSectionDeleteBitsTotal = 0;
            deltaSectionHierarchyBitsTotal = 0;
            deltaSectionInputBitsTotal = 0;
            deltaSectionStateBitsTotal = 0;
            deltaFramesWrittenTotal = 0;
            deltaFrameBytesTotal = 0;
            maxDeltaFrameBytes = 0;
            fullFrameBytesTotal = 0;
            renderPhaseFrameAppliesTotal = 0;
            tickPhaseFrameAppliesTotal = 0;
            maxFrameApplyAgeFrames = 0;
            refreshViewLatchOnly = false;
            ClearVerifiedStores();
            ClearVisibilityReplication();
            while (_deltas.Count > 0)
                _deltas.Dequeue().Dispose();
        }

        private uint _nextSystemId;

        public T RegisterSystem<T>() where T : PredictedIdentity
        {
            var system = gameObject.AddComponent<T>();
            system.hideFlags = HideFlags.NotEditable;
            if (cachedIsServer)
                system.OnPreSetup();
            RegisterInstance(system, new PredictedObjectID(1), _nextSystemId++, null);
            return system;
        }

        public void RegisterInstance(GameObject go, PredictedObjectID objectID, PlayerID? owner, bool reset, bool triggedOnRemovedFromPool)
        {
            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponents(components);
            int count = components.Count;

            for (uint i = 0; i < count; i++)
            {
                var component = components[(int)i];

                if (!_systems.Contains(component))
                {
                    var componentId = new PredictedComponentID(objectID, i);
                    bool preserveState = !reset && !component.isFreshSpawn && component.id.Equals(componentId);
                    bool recycledForNewId = !reset && !component.isFreshSpawn && !component.id.Equals(componentId);
                    var incomingPolicy = component.ResolveEffectivePredictionPolicyForSetup(owner, this);
                    bool preserveSoftState = preserveState &&
                                             component.previousRegisteredEffectivePredictionPolicy == PredictionPolicy.SoftCorrection &&
                                             incomingPolicy == PredictionPolicy.SoftCorrection;

                    if (!preserveSoftState)
                        component.OnPreSetup();
                    if (reset || recycledForNewId)
                         component.ResetState();
                    if (triggedOnRemovedFromPool)
                        component.TriggerOnRemovedFromPool();
                    RegisterInstance(component, objectID, i, owner, preserveSoftState);
                }
            }

            ListPool<PredictedIdentity>.Destroy(components);
        }

        public void UnregisterInstance(GameObject go, bool reset, bool destroyEvent)
        {
            if (!go)
                return;

            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponents(components);

            for (var i = 0; i < components.Count; i++)
            {
                if (components[i].hideFlags != HideFlags.NotEditable)
                {
                    UnregisterInstance(components[i]);
                    if (reset)
                        components[i].ResetState();
                    if (destroyEvent)
                        components[i].TriggerDestroyedEvent();
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

        private void RegisterInstance(PredictedIdentity system, PredictedObjectID objectId, uint componentId, PlayerID? owner, bool preserveState = false)
        {
            if (!isSpawned)
            {
                _queue.Add(system);
                return;
            }

            var pid = new PredictedComponentID(objectId, componentId);
            _instanceMap[pid] = system;
            system.SetSoftCorrectionReplaySimulation(false);
            system.SetSkipReplaySpawnInitialization(false);
            system.SetPreserveStateOnSetup(preserveState);
            try
            {
                system.Setup(networkManager, this, pid, owner);
                system.CompletePredictionPolicySetup();
            }
            finally
            {
                system.CancelPendingPredictionPolicySetup();
                system.SetPreserveStateOnSetup(false);
            }

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
            if (system.requiresGuaranteedInputHistory)
                ++_guaranteedInputHistorySystems;
            InvalidateInputBlockCache();

            if (isReplaying && system.UsesSoftCorrectionTimeline() && !preserveState)
            {
                system.OnReplayEnd();
                system.SetSoftCorrectionReplaySimulation(true);
            }
            else if (isReplaying && preserveState && system.UsesSoftCorrectionTimeline())
            {
                system.SetSkipReplaySpawnInitialization(true);
            }
        }

        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            RemoveSpeculativeRelayLock(predictedIdentity);
            if (_systems.Contains(predictedIdentity))
                HandleVisibilitySystemRemoved(predictedIdentity);

            // A pooled instance keeps its old id, so an expiring pool entry can tear down an
            // identity whose id has already been re-registered to a live replacement. Only drop
            // the lookup when it still resolves to this exact identity.
            if (_instanceMap.TryGetValue(predictedIdentity.id, out var mapped) &&
                ReferenceEquals(mapped, predictedIdentity))
            {
                _instanceMap.Remove(predictedIdentity.id);
                _recordDecodeQuarantine.Remove(predictedIdentity.id);
                _recordFailureLogAt.Remove(predictedIdentity.id);
            }

            if (_systems.Remove(predictedIdentity))
            {
                --_systemsCount;
                if (predictedIdentity.requiresGuaranteedInputHistory)
                {
                    --_guaranteedInputHistorySystems;
                    ForceFullFramesForAllClients();
                }
                predictedIdentity.RecordCompletedRegistrationPolicy();
                InvalidateInputBlockCache();
            }
        }

        protected override void OnObserverRemoved(PlayerID player)
        {
            _clientTicks.Remove(player);
            _pendingFullSync.Remove(player);
            RemovePlayerVisibility(player);

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
            if (player == localPlayer || player.isBot)
                return;

            if (localTick == 1)
                OnPreTick();
        }

        readonly List<PlayerID> _pendingFullSync = new ();

        protected override void OnObserverAdded(PlayerID player)
        {
            if (player == localPlayer || player.isBot)
                return;

            _pendingFullSync.Add(player);
        }

        private void FlushPendingFullSyncs()
        {
            for (var p = 0; p < _pendingFullSync.Count; p++)
            {
                var player = _pendingFullSync[p];
                var mtu = networkManager.GetMTU(player, Channel.Unreliable, true);
                var maxUnreliableFrameBytes = GetMaxUnreliableFrameBytes(mtu);

                _clientTicks[player] = new InputQueue();
                ClearDesyncTrackingForPlayer(player);

                var found = false;
                for (var i = 0; i < _clientFrames.Count; i++)
                {
                    var clientFrame = _clientFrames[i];
                    if (!clientFrame.player.Equals(player))
                        continue;

                    clientFrame.fullFrame = true;
                    clientFrame.preparedFrameTick = 0;
                    clientFrame.preparedVisibilityTick = 0;
                    clientFrame.sentVisibilityTick = 0;
                    clientFrame.maxUnreliableFrameBytes = maxUnreliableFrameBytes;
                    clientFrame.reliableFrame.Clear();
                    clientFrame.baselineAdvance.Reset();
                    _clientFrames[i] = clientFrame;
                    found = true;
                    break;
                }

                if (found)
                    continue;

                _clientFrames.Add(new PlayerPacker
                {
                    player = player,
                    packer = BitPackerPool.Get(),
                    fullFrame = true,
                    maxUnreliableFrameBytes = maxUnreliableFrameBytes
                });
            }

            _pendingFullSync.Clear();
        }

        private void ReadFullFrame(BitPacker frame, ulong stateTick, ulong inputTick, ulong serverTick)
        {
            frame.ResetPositionAndMode(true);

            tickRate = Packer<PackedInt>.Read(frame);
            tickDelta = Packer<float>.Read(frame);
            _sessionSeed = Packer<uint>.Read(frame);

            ReadVisibilityDeleteSection(frame);
            ReadAddressedHierarchyRecord(frame, stateTick, 0, serverTick, true);
            ReadAddressedStateRecords(frame, stateTick, 0, serverTick, true, false);
            ReadAddressedFirstInputSection(frame, inputTick);
            ReadAddressedStateRecords(frame, stateTick, 0, serverTick, true, true);

            SyncTransforms();
            PredictionPerformanceTelemetry.StateRestored(this);
        }

        readonly List<PlayerPacker> _clientFrames = new (16);

        public bool cachedIsServer { get; private set; }

        private void OnPreTick()
        {
            cachedIsServer = isServer;
            localTickInContext = localTick;

            if (cachedIsServer && _tickManager != null)
                _serverTickBoundaryTime = _tickManager.lastTickTime;

            if (!cachedIsServer && _pauseAdvanceTicks > 0)
            {
                _pauseAdvanceTicks--;
                return;
            }

            using var performance = PredictionPerformanceTelemetry.BeginPass(this, PredictionPassKind.Forward);
            var myPlayer = isSpawned ? localPlayer ?? default : default;
            var cachedIsClient = isClient;

            isSimulating = true;
            if (cachedIsServer)
                isVerified = true;

            LockSpeculativeRelayStates(localTick);

            if (cachedIsServer)
                PrepareInputs();

            using var ownedIdentities = DisposableList<PredictedIdentity>.Create(_systemsCount);

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                bool controller = system.IsOwner(myPlayer, cachedIsServer);
                if (controller)
                    ownedIdentities.Add(system);
                system.PrepareInput(cachedIsServer, controller, localTick, _extrapolateMissingInputs);
            }

            if (!cachedIsServer)
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].SynchronizeEffectivePredictionPolicy();
            }

            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (!system.isEventHandler)
                        system.RunSaveState(localTick);
                }
            }

            if (cachedIsServer)
            {
                using (WriteFrameOnServerMarker.Auto())
                {
                    if (_pendingFullSync.Count > 0)
                        FlushPendingFullSyncs();
                    WriteInitialFrameToOthers();
                }
            }

            float delta = this.tickDelta;

            if (time)
                delta *= time.timeScale;

            long prepareStarted = performance.Timestamp();
            using (SimulateInputsMarker.Auto())
            {
                try
                {
                    for (var i = 0; i < _systemsCount; i++)
                        _systems[i].RunPrepareSimulationInputs(localTick, delta);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            performance.PrepareDone(prepareStarted);
            long simulateStarted = performance.Timestamp();
            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunSimulateTick(localTick, delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                simulateMarker.Dispose();
            }

            performance.SimulateDone(simulateStarted);
            DoPhysicsPass(performance);

            long lateStarted = performance.Timestamp();
            var lateSimulateMarker = LateSimulateMarker.Auto();
            try
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunLateSimulateTick(delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateSimulateMarker.Dispose();
            }
            performance.LateDone(lateStarted);

            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (system.isEventHandler)
                        system.RunSaveState(localTick);
                }
            }

            if (cachedIsServer)
            {
                using (WriteFrameOnServerMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                        _systems[i].lastVerifiedTick = localTick;
                    WriteEventHandles();
                    SendFrameToOthers();
                }
            }

            try
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunPostSimulate();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            RestoreSpeculativeRelayStates();

            if (cachedIsServer)
                FinalizeTickOnServer(cachedIsClient);
            else FinalizeInputOnClient(ownedIdentities);

            isSimulating = false;

            localTick += 1;
            localTickInContext = localTick;
        }

        private void PrepareInputs()
        {
            foreach (var (player, queue) in _clientTicks)
            {
                if (queue.byTick.Remove(localTick, out var entry))
                {
                    HandleIncomingInput(entry.inputPacket, entry.count, player);
                    entry.inputPacket.Dispose();
                    queue.lastConsumedTick = localTick;
                }

                if (queue.Count > 0)
                    PruneStaleInputs(queue);
            }
        }

        private static readonly List<ulong> _staleInputScratch = new ();

        private void PruneStaleInputs(InputQueue queue)
        {
            _staleInputScratch.Clear();

            foreach (var tick in queue.byTick.Keys)
            {
                if (tick <= localTick)
                    _staleInputScratch.Add(tick);
            }

            for (var i = 0; i < _staleInputScratch.Count; i++)
            {
                if (queue.byTick.Remove(_staleInputScratch[i], out var stale))
                    stale.inputPacket.Dispose();
            }
        }

        private const int InputMtu = 960;
        private const ulong MaxInputWindow = 32;
        private const double InputResendIntervalSeconds = 0.02;
        // Upload copies of an input tick are only useful while they can still beat the server's
        // simulation deadline; the adaptive lead controller steers input arrival into
        // [inputMarginTarget, inputMarginHigh] ticks of slack, so any copy sent more than
        // marginHigh ticks after the first can never arrive in time. The redundancy window is
        // therefore the margin high band plus one - larger values are pure waste, smaller ones
        // give away loss protection the deadline still permits.
        internal static ulong InputRedundancyTicks(int tickRate)
        {
            var ticks = (ulong)(2 * InputMarginTargetTicks(tickRate) + 1);
            if (ticks < 4)
                ticks = 4;
            return ticks > MaxInputWindow ? MaxInputWindow : ticks;
        }

        private int _guaranteedInputHistorySystems;

        /// <summary>
        /// Number of registered systems whose input rides the guaranteed transcript.
        /// Diagnostic only.
        /// </summary>
        public int guaranteedInputHistorySystems => _guaranteedInputHistorySystems;

        /// <summary>
        /// The upload loss-burst redundancy window in ticks at the current tick rate.
        /// Diagnostic only.
        /// </summary>
        public ulong inputRedundancyTickCount => InputRedundancyTicks(tickRate);

        private ulong _inputAckTick;

        private BitPacker _cachedInputPayload;
        private ulong _cachedInputFirstTick;
        private uint _cachedInputTickCount;
        private bool _cachedInputFragmented;
        private double _lastInputSendTime;

        private void CacheInputPayload(ulong firstTick, uint tickCount, BitPacker payload)
        {
            _cachedInputPayload?.Dispose();
            _cachedInputPayload = BitPackerPool.Get();
            _cachedInputPayload.WriteBitsWithoutConsumingIt(payload, payload.positionInBits);
            _cachedInputFirstTick = firstTick;
            _cachedInputTickCount = tickCount;
            _cachedInputFragmented = payload.positionInBytes >= InputMtu;
        }

        private void DisposeCachedInputPayload()
        {
            _cachedInputPayload?.Dispose();
            _cachedInputPayload = null;
            _cachedInputTickCount = 0;
            _lastInputSendTime = 0;
        }

        private void ResendCachedInput()
        {
            if (_cachedInputPayload == null || _cachedInputTickCount == 0)
                return;

            var now = Time.unscaledTimeAsDouble;
            if (now - _lastInputSendTime < InputResendIntervalSeconds)
                return;

            _lastInputSendTime = now;

            using var payload = BitPackerPool.Get();
            payload.WriteBitsWithoutConsumingIt(_cachedInputPayload, _cachedInputPayload.positionInBits);

            if (_cachedInputFragmented)
                SendInputToServerFragmented(_cachedInputFirstTick, _cachedInputTickCount, _ackedServerTick, payload);
            else SendInputToServer(_cachedInputFirstTick, _cachedInputTickCount, _ackedServerTick, payload);

            inputSendsTotal++;
            inputBytesSentTotal += (ulong)payload.positionInBytes;
            inputTicksSentTotal += _cachedInputTickCount;
        }

        private void FinalizeInputOnClient(DisposableList<PredictedIdentity> ownedIdentities)
        {
            try
            {
                for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                    _systems[systemIdx].RunGetLatestUnityState();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            ulong firstTick = _inputAckTick + 1;

            if (localTick >= MaxInputWindow && firstTick < localTick - MaxInputWindow + 1)
                firstTick = localTick - MaxInputWindow + 1;

            var redundancy = InputRedundancyTicks(tickRate);
            ulong cappedFirstTick = localTick >= redundancy ? localTick - redundancy + 1 : firstTick;
            if (cappedFirstTick < firstTick)
                cappedFirstTick = firstTick;

            var count = ownedIdentities.Count;

            bool anyOwnedGuaranteed = false;
            for (var ownedIdx = 0; ownedIdx < count; ownedIdx++)
            {
                var owned = ownedIdentities[ownedIdx];
                if (owned && owned.hasInput && owned.requiresGuaranteedInputHistory)
                {
                    anyOwnedGuaranteed = true;
                    break;
                }
            }

            if (!anyOwnedGuaranteed)
                firstTick = cappedFirstTick;

            if (firstTick > localTick)
                firstTick = localTick;

            using var payload = BitPackerPool.Get();
            using var blockA = BitPackerPool.Get();
            using var blockB = BitPackerPool.Get();
            using var wireBlock = BitPackerPool.Get();

            var prevBlock = blockA;
            var curBlock = blockB;
            var prevSpans = _uploadPrevSpans;
            var curSpans = _uploadCurSpans;
            prevSpans.Clear();
            curSpans.Clear();

            uint tickCount = 0;

            for (ulong tick = firstTick; tick <= localTick; tick++)
            {
                curBlock.ResetPositionAndMode(false);
                curSpans.Clear();
                bool cappedRegion = tick >= cappedFirstTick;

                for (var ownedIdx = 0; ownedIdx < count; ownedIdx++)
                {
                    var owned = ownedIdentities[ownedIdx];
                    if (owned && owned.hasInput &&
                        (cappedRegion || owned.requiresGuaranteedInputHistory))
                    {
                        int origin = curBlock.positionInBits;
                        owned.WriteFirstInput(tick, curBlock);
                        curSpans.Add(new InputHistorySpan
                        {
                            id = owned.id,
                            bitOrigin = origin,
                            bitLength = curBlock.positionInBits - origin
                        });
                    }
                }

                wireBlock.ResetPositionAndMode(false);
                for (var s = 0; s < curSpans.Count; s++)
                {
                    var span = curSpans[s];
                    Packer<PredictedComponentID>.Write(wireBlock, span.id);

                    bool repeat = false;
                    for (var p = 0; p < prevSpans.Count; p++)
                    {
                        if (!prevSpans[p].id.Equals(span.id))
                            continue;
                        var prevSpan = prevSpans[p];
                        repeat = new BitData(curBlock, span.bitOrigin, span.bitLength)
                            .Equals(new BitData(prevBlock, prevSpan.bitOrigin, prevSpan.bitLength));
                        break;
                    }

                    Packer<bool>.Write(wireBlock, repeat);
                    if (repeat)
                        continue;

                    Packer<PackedUInt>.Write(wireBlock, (uint)span.bitLength);
                    wireBlock.WriteBitDataWithoutConsumingIt(
                        new BitData(curBlock, span.bitOrigin, span.bitLength));
                }

                int blockBits = wireBlock.positionInBits;
                Packer<PackedUInt>.Write(payload, (uint)blockBits);
                Packer<PackedUInt>.Write(payload, (uint)curSpans.Count);
                payload.WriteBitsWithoutConsumingIt(wireBlock, blockBits);
                tickCount += 1;

                (prevBlock, curBlock) = (curBlock, prevBlock);
                (prevSpans, curSpans) = (curSpans, prevSpans);
            }

            CacheInputPayload(firstTick, tickCount, payload);
            _lastInputSendTime = Time.unscaledTimeAsDouble;

            if (payload.positionInBytes >= InputMtu)
                SendInputToServerFragmented(firstTick, tickCount, _ackedServerTick, payload);
            else SendInputToServer(firstTick, tickCount, _ackedServerTick, payload);

            inputSendsTotal++;
            inputBytesSentTotal += (ulong)payload.positionInBytes;
            inputTicksSentTotal += tickCount;
        }

        private void FinalizeTickOnServer(bool cachedIsClient)
        {
            if (cachedIsClient)
            {
                for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.GetLatestUnityState();
                    system.RunUpdateRollbackInterpolation(tickDelta, false);
                }
            }
            else
            {
                for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                    _systems[systemIdx].GetLatestUnityState();
            }
        }

        private void WriteInitialFrameToOthers()
        {
            var fCount = _clientFrames.Count;
            PackedInt packedTickRate = tickRate;
            ulong maxAckLag = 0;

            for (var j = 0; j < fCount; j++)
            {
                var clientFrame = _clientFrames[j];
                var player = clientFrame.player;

                ulong baselineTick = 0;
                if (_clientTicks.TryGetValue(player, out var ackQueue))
                    baselineTick = ackQueue.ackedServerTick;

                ulong ackLag = localTick > baselineTick ? localTick - baselineTick : 0;
                if (ackLag > maxAckLag)
                    maxAckLag = ackLag;

                clientFrame.baselineAdvance.Observe(
                    localTick,
                    baselineTick,
                    ReliableRecoveryWindowTicks(tickRate));

                if (clientFrame.reliableFrame.ShouldSuppress(baselineTick))
                {
                    suppressedTicksTotal++;
                    if (clientFrame.reliableSentAtLocalTick != 0 &&
                        (localTick - clientFrame.reliableSentAtLocalTick) % RecoveryHedgeIntervalTicks == 0)
                    {
                        var latchedLen = clientFrame.packer.ToByteData().length;
                        if (latchedLen <= clientFrame.maxUnreliableFrameBytes)
                        {
                            SendFrameToRemote(
                                player,
                                clientFrame.reliableSentAtLocalTick,
                                clientFrame.reliableSentBaselineTick,
                                clientFrame.reliableSentInputAck,
                                clientFrame.reliableSentFullFrame,
                                false,
                                0,
                                false,
                                0,
                                new BitPackerWithLength(latchedLen, clientFrame.packer));
                        }
                    }
                    clientFrame.preparedFrameTick = 0;
                    clientFrame.preparedVisibilityTick = 0;
                    _clientFrames[j] = clientFrame;
                    continue;
                }

                if (clientFrame.reliableSentAtLocalTick != 0)
                {
                    ulong latchTicks = localTick > clientFrame.reliableSentAtLocalTick
                        ? localTick - clientFrame.reliableSentAtLocalTick
                        : 0;
                    latchCyclesTotal++;
                    latchTicksTotal += latchTicks;
                    if (latchTicks > maxLatchTicks)
                        maxLatchTicks = latchTicks;
                    clientFrame.reliableSentAtLocalTick = 0;
                }

                clientFrame.preparedFrameTick = localTick;

                if (!clientFrame.fullFrame && clientFrame.baselineAdvance.distressed)
                {
                    clientFrame.fullFrame = true;
                }

                if (!clientFrame.fullFrame && baselineTick > 0 &&
                    localTick > baselineTick && localTick - baselineTick > (ulong)(tickRate * 8))
                {
                    clientFrame.fullFrame = true;
                }

                if (!clientFrame.fullFrame && _guaranteedInputHistorySystems > 0 &&
                    baselineTick > 0 && localTick > baselineTick &&
                    localTick - baselineTick > MaxInputWindow)
                {
                    clientFrame.fullFrame = true;
                }

                var timeline = PreparePlayerVisibility(player, localTick, baselineTick);
                clientFrame.preparedVisibilityTick = localTick;
                _clientFrames[j] = clientFrame;

                clientFrame.packer.ResetPositionAndMode(false);
                var frame = clientFrame.packer;
                var fullFrame = clientFrame.fullFrame;

                if (fullFrame)
                {
                    using var _ = WriteFullFrameMarker.Auto();

                    Packer<PackedInt>.Write(frame, packedTickRate);
                    Packer<float>.Write(frame, tickDelta);
                    Packer<uint>.Write(frame, _sessionSeed);

                    WritePendingVisibilityDeleteSection(player, frame, localTick);
                    WriteAddressedHierarchy(
                        player,
                        timeline,
                        frame,
                        localTick,
                        baselineTick,
                        true);
                    WriteAddressedStateSection(
                        player,
                        timeline,
                        frame,
                        localTick,
                        baselineTick,
                        true,
                        false);
                    WriteAddressedFirstInputSection(timeline, frame, localTick);
                }
                else
                {
                    long sectionStart = frame.positionInBits;
                    WritePendingVisibilityDeleteSection(player, frame, localTick);
                    deltaSectionDeleteBitsTotal += (ulong)(frame.positionInBits - sectionStart);

                    sectionStart = frame.positionInBits;
                    WriteAddressedHierarchy(
                        player,
                        timeline,
                        frame,
                        localTick,
                        baselineTick,
                        false);
                    deltaSectionHierarchyBitsTotal += (ulong)(frame.positionInBits - sectionStart);

                    sectionStart = frame.positionInBits;
                    using (WriteInputHistoryMarker.Auto())
                        WriteVisibilityInputHistory(player, frame, baselineTick, timeline);
                    deltaSectionInputBitsTotal += (ulong)(frame.positionInBits - sectionStart);

                    sectionStart = frame.positionInBits;
                    using (WriteStateDeltasMarker.Auto())
                    {
                        WriteAddressedStateSection(
                            player,
                            timeline,
                            frame,
                            localTick,
                            baselineTick,
                            false,
                            false);
                    }
                    deltaSectionStateBitsTotal += (ulong)(frame.positionInBits - sectionStart);
                    deltaFramesWrittenTotal++;
                }
            }

            lastMaxAckLagTicks = maxAckLag;
        }

        /// <summary>
        /// Largest gap, in ticks, between the current server tick and any connected client's
        /// last acked frame at the time the previous server frame was written. Diagnostic only.
        /// </summary>
        public ulong lastMaxAckLagTicks { get; private set; }

        /// <summary>
        /// Count of per-client frames sent over the reliable recovery path since the session
        /// started. Diagnostic only.
        /// </summary>
        public ulong reliableFramesSentTotal { get; private set; }

        /// <summary>
        /// Count of per-client full (non-delta) frames sent since the session started.
        /// Diagnostic only.
        /// </summary>
        public ulong fullFramesSentTotal { get; private set; }

        /// <summary>
        /// Reliable-frame suppression latch accounting: ticks spent suppressed, completed latch
        /// cycles, cumulative and worst latch duration in ticks. Diagnostic only.
        /// </summary>
        public ulong suppressedTicksTotal { get; private set; }
        public ulong latchCyclesTotal { get; private set; }
        public ulong latchTicksTotal { get; private set; }
        public ulong maxLatchTicks { get; private set; }

        /// <summary>
        /// Client-side input upload accounting: payload sends (including timer-driven resends),
        /// cumulative payload bytes, and cumulative window ticks per send. Diagnostic only.
        /// </summary>
        public ulong inputSendsTotal { get; private set; }
        public ulong inputBytesSentTotal { get; private set; }
        public ulong inputTicksSentTotal { get; private set; }

        /// <summary>
        /// Cumulative bits written into each section of per-client delta frames, plus delta and
        /// full frame byte totals and the largest single delta frame. Diagnostic only.
        /// </summary>
        public ulong deltaSectionDeleteBitsTotal { get; private set; }
        public ulong deltaSectionHierarchyBitsTotal { get; private set; }
        public ulong deltaSectionInputBitsTotal { get; private set; }
        public ulong deltaSectionStateBitsTotal { get; private set; }
        public ulong deltaFramesWrittenTotal { get; private set; }
        public ulong deltaFrameBytesTotal { get; private set; }
        public int maxDeltaFrameBytes { get; private set; }
        public ulong fullFrameBytesTotal { get; private set; }

        internal struct CachedInputEntry
        {
            public PredictedIdentity system;
            public int bitOrigin;
            public int bitLength;
        }

        private struct CachedInputBlock
        {
            public ulong tick;
            public uint version;
            public BitPacker packer;
            public List<CachedInputEntry> entries;
            public Dictionary<PredictedIdentity, int> entryIndex;
            public int guaranteedEntryCount;
            public BitPacker guaranteedFramedPacker;
        }

        private CachedInputBlock[] _inputBlockCache;
        private uint _inputBlockVersion = 1;

        private void InvalidateInputBlockCache()
        {
            _inputBlockVersion++;
        }

        private void DisposeInputBlockCache()
        {
            if (_inputBlockCache == null)
                return;

            for (var i = 0; i < _inputBlockCache.Length; i++)
            {
                _inputBlockCache[i].packer?.Dispose();
                _inputBlockCache[i].guaranteedFramedPacker?.Dispose();
                _inputBlockCache[i] = default;
            }

            _inputBlockCache = null;
        }

        // Serializes every input-bearing system's first-input payload for a given tick exactly
        // once, regardless of how many players' frames end up including it. Per-player visibility
        // filtering happens afterward against the cached (system, bitOrigin, bitLength) index, by
        // copying bit ranges out of the shared blob rather than re-invoking WriteFirstInput -
        // restores the O(window x systems)-once-per-tick cost this used to have before per-player
        // visibility filtering was added (see WriteVisibilityInputHistory).
        private CachedInputBlock GetInputBlockForTick(ulong tick)
        {
            _inputBlockCache ??= new CachedInputBlock[(int)MaxInputWindow + 1];

            var index = (int)(tick % (ulong)_inputBlockCache.Length);
            ref var slot = ref _inputBlockCache[index];

            if (slot.packer != null && slot.tick == tick && slot.version == _inputBlockVersion)
                return slot;

            slot.packer ??= BitPackerPool.Get();
            slot.entries ??= new List<CachedInputEntry>();
            slot.entryIndex ??= new Dictionary<PredictedIdentity, int>();
            slot.tick = tick;
            slot.version = _inputBlockVersion;
            slot.entries.Clear();
            slot.entryIndex.Clear();

            var block = slot.packer;
            block.ResetPositionAndMode(false);

            for (var i = 0; i < _systemsCount; i++)
            {
                var sys = _systems[i];
                if (!sys.hasInput || !sys.HasInputAt(tick))
                    continue;

                int origin = block.positionInBits;
                sys.WriteFirstInput(tick, block);
                slot.entryIndex[sys] = slot.entries.Count;
                slot.entries.Add(new CachedInputEntry
                {
                    system = sys,
                    bitOrigin = origin,
                    bitLength = block.positionInBits - origin
                });
            }

            slot.guaranteedFramedPacker ??= BitPackerPool.Get();
            var guaranteed = slot.guaranteedFramedPacker;
            guaranteed.ResetPositionAndMode(false);
            uint guaranteedCount = 0;
            for (var i = 0; i < slot.entries.Count; i++)
            {
                if (slot.entries[i].system.requiresGuaranteedInputHistory)
                    guaranteedCount++;
            }

            slot.guaranteedEntryCount = (int)guaranteedCount;
            Packer<PackedUInt>.Write(guaranteed, guaranteedCount);
            for (var i = 0; i < slot.entries.Count; i++)
            {
                var entry = slot.entries[i];
                if (!entry.system.requiresGuaranteedInputHistory)
                    continue;
                Packer<PredictedComponentID>.Write(guaranteed, entry.system.id);
                Packer<PackedUInt>.Write(guaranteed, (uint)entry.bitLength);
                guaranteed.WriteBitDataWithoutConsumingIt(
                    new BitData(block, entry.bitOrigin, entry.bitLength));
            }

            return slot;
        }

        private bool TryPeekCachedInputBlock(ulong tick, out CachedInputBlock block)
        {
            block = default;
            if (_inputBlockCache == null)
                return false;

            var index = (int)(tick % (ulong)_inputBlockCache.Length);
            ref var slot = ref _inputBlockCache[index];
            if (slot.packer == null || slot.tick != tick || slot.version != _inputBlockVersion)
                return false;

            block = slot;
            return true;
        }

        private struct InputHistorySpan
        {
            public PredictedComponentID id;
            public int bitOrigin;
            public int bitLength;
        }

        private readonly List<InputHistorySpan> _uploadPrevSpans = new();
        private readonly List<InputHistorySpan> _uploadCurSpans = new();
        private readonly List<InputHistorySpan> _recvPrevSpans = new();
        private readonly List<InputHistorySpan> _recvCurSpans = new();

        private struct NewestInputSlot
        {
            public ulong tick;
            public BitPacker bits;
            public List<InputHistorySpan> entries;
        }

        private NewestInputSlot[] _newestInputRing;

        private void DisposeNewestInputRing()
        {
            if (_newestInputRing == null)
                return;

            for (var i = 0; i < _newestInputRing.Length; i++)
            {
                _newestInputRing[i].bits?.Dispose();
                _newestInputRing[i] = default;
            }

            _newestInputRing = null;
        }

        private ref NewestInputSlot GetNewestInputSlot(ulong tick)
        {
            _newestInputRing ??= new NewestInputSlot[(int)MaxInputWindow + 1];
            ref var slot = ref _newestInputRing[(int)(tick % (ulong)_newestInputRing.Length)];
            slot.bits ??= BitPackerPool.Get();
            slot.entries ??= new List<InputHistorySpan>();
            slot.tick = tick;
            slot.entries.Clear();
            slot.bits.ResetPositionAndMode(false);
            return ref slot;
        }

        private void StoreNewestInputEntry(
            ref NewestInputSlot slot,
            PredictedComponentID pid,
            BitPacker source,
            int sourceOrigin,
            int payloadLength)
        {
            int origin = slot.bits.positionInBits;
            slot.bits.WriteBitDataWithoutConsumingIt(
                new BitData(source, sourceOrigin, payloadLength));
            slot.entries.Add(new InputHistorySpan
            {
                id = pid,
                bitOrigin = origin,
                bitLength = payloadLength
            });
        }

        private void ResetNewestInputSlot(ulong tick)
        {
            GetNewestInputSlot(tick);
        }

        private void ForceFullFramesForAllClients()
        {
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var clientFrame = _clientFrames[i];
                clientFrame.fullFrame = true;
                _clientFrames[i] = clientFrame;
            }
        }

        private void AppendNewestInputEntry(
            ulong tick,
            PredictedComponentID pid,
            BitPacker source,
            int sourceOrigin,
            int payloadLength)
        {
            ref var slot = ref _newestInputRing[(int)(tick % (ulong)_newestInputRing.Length)];
            if (slot.bits == null || slot.tick != tick)
                return;

            StoreNewestInputEntry(ref slot, pid, source, sourceOrigin, payloadLength);
        }

        private void ReadInputHistory(BitPacker frame, ulong serverTick, ulong baselineTick)
        {
            using var _ = ReadInputHistoryMarker.Auto();

            PackedUInt tickCount = default;
            Packer<PackedUInt>.Read(frame, ref tickCount);

            ulong from = serverTick - tickCount.value;
            using var entryPayload = BitPackerPool.Get();

            for (uint k = 0; k < tickCount.value; k++)
            {
                ulong t = from + 1 + k;

                PackedUInt entryCount = default;
                Packer<PackedUInt>.Read(frame, ref entryCount);

                for (uint e = 0; e < entryCount.value; e++)
                {
                    PredictedComponentID pid = default;
                    Packer<PredictedComponentID>.Read(frame, ref pid);
                    PackedUInt bits = default;
                    Packer<PackedUInt>.Read(frame, ref bits);
                    int payloadLength = checked((int)bits.value);
                    int origin = frame.positionInBits;
                    frame.SkipBits(payloadLength);

                    if (_instanceMap.TryGetValue(pid, out var system))
                    {
                        entryPayload.ResetPositionAndMode(false);
                        entryPayload.WriteBitDataWithoutConsumingIt(
                            new BitData(frame, origin, payloadLength));
                        entryPayload.ResetPositionAndMode(true);
                        system.ReadFirstInput(t, entryPayload);

                        if (entryPayload.positionInBits > payloadLength)
                        {
                            throw new InvalidOperationException(
                                $"Input history record {pid} consumed " +
                                $"{entryPayload.positionInBits} bits, past its " +
                                $"declared {payloadLength}-bit payload.");
                        }
                    }
                }
            }

            PackedUInt newestCount = default;
            Packer<PackedUInt>.Read(frame, ref newestCount);

            ref var slot = ref GetNewestInputSlot(serverTick);
            int refIndex = (int)(baselineTick % (ulong)_newestInputRing.Length);

            for (uint e = 0; e < newestCount.value; e++)
            {
                PredictedComponentID pid = default;
                Packer<PredictedComponentID>.Read(frame, ref pid);
                bool repeat = Packer<bool>.Read(frame);

                int origin;
                int payloadLength;

                if (repeat)
                {
                    int refEntryIdx = -1;
                    ref var refSlot = ref _newestInputRing[refIndex];
                    if (refSlot.bits != null && refSlot.tick == baselineTick)
                    {
                        for (var i = 0; i < refSlot.entries.Count; i++)
                        {
                            if (refSlot.entries[i].id.Equals(pid))
                            {
                                refEntryIdx = i;
                                break;
                            }
                        }
                    }

                    if (refEntryIdx < 0)
                    {
                        throw new InvalidOperationException(
                            $"Newest input record {pid} repeats a payload that is absent " +
                            $"from the baseline block at tick {baselineTick}.");
                    }

                    var refEntry = refSlot.entries[refEntryIdx];
                    origin = slot.bits.positionInBits;
                    payloadLength = refEntry.bitLength;
                    slot.bits.WriteBitDataWithoutConsumingIt(
                        new BitData(refSlot.bits, refEntry.bitOrigin, refEntry.bitLength));
                    slot.entries.Add(new InputHistorySpan
                    {
                        id = pid,
                        bitOrigin = origin,
                        bitLength = payloadLength
                    });
                }
                else
                {
                    PackedUInt bits = default;
                    Packer<PackedUInt>.Read(frame, ref bits);
                    payloadLength = checked((int)bits.value);
                    int frameOrigin = frame.positionInBits;
                    frame.SkipBits(payloadLength);

                    origin = slot.bits.positionInBits;
                    StoreNewestInputEntry(ref slot, pid, frame, frameOrigin, payloadLength);
                }

                if (_instanceMap.TryGetValue(pid, out var system))
                {
                    entryPayload.ResetPositionAndMode(false);
                    entryPayload.WriteBitDataWithoutConsumingIt(
                        new BitData(slot.bits, origin, payloadLength));
                    entryPayload.ResetPositionAndMode(true);
                    system.ReadFirstInput(serverTick, entryPayload);

                    if (entryPayload.positionInBits > payloadLength)
                    {
                        throw new InvalidOperationException(
                            $"Newest input record {pid} consumed " +
                            $"{entryPayload.positionInBits} bits, past its " +
                            $"declared {payloadLength}-bit payload.");
                    }
                }
            }
        }

        private void RollbackAllToVerified(ulong tick)
        {
            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system.UsesSoftCorrectionTimeline())
                    system.RunRollback(tick);
            }
            SyncTransforms();
            PredictionPerformanceTelemetry.StateRestored(this);
        }

        private void WriteEventHandles()
        {
            using var _ = WriteEventHandlesMarker.Auto();

            var fCount = _clientFrames.Count;
            for (var j = 0; j < fCount; j++)
            {
                var frame = _clientFrames[j];
                if (frame.preparedFrameTick != localTick ||
                    !_playerVisibility.TryGetValue(frame.player, out var timeline))
                {
                    continue;
                }

                ulong baselineTick = 0;
                if (_clientTicks.TryGetValue(frame.player, out var ackQueue))
                    baselineTick = ackQueue.ackedServerTick;

                WriteAddressedStateSection(
                    frame.player,
                    timeline,
                    frame.packer,
                    localTick,
                    baselineTick,
                    frame.fullFrame,
                    true);
            }
        }

        private void SendFrameToOthers()
        {
            using var _ = SendFrameMarker.Auto();

            var fCount = _clientFrames.Count;

            for (var j = 0; j < fCount; j++)
            {
                var clientFrame = _clientFrames[j];
                if (clientFrame.preparedFrameTick != localTick)
                    continue;

                var player = clientFrame.player;
                var packer = clientFrame.packer;
                var deltaLen = packer.ToByteData().length;
                var fullFrame = clientFrame.fullFrame;
                var requiresReliableRecovery = RequiresReliableRecovery(
                    fullFrame,
                    deltaLen,
                    clientFrame.maxUnreliableFrameBytes) || clientFrame.baselineAdvance.distressed;

                ulong inputAck = 0;
                ulong baselineTick = 0;
                bool hasInputMargin = false;
                PackedInt inputMargin = 0;
                bool hasInputSlack = false;
                PackedInt inputSlackMs = 0;

                if (_clientTicks.TryGetValue(player, out var queue))
                {
                    ulong contiguous = queue.lastConsumedTick;
                    while (queue.byTick.ContainsKey(contiguous + 1))
                        contiguous++;
                    inputAck = contiguous;
                    baselineTick = queue.ackedServerTick;

                    if (queue.rawHighestReceivedTick > 0)
                    {
                        long margin = (long)queue.rawHighestReceivedTick - (long)localTick;
                        if (margin > InputMarginClamp) margin = InputMarginClamp;
                        else if (margin < -InputMarginClamp) margin = -InputMarginClamp;
                        hasInputMargin = true;
                        inputMargin = (int)margin;
                    }

                    if (queue.hasPendingInputSlack)
                    {
                        double slack = queue.pendingInputSlackMs;
                        if (slack > InputSlackClampMs) slack = InputSlackClampMs;
                        else if (slack < -InputSlackClampMs) slack = -InputSlackClampMs;
                        hasInputSlack = true;
                        inputSlackMs = (int)Math.Round(slack);
                        queue.hasPendingInputSlack = false;
                    }
                }

                if (requiresReliableRecovery)
                {
                    SendFrameToRemoteReliable(player, localTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, hasInputSlack, inputSlackMs, new BitPackerWithLength(deltaLen, packer));
                    if (deltaLen <= clientFrame.maxUnreliableFrameBytes)
                        SendFrameToRemote(player, localTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, hasInputSlack, inputSlackMs, new BitPackerWithLength(deltaLen, packer));
                }
                else SendFrameToRemote(player, localTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, hasInputSlack, inputSlackMs, new BitPackerWithLength(deltaLen, packer));

                if (requiresReliableRecovery)
                    reliableFramesSentTotal++;
                if (fullFrame)
                {
                    fullFramesSentTotal++;
                    fullFrameBytesTotal += (ulong)deltaLen;
                }
                else
                {
                    deltaFrameBytesTotal += (ulong)deltaLen;
                    if (deltaLen > maxDeltaFrameBytes)
                        maxDeltaFrameBytes = deltaLen;
                }

                clientFrame.sentVisibilityTick = localTick;
                clientFrame.preparedVisibilityTick = 0;
                if (_playerVisibility.TryGetValue(player, out var visibilityTimeline))
                    HandleVisibilityFrameSent(player, visibilityTimeline, localTick);
                MarkPendingVisibilityDeletesSent(player, localTick);

                if (requiresReliableRecovery)
                {
                    clientFrame.reliableFrame.MarkSent(localTick);
                    clientFrame.reliableSentAtLocalTick = localTick;
                    clientFrame.reliableSentBaselineTick = baselineTick;
                    clientFrame.reliableSentInputAck = inputAck;
                    clientFrame.reliableSentFullFrame = fullFrame;
                }

                clientFrame.preparedFrameTick = 0;
                if (fullFrame)
                    clientFrame.fullFrame = false;

                _clientFrames[j] = clientFrame;
            }
        }

        internal static int GetMaxUnreliableFrameBytes(int mtu)
        {
            const int maxGeneratedRpcArgumentBytes = 30;
            const int maxCompressedFrameExpansionBytes = 1;
            const int maxEntryLengthPrefixBytes = 6;

            var maxFragmentedMessageBytes = FragmentationLayer.GetMaxMessageSize(
                mtu,
                BroadcastModule.MAX_HEADER_SIZE);

            return maxFragmentedMessageBytes -
                   (maxGeneratedRpcArgumentBytes +
                    maxCompressedFrameExpansionBytes +
                    BroadcastModule.MAX_HEADER_SIZE +
                    RPCBatch.MAX_HEADER_SIZE +
                    maxEntryLengthPrefixBytes);
        }

        internal static bool RequiresReliableRecovery(
            bool fullFrame,
            int frameBytes,
            int maxUnreliableFrameBytes)
        {
            return fullFrame || frameBytes > maxUnreliableFrameBytes;
        }

        internal static ulong ReliableRecoveryWindowTicks(int tickRate)
        {
            var half = (ulong)Math.Max(1, tickRate / 2);
            return half > MaxInputWindow ? half : MaxInputWindow;
        }

        internal const ulong RecoveryHedgeIntervalTicks = 4;

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
        /// True while the simulation is re-running an already-verified tick purely to rebuild
        /// state (client catch-up before a jumped or in-place frame, server full-state rebuild
        /// when a new observer joins). Deterministic simulation code — including physics event
        /// handlers that mutate predicted state — still runs and must not be skipped.
        /// Gate one-shot side effects (VFX, SFX, scoring, notifications) on this flag to avoid
        /// reacting twice to the same tick.
        /// </summary>
        public bool isCatchingUpFrames { get; private set; }

        /// <summary>
        /// True when one-shot, user-facing reactions (VFX, SFX, scoring, UI) should run for
        /// the tick being simulated: the tick is verified and this is its first delivery,
        /// not a state-rebuilding catch-up pass. Prefer this over checking
        /// <see cref="isVerified"/> directly for visual/audio feedback.
        /// </summary>
        public bool isVerifiedView => isVerified && !isCatchingUpFrames;


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

        /// <summary>
        /// Invoked immediately before PurrDiction simulates its configured physics scenes.
        /// This is also fired during resimulation after a rollback, before each replayed physics pass.
        /// This occurs after <see>
        ///     <cref>PredictedIdentity.Simulate()</cref>
        /// </see>
        /// and before
        /// <see>
        ///     <cref>PredictedIdentity.LateSimulate()</cref>
        /// </see>
        /// .
        /// </summary>
        public event Action onBeforePhysicsPass;

        /// <summary>
        /// Invoked immediately after PurrDiction simulates its configured physics scenes.
        /// This is also fired during resimulation after a rollback, after each replayed physics pass.
        /// This occurs after <see>
        ///     <cref>PredictedIdentity.Simulate()</cref>
        /// </see>
        /// and before
        /// <see>
        ///     <cref>PredictedIdentity.LateSimulate()</cref>
        /// </see>
        /// .
        /// </summary>
        public event Action onAfterPhysicsPass;

        private void DoPhysicsPass(PredictionPerformanceTelemetry.PassScope performance)
        {
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            try
            {
                onBeforePhysicsPass?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            isInPhysicsPass = true;
            long physicsStarted = performance.Timestamp();
            try
            {
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
            }
            finally
            {
                performance.PhysicsDone(physicsStarted);
                isInPhysicsPass = false;

                try
                {
                    onAfterPhysicsPass?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        struct FrameDelta : IDisposable
        {
            public BitPacker packer;
            public ulong serverTick;
            public ulong baselineTick;
            public ulong inputAck;
            public bool fullFrame;
            public int enqueuedFrame;
            public bool trackAge;

            public void Dispose()
            {
                packer?.Dispose();
            }
        }

        readonly Queue<FrameDelta> _deltas = new ();

        [TargetRpc(channel: Channel.Unreliable, compressionLevel: CompressionLevel.Fast, mtuExceeded: MTUBehaviour.Fragment, immediate: true)]
        private void SendFrameToRemote([UsedImplicitly] PlayerID player, ulong serverTick, ulong baselineTick, ulong inputAck, bool fullFrame, bool hasInputMargin, PackedInt inputMargin, bool hasInputSlack, PackedInt inputSlackMs, BitPackerWithLength delta)
        {
            HandleFrameFromServer(serverTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, hasInputSlack, inputSlackMs, delta);
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SendFrameToRemoteReliable([UsedImplicitly] PlayerID player, ulong serverTick, ulong baselineTick, ulong inputAck, bool fullFrame, bool hasInputMargin, PackedInt inputMargin, bool hasInputSlack, PackedInt inputSlackMs, BitPackerWithLength delta)
        {
            HandleFrameFromServer(serverTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, hasInputSlack, inputSlackMs, delta);
        }

        /// <summary>
        /// Client-side counts of frames received from the server, split by kind. Diagnostic only.
        /// </summary>
        public ulong framesReceivedTotal { get; private set; }
        public ulong fullFramesReceivedTotal { get; private set; }

        private void HandleFrameFromServer(ulong serverTick, ulong baselineTick, ulong inputAck, bool fullFrame, bool hasInputMargin, PackedInt inputMargin, bool hasInputSlack, PackedInt inputSlackMs, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);

            framesReceivedTotal++;
            if (fullFrame)
                fullFramesReceivedTotal++;

            if (inputAck > _inputAckTick)
                _inputAckTick = inputAck;

            if (hasInputMargin && serverTick >= _frameInputMarginTick)
            {
                _frameInputMargin = inputMargin;
                _frameInputMarginTick = serverTick;
                _hasFrameInputMargin = true;
            }

            if (hasInputSlack && serverTick >= _frameInputSlackServerTick)
            {
                _frameInputSlackMs = inputSlackMs;
                _frameInputSlackServerTick = serverTick;
                _frameInputSlackInputTick = (long)serverTick + (hasInputMargin ? (long)(int)inputMargin : 0);
                _hasFrameInputSlack = true;
                _serverSendsInputSlack = true;
                lastInputSlackMs = inputSlackMs;
            }

            if (fullFrame)
            {
                int queued = _deltas.Count;
                for (int i = 0; i < queued; i++)
                {
                    var pending = _deltas.Dequeue();
                    if (pending.serverTick > serverTick)
                        _deltas.Enqueue(pending);
                    else pending.Dispose();
                }
            }

            _deltas.Enqueue(new FrameDelta
            {
                packer = delta.packer,
                serverTick = serverTick,
                baselineTick = baselineTick,
                inputAck = inputAck,
                fullFrame = fullFrame,
                enqueuedFrame = Time.frameCount,
                trackAge = localTick > 1
            });
        }

        private void RollbackToFrame(BitPacker frame, ulong stateTick, ulong baselineTick, ulong serverTick)
        {
            using var _ = RollbackToFrameMarker.Auto();

            frame.ResetPositionAndMode(true);

            bool crossedGap = _verifiedServerTick > 0 &&
                              serverTick > _verifiedServerTick + 1;

            ReadVisibilityDeleteSection(frame);

            // Across a gap, decode and store the new hierarchy without applying it yet.
            // The old live topology must remain intact while its historical inputs replay.
            ReadAddressedHierarchyRecord(
                frame,
                stateTick,
                baselineTick,
                serverTick,
                false,
                crossedGap);
            int inputHistoryStart = frame.positionInBits;
            ReadInputHistory(frame, serverTick, baselineTick);
            int stateRecordsStart = frame.positionInBits;

            if (crossedGap)
            {
                RollbackAllToVerified(_verifiedServerTick + 1);

                for (ulong tick = _verifiedServerTick + 1; tick < serverTick; tick++)
                    SimulateFrame(tick, HistorySaveMode.Full, PredictionPassKind.GapCatchup);

                // Applying the verified hierarchy now removes leavers only after their gap
                // inputs were consumed, and creates entrants before their addressed state.
                if (hierarchy)
                {
                    ApplyPendingRemoteVisibilityDeletes(serverTick);
                    if (!hierarchy.RestoreVerifiedState(serverTick))
                    {
                        throw new InvalidOperationException(
                            $"Failed to restore staged hierarchy state for tick {serverTick}.");
                    }
                    hierarchy.lastVerifiedTick = stateTick;
                }
                SaveEnteringState(serverTick);

                // Entrants did not exist during the first read. Populate their retained input
                // history now, then continue at the already-parsed state section.
                frame.SetBitPosition(inputHistoryStart);
                ReadInputHistory(frame, serverTick, baselineTick);
                frame.SetBitPosition(stateRecordsStart);
            }

            ReadAddressedStateRecords(
                frame,
                stateTick,
                baselineTick,
                serverTick,
                false,
                false);
            ReadAddressedStateRecords(
                frame,
                stateTick,
                baselineTick,
                serverTick,
                false,
                true);

            SyncTransforms();
            PredictionPerformanceTelemetry.StateRestored(this);
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

        private ulong _verifiedServerTick;
        private ulong _ackedServerTick;
        private ulong _pauseAdvanceTicks;
        private ulong _latestFrameServerTick;
        private bool _inputStarved;

        private const ulong MinLead = 2;
        private const ulong TargetLead = 3;
        private const ulong AbsoluteMaxLead = MaxInputWindow - 2;

        private const long InputMarginClamp = 64;
        private const long InputMarginLow = 1;
        private const float InputMarginTargetSeconds = 0.04f;
        private const long MaxLeadAdjustPerFrame = 16;

        private const long InputSlackClampMs = 1000;
        internal const double SlackTargetBaseMs = 12;
        internal const double SlackTargetMaxMs = 150;
        internal const double SlackDeadbandMs = 4;
        internal const double SlackJitterHeadroom = 1.5;
        internal const double SlackScalePerMs = 0.0005;
        internal const double MaxTickPacingOffset = 0.02;
        internal const double SlackDroopMarginMs = 4;
        internal const double SlackFloorDecayMsPerSample = 0.25;
        private const double SlackEmaAlpha = 0.15;
        private const double SlackDeviationAlpha = 0.1;
        internal const int LowMarginJumpStreak = 3;

        internal static long InputMarginTargetTicks(int tickRate) =>
            Math.Max(2, (long)Math.Ceiling(InputMarginTargetSeconds * tickRate));

        private long inputMarginTarget => InputMarginTargetTicks(tickRate);

        private long inputMarginHigh => inputMarginTarget * 2;

        private long _frameInputMargin;
        private ulong _frameInputMarginTick;
        private bool _hasFrameInputMargin;
        private ulong _leadAdjustGateTick;

        private long _frameInputSlackMs;
        private ulong _frameInputSlackServerTick;
        private long _frameInputSlackInputTick;
        private bool _hasFrameInputSlack;
        private bool _serverSendsInputSlack;
        private int _lowMarginStreak;
        private double _slackEmaMs;
        private double _slackDevEmaMs;
        private double _slackFloorEstimateMs;
        private bool _hasSlackEma;
        private double _serverTickBoundaryTime;

        /// <summary>
        /// Latest server-echoed input slack sample, in milliseconds: how long before its
        /// consumption deadline the newest input tick arrived at the server. Positive means
        /// early, negative means late. Diagnostic only.
        /// </summary>
        public double lastInputSlackMs { get; private set; }

        /// <summary>
        /// Smoothed input slack estimate driving the tick pacing controller, in milliseconds.
        /// Diagnostic only.
        /// </summary>
        public double smoothedInputSlackMs => _hasSlackEma ? _slackEmaMs : 0d;

        /// <summary>
        /// Current slack the pacing controller aims for, in milliseconds. Grows with observed
        /// arrival jitter. Diagnostic only.
        /// </summary>
        public double currentSlackTargetMs => _hasSlackEma
            ? ComputeSlackTargetMs(_slackDevEmaMs, Math.Max(0d, _slackEmaMs - _slackFloorEstimateMs))
            : ComputeSlackTargetMs(0d);

        /// <summary>
        /// Tick pacing scale last applied to the local tick clock. 1 means neutral pacing.
        /// Diagnostic only.
        /// </summary>
        public double currentTickPacingScale { get; private set; } = 1d;

        /// <summary>
        /// True once the server has echoed at least one input slack sample this session.
        /// Diagnostic only.
        /// </summary>
        public bool hasInputSlackFeedback => _serverSendsInputSlack;

        /// <summary>
        /// Count of forward prediction-head jumps triggered by the input margin feedback loop
        /// since the session started. Diagnostic only.
        /// </summary>
        public ulong leadJumpsTotal { get; private set; }

        /// <summary>
        /// Count of tick pauses scheduled by the input margin feedback loop or the absolute
        /// lead clamp since the session started. Diagnostic only.
        /// </summary>
        public ulong leadPausesTotal { get; private set; }

        /// <summary>
        /// Count of minimum-lead snaps of the prediction head since the session started.
        /// Diagnostic only.
        /// </summary>
        public ulong minLeadSnapsTotal { get; private set; }

        /// <summary>
        /// Count of input starvation rescue jumps since the session started. Diagnostic only.
        /// </summary>
        public ulong starvationJumpsTotal { get; private set; }

        internal static double ComputeSlackTargetMs(double deviationMs, double droopMs = 0d)
        {
            double jitterHeadroom = SlackJitterHeadroom * Math.Max(0d, deviationMs);
            double droopHeadroom = droopMs > 0d ? droopMs + SlackDroopMarginMs : 0d;
            double target = SlackTargetBaseMs + Math.Max(jitterHeadroom, droopHeadroom);
            return Math.Min(target, SlackTargetMaxMs);
        }

        internal static double ComputeTickPacingScale(double emaSlackMs, double targetSlackMs)
        {
            double error = emaSlackMs - targetSlackMs;
            double magnitude = Math.Abs(error) - SlackDeadbandMs;
            if (magnitude <= 0d)
                return 1d;

            double offset = Math.Min(magnitude * SlackScalePerMs, MaxTickPacingOffset);
            return error > 0d ? 1d + offset : 1d - offset;
        }

        internal static double ClampPacingScaleForLead(double scale, ulong lead, ulong minLead)
        {
            return scale > 1d && lead <= minLead ? 1d : scale;
        }

        internal static bool ShouldJumpForLowMargin(long margin, long marginTarget, bool slackFeedback, int lowMarginStreak)
        {
            if (!slackFeedback)
                return margin < InputMarginLow;

            if (margin >= 0)
                return false;

            return lowMarginStreak >= LowMarginJumpStreak || margin <= -marginTarget;
        }

        private void AdjustLeadFromInputMargin()
        {
            if (_hasFrameInputMargin)
            {
                long sampleInputTick = (long)_frameInputMarginTick + _frameInputMargin;
                if (sampleInputTick > (long)_leadAdjustGateTick)
                {
                    if (_serverSendsInputSlack)
                    {
                        if (_frameInputMargin < 0)
                            _lowMarginStreak++;
                        else
                            _lowMarginStreak = 0;
                    }

                    if (ShouldJumpForLowMargin(_frameInputMargin, inputMarginTarget, _serverSendsInputSlack, _lowMarginStreak))
                    {
                        long deficit = inputMarginTarget - _frameInputMargin;
                        if (deficit > MaxLeadAdjustPerFrame)
                            deficit = MaxLeadAdjustPerFrame;

                        localTick += (ulong)deficit;
                        localTickInContext = localTick;
                        _pauseAdvanceTicks = 0;
                        _leadAdjustGateTick = localTick;
                        _hasFrameInputMargin = false;
                        leadJumpsTotal++;
                        ResetSlackController();
                    }
                    else if (_frameInputMargin > inputMarginHigh)
                    {
                        long excess = _frameInputMargin - inputMarginTarget;
                        if (excess > MaxLeadAdjustPerFrame)
                            excess = MaxLeadAdjustPerFrame;

                        _pauseAdvanceTicks = (ulong)excess;
                        _leadAdjustGateTick = localTick;
                        _hasFrameInputMargin = false;
                        leadPausesTotal++;
                        ResetSlackController();
                    }
                }
            }

            UpdateTickPacingFromSlack();
        }

        private void UpdateTickPacingFromSlack()
        {
            if (!_hasFrameInputSlack)
                return;

            _hasFrameInputSlack = false;

            if (_frameInputSlackInputTick <= (long)_leadAdjustGateTick)
                return;

            double sample = _frameInputSlackMs;

            if (!_hasSlackEma)
            {
                _slackEmaMs = sample;
                _slackDevEmaMs = 0d;
                _slackFloorEstimateMs = sample;
                _hasSlackEma = true;
            }
            else
            {
                _slackEmaMs += SlackEmaAlpha * (sample - _slackEmaMs);
                _slackDevEmaMs += SlackDeviationAlpha * (Math.Abs(sample - _slackEmaMs) - _slackDevEmaMs);
                _slackFloorEstimateMs = Math.Min(sample, _slackFloorEstimateMs + SlackFloorDecayMsPerSample);
            }

            double droop = Math.Max(0d, _slackEmaMs - _slackFloorEstimateMs);
            double scale = ComputeTickPacingScale(_slackEmaMs, ComputeSlackTargetMs(_slackDevEmaMs, droop));
            ulong lead = localTick > _verifiedServerTick ? localTick - _verifiedServerTick : 0;
            SetTickPacingScale(ClampPacingScaleForLead(scale, lead, MinLead));
        }

        private void ResetSlackController()
        {
            _hasSlackEma = false;
            _slackEmaMs = 0d;
            _slackDevEmaMs = 0d;
            _slackFloorEstimateMs = 0d;
            _hasFrameInputSlack = false;
            _lowMarginStreak = 0;
            SetTickPacingScale(1d);
        }

        private void SetTickPacingScale(double scale)
        {
            currentTickPacingScale = scale;
            if (_tickManager != null && isClient && !cachedIsServer)
                _tickManager.tickPacingScale = scale;
        }

        private void SaveEnteringState(ulong tick)
        {
            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (!system.isEventHandler)
                        system.RunSaveState(tick);
                }
            }
        }

        private void OnPostTick()
        {
            // Tick catch-up polls the transport between ticks. Replaying here would rebuild
            // the speculative future for each newly received batch within the same frame.
            // Active clients instead drain once in Update, after the network's tick loop.
            // Disabled managers remain subscribed to ticks but do not receive Unity Update.
            if (!cachedIsServer && _deltas.Count > 0 && !isActiveAndEnabled)
            {
                ProcessQueuedFrames(false);
                return;
            }

            // Capture the prediction before correction so the render drain can refresh the
            // pending view sample without interpreting ordinary movement as rollback error.
            if (isClient)
                UpdateInterpolation(false);
            TickBandwidthProfiler.MarkEndOfTick();
        }

        internal static bool ShouldApplyQueuedFramesInRenderPhase(
            ulong localTick,
            int queuedFrames,
            bool isSimulating,
            bool isReplaying)
        {
            return queuedFrames > 0 && localTick > 1 && !isSimulating && !isReplaying;
        }

        internal static bool ShouldReplaceViewLatch(bool refreshOnly, bool hasPendingLatch)
        {
            return !refreshOnly || hasPendingLatch;
        }

        internal const float ViewInterpolationMaxBufferSeconds = 0.1f;
        internal const int ViewInterpolationMaxBufferFloor = 3;

        internal static int GetViewInterpolationMaxBufferSize(int tickRate)
        {
            return Math.Max(ViewInterpolationMaxBufferFloor,
                (int)(tickRate * ViewInterpolationMaxBufferSeconds));
        }

        internal bool refreshViewLatchOnly { get; private set; }

        /// <summary>
        /// Count of view interpolation buffer overflow trims across all identities and modules
        /// since the session started. Each trim discards buffered view samples and snaps the
        /// view forward. Diagnostic only.
        /// </summary>
        public ulong viewBufferTrimsTotal { get; private set; }

        /// <summary>
        /// Count of view updates that advanced with an empty interpolation buffer across all
        /// identities and modules since the session started. The view holds its last sample on
        /// those frames. Diagnostic only.
        /// </summary>
        public ulong viewBufferStarvedFramesTotal { get; private set; }

        internal void ReportViewBufferTrim() => viewBufferTrimsTotal++;

        internal void ReportViewBufferStarved() => viewBufferStarvedFramesTotal++;

        /// <summary>
        /// Count of server-frame batches applied from the render-frame path since the session
        /// started. Diagnostic only.
        /// </summary>
        public ulong renderPhaseFrameAppliesTotal { get; private set; }

        /// <summary>
        /// Count of server-frame batches applied from the post-tick fallback for disabled
        /// managers since the session started. Active clients reconcile in Update instead.
        /// </summary>
        public ulong tickPhaseFrameAppliesTotal { get; private set; }

        /// <summary>
        /// Largest number of render frames any received server frame spent queued before being
        /// consumed, ignoring frames received before the first local tick. Diagnostic only.
        /// </summary>
        public int maxFrameApplyAgeFrames { get; private set; }

        private double _lastPerformanceReconcileTime = double.NegativeInfinity;

        internal static bool ShouldDeferReconciliation(
            bool server, ulong verifiedTick, double intervalSeconds, double now, double lastBatchTime)
        {
            return !server && verifiedTick > 0 && intervalSeconds > 0 &&
                   now - lastBatchTime < intervalSeconds;
        }

        private void ProcessQueuedFrames(bool renderPhase)
        {
            double interval = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
            if (interval > 0)
            {
                double now = Time.unscaledTimeAsDouble;
                if (ShouldDeferReconciliation(cachedIsServer, _verifiedServerTick,
                        interval, now, _lastPerformanceReconcileTime))
                {
                    PredictionPerformanceTelemetry.CadenceDeferred(this);
                    if (!renderPhase)
                    {
                        UpdateInterpolation(false);
                        TickBandwidthProfiler.MarkEndOfTick();
                    }
                    return;
                }
                _lastPerformanceReconcileTime = now;
            }

            using var performance = PredictionPerformanceTelemetry.BeginBatch(this, _deltas.Count);
            onStartingToRollback?.Invoke();

            if (!renderPhase)
                UpdateInterpolation(false);

            isSimulating = true;
            isReplaying = true;

            NotifyReplayStart();

            bool applied = false;

            try
            {
                bool firstContact = _latestFrameServerTick == 0;

                while (_deltas.Count > 0)
                {
                    using var frame = _deltas.Dequeue();

                    if (frame.trackAge)
                    {
                        int age = Time.frameCount - frame.enqueuedFrame;
                        if (age > maxFrameApplyAgeFrames)
                            maxFrameApplyAgeFrames = age;
                    }

                    if (frame.serverTick > _latestFrameServerTick)
                    {
                        _latestFrameServerTick = frame.serverTick;
                        _inputStarved = frame.serverTick > frame.inputAck + MaxInputWindow;
                    }

                    if (frame.serverTick <= _verifiedServerTick)
                        continue;

                    isVerified = true;

                    _frameApplyHadRecordFailure = false;

                    if (frame.fullFrame)
                    {
                        ReadFullFrame(frame.packer, frame.serverTick, frame.serverTick, frame.serverTick);
                        SimulateFrame(frame.serverTick, HistorySaveMode.VerifiedFrame);
                        SaveEnteringState(frame.serverTick + 1);
                        _verifiedServerTick = frame.serverTick;
                    }
                    else
                    {
                        RollbackToFrame(frame.packer, frame.serverTick, frame.baselineTick, frame.serverTick);
                        SimulateFrame(frame.serverTick, HistorySaveMode.VerifiedFrame);
                        SaveEnteringState(frame.serverTick + 1);
                        _verifiedServerTick = frame.serverTick;
                    }

                    if (!_frameApplyHadRecordFailure)
                        _ackedServerTick = frame.serverTick;

                    MaybeSendDesyncReport(frame.serverTick);

                    applied = true;
                    isVerified = false;
                }

                if (applied)
                {
                    if (_inputStarved && _latestFrameServerTick + TargetLead + 4 > localTick)
                    {
                        localTick = _latestFrameServerTick + TargetLead + 4;
                        localTickInContext = localTick;
                        _pauseAdvanceTicks = 0;
                        _leadAdjustGateTick = localTick;
                        starvationJumpsTotal++;
                        ResetSlackController();
                        if (!firstContact)
                            PurrLogger.LogWarning($"Input starvation detected; jumping prediction head to {localTick} (server at {_latestFrameServerTick}).");
                    }

                    AdjustLeadFromInputMargin();

                    ulong lead = localTick > _verifiedServerTick ? localTick - _verifiedServerTick : 0;

                    if (lead < MinLead)
                    {
                        localTick = _verifiedServerTick + TargetLead;
                        localTickInContext = localTick;
                        _pauseAdvanceTicks = 0;
                        _leadAdjustGateTick = localTick;
                        minLeadSnapsTotal++;
                        ResetSlackController();
                    }
                    else if (lead > AbsoluteMaxLead)
                    {
                        _pauseAdvanceTicks = lead - AbsoluteMaxLead;
                        leadPausesTotal++;
                    }

                    SimulateFrame(_verifiedServerTick + 1, HistorySaveMode.Full);
                    ReplayToLatestTick(_verifiedServerTick + 2, HistorySaveMode.None);
                }

                if (!renderPhase)
                {
                    SyncTransforms();
                    UpdateInterpolation(true);
                }
                else if (applied)
                {
                    SyncTransforms();
                    refreshViewLatchOnly = true;
                    try
                    {
                        UpdateInterpolation(true);
                    }
                    finally
                    {
                        refreshViewLatchOnly = false;
                    }
                }
            }
            finally
            {
                NotifyReplayEnd();

                isVerified = false;
                isCatchingUpFrames = false;
                isReplaying = false;
                isSimulating = false;
            }

            if (applied)
            {
                if (renderPhase)
                    renderPhaseFrameAppliesTotal++;
                else tickPhaseFrameAppliesTotal++;
            }

            if (!renderPhase)
                TickBandwidthProfiler.MarkEndOfTick();

            onRollbackFinished?.Invoke();
        }

        readonly List<PredictedIdentity> _replayFrozenSystems = new ();

        private struct SpeculativeRelayLock
        {
            public PredictedIdentity system;
            public ulong tick;
        }

        readonly List<SpeculativeRelayLock> _speculativeRelayLocks = new ();

        private void LockSpeculativeRelayStates(ulong tick)
        {
            if (cachedIsServer || isVerified)
                return;

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system.UsesServerRelayTimeline() || !system.SkipsCurrentSimulationPhase())
                    continue;
                if (HasSpeculativeRelayLock(system))
                    continue;

                system.RunSaveStateUnchecked(tick);
                _speculativeRelayLocks.Add(new SpeculativeRelayLock
                {
                    system = system,
                    tick = tick
                });
            }
        }

        private bool HasSpeculativeRelayLock(PredictedIdentity system)
        {
            for (var i = 0; i < _speculativeRelayLocks.Count; i++)
            {
                if (_speculativeRelayLocks[i].system == system)
                    return true;
            }

            return false;
        }

        private void RemoveSpeculativeRelayLock(PredictedIdentity system)
        {
            for (var i = _speculativeRelayLocks.Count - 1; i >= 0; i--)
            {
                if (_speculativeRelayLocks[i].system == system)
                    _speculativeRelayLocks.RemoveAt(i);
            }
        }

        private void RestoreSpeculativeRelayStates()
        {
            if (_speculativeRelayLocks.Count == 0)
                return;

            for (var i = _speculativeRelayLocks.Count - 1; i >= 0; i--)
            {
                var locked = _speculativeRelayLocks[i];
                var system = locked.system;
                if (!system || !system.UsesServerRelayTimeline())
                {
                    _speculativeRelayLocks.RemoveAt(i);
                    continue;
                }

                system.RunRollback(locked.tick);
            }

            _speculativeRelayLocks.Clear();
            SyncTransforms();
            PredictionPerformanceTelemetry.StateRestored(this);
        }

        private void ClearSpeculativeRelayLocks()
        {
            _speculativeRelayLocks.Clear();
        }

        private void NotifyReplayStart()
        {
            ClearSpeculativeRelayLocks();

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.UsesSoftCorrectionTimeline())
                    FreezeForReplay(system);
            }
        }

        private void FreezeForReplay(PredictedIdentity system)
        {
            system.OnReplayStart();
            _replayFrozenSystems.Add(system);
        }

        internal void HandlePredictionPolicyChanged(
            PredictedIdentity system,
            PredictionPolicy oldPolicy,
            PredictionPolicy newPolicy)
        {
            if (!isReplaying || !_systems.Contains(system))
                return;

            bool wasSoftCorrected = oldPolicy == PredictionPolicy.SoftCorrection;
            bool isSoftCorrected = newPolicy == PredictionPolicy.SoftCorrection;
            if (wasSoftCorrected == isSoftCorrected)
                return;

            if (isSoftCorrected)
            {
                for (var i = 0; i < _replayFrozenSystems.Count; i++)
                {
                    if (_replayFrozenSystems[i] == system)
                        return;
                }

                system.SetSoftCorrectionReplaySimulation(false);
                FreezeForReplay(system);
                return;
            }

            system.SetSoftCorrectionReplaySimulation(false);
            for (var i = _replayFrozenSystems.Count - 1; i >= 0; i--)
            {
                if (_replayFrozenSystems[i] != system)
                    continue;

                _replayFrozenSystems.RemoveAt(i);
                system.OnReplayEnd();
            }
        }

        private void NotifyReplayEnd()
        {
            for (var i = 0; i < _replayFrozenSystems.Count; i++)
            {
                var system = _replayFrozenSystems[i];
                if (system)
                    system.OnReplayEnd();
            }

            _replayFrozenSystems.Clear();

            for (var i = 0; i < _systemsCount; i++)
            {
                _systems[i].SetSoftCorrectionReplaySimulation(false);
                _systems[i].SetSkipReplaySpawnInitialization(false);
            }
        }

        private void UpdateInterpolation(bool accumulateError)
        {
            for (var j = 0; j < _systemsCount; j++)
                _systems[j].RunUpdateRollbackInterpolation(tickDelta, accumulateError);
        }

        private enum HistorySaveMode
        {
            None,
            Full,
            VerifiedFrame
        }

        private void ReplayToLatestTick(ulong verifiedTick, HistorySaveMode saveMode)
        {
            using var _ = ReplayToLatestTickMarker.Auto();

            for (ulong simTick = verifiedTick; simTick < localTick; simTick++)
                SimulateFrame(simTick, saveMode);
        }

        private void SimulateFrame(ulong verifiedTick, HistorySaveMode saveMode,
            PredictionPassKind passKind = PredictionPassKind.SpeculativeReplay)
        {
            if (saveMode == HistorySaveMode.VerifiedFrame)
                passKind = PredictionPassKind.Verified;
            using var performance = PredictionPerformanceTelemetry.BeginPass(this, passKind);
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            isSimulating = true;
            localTickInContext = verifiedTick;

            LockSpeculativeRelayStates(verifiedTick);

            if (saveMode is HistorySaveMode.Full or HistorySaveMode.VerifiedFrame || isReplaying)
            {
                using (SaveHistoryMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                    {
                        var system = _systems[i];
                        if (!system.isEventHandler &&
                            (saveMode == HistorySaveMode.Full ||
                             system.isDeterministic ||
                             system.IsSoftCorrectionReplaySimulating()))
                        {
                            system.RunSaveState(verifiedTick);
                        }
                    }
                }
            }

            long prepareStarted = performance.Timestamp();
            using (SimulateInputsMarker.Auto())
            {
                try
                {
                    for (var i = 0; i < _systemsCount; i++)
                        _systems[i].RunPrepareSimulationInputs(verifiedTick, delta);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            performance.PrepareDone(prepareStarted);
            long simulateStarted = performance.Timestamp();
            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].RunSimulateTick(verifiedTick, delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                simulateMarker.Dispose();
            }

            performance.SimulateDone(simulateStarted);
            DoPhysicsPass(performance);

            long lateStarted = performance.Timestamp();
            var lateSimulateMarker = LateSimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].RunLateSimulateTick(delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateSimulateMarker.Dispose();
            }
            performance.LateDone(lateStarted);

            if (saveMode is HistorySaveMode.Full or HistorySaveMode.VerifiedFrame)
            {
                using (SaveHistoryMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                    {
                        var system = _systems[i];
                        if (system.isEventHandler)
                            system.RunSaveState(verifiedTick);
                    }
                }
            }

            try
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunPostSimulate();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            try
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].RunGetLatestUnityState();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            RestoreSpeculativeRelayStates();

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
            public ulong highestReceivedTick;
            public ulong rawHighestReceivedTick;
            public ulong ackedServerTick;
            public ulong lastConsumedTick;
            public double pendingInputSlackMs;
            public bool hasPendingInputSlack;
            public readonly Dictionary<ulong, InputQueueValue> byTick = new ();
            public int Count => byTick.Count;

            public void Clear()
            {
                foreach (var entry in byTick.Values)
                    entry.inputPacket.Dispose();
                byTick.Clear();
                rawHighestReceivedTick = 0;
                pendingInputSlackMs = 0;
                hasPendingInputSlack = false;
            }
        }

        readonly Dictionary<PlayerID, InputQueue> _clientTicks = new ();

        [ServerRpc(requireOwnership: false, channel: Channel.Unreliable, mtuExceeded: MTUBehaviour.Fragment, immediate: true)]
        private void SendInputToServerFragmented(ulong firstTick, uint tickCount, ulong frameAck, BitPacker payload, RPCInfo info = default)
        {
            ReceivedInput(firstTick, tickCount, frameAck, payload, info);
        }

        [ServerRpc(requireOwnership: false, channel: Channel.Unreliable, immediate: true)]
        private void SendInputToServer(ulong firstTick, uint tickCount, ulong frameAck, BitPacker payload, RPCInfo info = default)
        {
            ReceivedInput(firstTick, tickCount, frameAck, payload, info);
        }

        private void ReceivedInput(ulong firstTick, uint tickCount, ulong frameAck, BitPacker payload, RPCInfo info)
        {
            using (payload)
            {
                if (!_clientTicks.TryGetValue(info.sender, out var ticks))
                {
                    ticks = new InputQueue();
                    _clientTicks[info.sender] = ticks;
                }

                if (frameAck > ticks.ackedServerTick)
                    ticks.ackedServerTick = frameAck;

                if (tickCount > 0)
                {
                    ulong newestTick = firstTick + tickCount - 1;
                    if (newestTick > ticks.rawHighestReceivedTick)
                    {
                        ticks.rawHighestReceivedTick = newestTick;

                        if (_serverTickBoundaryTime > 0 && _tickManager != null)
                        {
                            double deadline = _serverTickBoundaryTime +
                                ((long)newestTick - (long)localTick + 1) * _tickManager.tickDeltaDouble;
                            ticks.pendingInputSlackMs = (deadline - Time.unscaledTimeAsDouble) * 1000d;
                            ticks.hasPendingInputSlack = true;
                        }
                    }
                }

                bool anyTickNeeded = false;
                for (uint i = 0; i < tickCount; i++)
                {
                    ulong tick = firstTick + i;
                    if (tick < localTick || tick <= ticks.lastConsumedTick)
                        continue;
                    if (tick > localTick + MaxInputWindow * 2)
                        continue;
                    if (ticks.byTick.ContainsKey(tick))
                        continue;
                    anyTickNeeded = true;
                    break;
                }

                if (!anyTickNeeded)
                    return;

                using var expandedA = BitPackerPool.Get();
                using var expandedB = BitPackerPool.Get();
                var prevExpanded = expandedA;
                var curExpanded = expandedB;
                var prevSpans = _recvPrevSpans;
                var curSpans = _recvCurSpans;
                prevSpans.Clear();
                curSpans.Clear();

                for (uint i = 0; i < tickCount; i++)
                {
                    PackedUInt blockBits = default;
                    Packer<PackedUInt>.Read(payload, ref blockBits);
                    PackedUInt count = default;
                    Packer<PackedUInt>.Read(payload, ref count);

                    ulong tick = firstTick + i;
                    int blockStart = payload.positionInBits;

                    curExpanded.ResetPositionAndMode(false);
                    curSpans.Clear();

                    for (uint e = 0; e < count.value; e++)
                    {
                        PredictedComponentID pid = default;
                        Packer<PredictedComponentID>.Read(payload, ref pid);
                        bool repeat = Packer<bool>.Read(payload);

                        Packer<PredictedComponentID>.Write(curExpanded, pid);

                        int payloadLength;
                        int origin = -1;

                        if (repeat)
                        {
                            for (var p = 0; p < prevSpans.Count; p++)
                            {
                                if (!prevSpans[p].id.Equals(pid))
                                    continue;
                                var prevSpan = prevSpans[p];
                                origin = curExpanded.positionInBits;
                                curExpanded.WriteBitDataWithoutConsumingIt(
                                    new BitData(prevExpanded, prevSpan.bitOrigin, prevSpan.bitLength));
                                payloadLength = prevSpan.bitLength;
                                curSpans.Add(new InputHistorySpan
                                {
                                    id = pid,
                                    bitOrigin = origin,
                                    bitLength = payloadLength
                                });
                                break;
                            }

                            if (origin < 0)
                                return;
                        }
                        else
                        {
                            PackedUInt bits = default;
                            Packer<PackedUInt>.Read(payload, ref bits);
                            payloadLength = checked((int)bits.value);
                            int sourceOrigin = payload.positionInBits;
                            payload.SkipBits(payloadLength);

                            origin = curExpanded.positionInBits;
                            curExpanded.WriteBitDataWithoutConsumingIt(
                                new BitData(payload, sourceOrigin, payloadLength));
                            curSpans.Add(new InputHistorySpan
                            {
                                id = pid,
                                bitOrigin = origin,
                                bitLength = payloadLength
                            });
                        }
                    }

                    if (payload.positionInBits - blockStart != (int)blockBits.value)
                        return;

                    bool tooOld = tick < localTick || tick <= ticks.lastConsumedTick;
                    bool tooFar = tick > localTick + MaxInputWindow * 2;

                    if (!tooOld && !tooFar && !ticks.byTick.ContainsKey(tick))
                    {
                        var slice = BitPackerPool.Get();
                        int expandedBits = curExpanded.positionInBits;
                        slice.WriteBitDataWithoutConsumingIt(new BitData(curExpanded, 0, expandedBits));
                        slice.ResetPositionAndMode(true);

                        if (tick > ticks.highestReceivedTick)
                            ticks.highestReceivedTick = tick;

                        ticks.byTick[tick] = new InputQueueValue
                        {
                            count = count,
                            inputPacket = slice,
                            clientTick = tick
                        };
                    }

                    (prevExpanded, curExpanded) = (curExpanded, prevExpanded);
                    (prevSpans, curSpans) = (curSpans, prevSpans);
                }

            }
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
                        system.QueueInput(inputPacket, sender);
                    }
                    else
                    {
                        PurrLogger.LogWarning(
                            $"Input entry {i}/{count} rejected from {sender}: id={pid} known={_instanceMap.ContainsKey(pid)} " +
                            $"owner={(system ? system.owner?.ToString() ?? "none" : "n/a")}; skipping remainder of the block.");
                        break;
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
            PredictionPerformanceTelemetry.ObserveFrame(this);
            if (isSpawned && isClient && !isServer)
            {
                ResendCachedInput();

                // NetworkManager (-999) has finished all catch-up ticks and its final receive
                // poll before this Update (1000). Apply every queued verified frame, then
                // rebuild the speculative future once before either view update mode runs.
                if (ShouldApplyQueuedFramesInRenderPhase(localTick, _deltas.Count, isSimulating, isReplaying))
                    ProcessQueuedFrames(true);
            }

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

        internal uint viewPassId { get; private set; }

        private void UpdateView()
        {
            viewPassId++;

            var updateViewMarker = UpdateViewMarker.Auto();
            try
            {
                var dt = Time.unscaledDeltaTime;
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunUpdateView(dt);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                updateViewMarker.Dispose();
            }

            LateUpdateView();
        }

        private void LateUpdateView()
        {
            var lateUpdateViewMarker = UpdateViewMarker.Auto();
            try
            {
                var dt = Time.unscaledDeltaTime;
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunLateUpdateView(dt);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateUpdateViewMarker.Dispose();
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

        internal GameObject InternalCreate(GameObject prefab, Vector3 position, Quaternion rotation, out bool fromPool)
        {
            if (_pools.TryGetPool(prefab, out var pool))
            {
                var go = pool.Allocate();
                var trs = go.transform;
                ProperlySetPosAndRot(trs, position, rotation);
                trs.SetParent(null);
                if (!go.activeSelf)
                    go.SetActive(true);
                fromPool = true;
                return go;
            }
            else
            {
                var go = UnityProxy.InstantiateDirectly(prefab, position, rotation, gameObject.scene);
                if (!go.activeSelf)
                    go.SetActive(true);
                fromPool = false;
                return go;
            }
        }

        internal void InternalDelete(PackedInt prefabId, GameObject instance)
        {
            int pid = prefabId;

            if (!_predictedPrefabs || pid < 0 || pid >= _predictedPrefabs.prefabs.Count)
            {
                UnregisterInstance(instance, false, true);
                UnityProxy.DestroyImmediateDirectly(instance);
                return;
            }

            var prefabsInfo = _predictedPrefabs.prefabs[pid];

            if (!prefabsInfo.pooled)
            {
                UnregisterInstance(instance, false, true);
                UnityProxy.DestroyImmediateDirectly(instance);
                return;
            }

            if (_pools != null && _pools.TryGetPool(prefabsInfo.prefab, out var pool))
            {
                UnregisterPooledInstance(instance);
                pool.Delete(instance);
            }
            else
            {
                UnregisterInstance(instance, false, true);
                UnityProxy.DestroyImmediateDirectly(instance);
            }
        }

        public void SetOwnership(PredictedObjectID? root, PlayerID? player, bool cascade = true)
        {
            if (!hierarchy.TryGetGameObject(root, out var rootGo))
                return;

            var children = ListPool<PredictedIdentity>.Instantiate();

            if (cascade)
                hierarchy.CollectInstanceIdentities(rootGo, root!.Value, children);
            else
                rootGo.GetComponents(children);

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                child.SetOwner(player);
            }

            ListPool<PredictedIdentity>.Destroy(children);
        }

        public static bool TryGetClosestPredictedID(GameObject go, out PredictedComponentID pid)
        {
            if (!go)
            {
                pid = default;
                return false;
            }

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
