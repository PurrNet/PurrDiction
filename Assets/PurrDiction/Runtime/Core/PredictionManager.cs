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
    [Serializable]
    public struct InputQueueSettings
    {
        [Tooltip("When a client's input for the current tick has not arrived, reuse its last known input instead of simulating with default input.")]
        public bool extrapolateForMissing;
    }

    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("PurrDiction/Prediction Manager")]
    public class PredictionManager : NetworkIdentity
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
        [SerializeField] private PredictionLODProfile _predictionLODProfile;
        [SerializeField] private InputQueueSettings _inputQueueSettings = new()
        {
            extrapolateForMissing = true
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

        public PredictionLODProfile predictionLODProfile
        {
            get => _predictionLODProfile;
            set
            {
                _predictionLODProfile = value;
                if (isSpawned)
                    InitializeInterest();
            }
        }

        public PredictionInterestModule interest { get; private set; }

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
            InitializeInterest();
        }

        protected override void OnDespawned()
        {
            if (_tickManager != null)
            {
                _tickManager.onPreTick -= OnPreTick;
                _tickManager.onPostTick -= OnPostTick;
                _tickManager = null;
            }

            interest?.Dispose();
            interest = null;

            CleanupAllSystems();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            interest?.Dispose();
            interest = null;

            foreach (var packer in _clientFrames)
                packer.Dispose();
            _clientFrames.Clear();

            if (_tickManager != null)
            {
                _tickManager.onPreTick -= OnPreTick;
                _tickManager.onPostTick -= OnPostTick;
            }

            if (_pools != null)
            {
                _pools.Dispose();
                _pools = null;
            }
        }

        private void InitializeInterest()
        {
            interest?.Dispose();
            interest = null;

            if (!isServer)
                ClearAllLocalInterestSideEffects();

            if (!_predictionLODProfile)
                return;

            NetworkLODFactory factory = null;
            NetworkLODModule lodModule = null;

            if (isServer && networkManager.TryGetModule<NetworkLODFactory>(true, out factory))
                factory.TryGetModule(sceneId, out lodModule);

            interest = new PredictionInterestModule(
                this,
                _predictionLODProfile,
                factory,
                lodModule,
                isServer);

            if (!isServer)
                SyncAllLocalInterestSideEffects();

            if (isServer)
            {
                for (var i = 0; i < _clientFrames.Count; i++)
                    interest.InitializePlayer(_clientFrames[i].player);
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
            _replayFrozenSystems.Clear();
            _speculativeRelayLocks.Clear();
            _systemsCount = 0;
            DisposeInputBlockCache();
            InvalidateInputBlockCache();
            _nextSystemId = 0;
            foreach (var queue in _clientTicks.Values)
                queue.Clear();
            _clientTicks.Clear();
            _clientFrames.Clear();
            _pendingStateRepairs.Clear();
            _stateRepairScratch.Clear();
            localTick = 1;
            localTickInContext = 1;
            _verifiedServerTick = 0;
            _pauseAdvanceTicks = 0;
            _latestFrameServerTick = 0;
            _inputStarved = false;
            _inputAckTick = 0;
            _frameInputMargin = 0;
            _frameInputMarginTick = 0;
            _hasFrameInputMargin = false;
            _leadAdjustGateTick = 0;
            ClearVerifiedStores();
            _deltas.Clear();
        }

        private uint _nextSystemId = 0;

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
                    component.PrepareInterestPolicyForRegistration(preserveState);
                    var incomingPolicy = component.ResolvePredictionPolicyForSetup();
                    bool preserveSoftState = preserveState &&
                                             component.previousRegisteredPredictionPolicy == PredictionPolicy.SoftCorrection &&
                                             incomingPolicy == PredictionPolicy.SoftCorrection;

                    if (!preserveSoftState)
                        component.OnPreSetup();
                    if (reset)
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
                    if (reset)
                        components[i].ResetState();
                    UnregisterInstance(components[i]);
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
            _pendingStateRepairs.Remove(pid);
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
            InvalidateInputBlockCache();

            if (!isServer)
            {
                if (_ownedRootsCacheValid && _ownedRootsCachePlayer.HasValue &&
                    system.owner == _ownedRootsCachePlayer)
                    _ownedRootsCache.Add(system.rootObjectId);
                SyncLocalInterestSideEffects(system);
            }

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
            InvalidateLocallyOwnedRoots();
            RemoveSpeculativeRelayLock(predictedIdentity);
            var id = predictedIdentity.id;
            if (_instanceMap.TryGetValue(id, out var registeredIdentity) &&
                ReferenceEquals(registeredIdentity, predictedIdentity))
            {
                _instanceMap.Remove(id);
                _pendingStateRepairs.Remove(id);

                for (var i = 0; i < _clientFrames.Count; i++)
                    _clientFrames[i].identityEstablishment?.Remove(id);
            }

            if (_systems.Remove(predictedIdentity))
            {
                --_systemsCount;
                InvalidateInputBlockCache();
                predictedIdentity.RecordCompletedRegistrationPolicy();
            }
        }

        protected override void OnObserverRemoved(PlayerID player)
        {
            _clientTicks.Remove(player);
            _pendingFullSync.Remove(player);
            interest?.RemovePlayer(player);

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

                _clientTicks[player] = new InputQueue();

                var found = false;
                for (var i = 0; i < _clientFrames.Count; i++)
                {
                    var clientFrame = _clientFrames[i];
                    if (!clientFrame.player.Equals(player))
                        continue;

                    clientFrame.fullFrame = true;
                    clientFrame.identityEstablishment ??= new IdentityEstablishmentState();
                    clientFrame.identityEstablishment.Clear();
                    clientFrame.sendBaselines ??= new RootSendBaselineState();
                    clientFrame.sendBaselines.Clear();
                    clientFrame.interestControls ??= new InterestControlState();
                    clientFrame.interestControls.Clear();
                    _clientFrames[i] = clientFrame;
                    interest?.InitializePlayer(player);
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
                    identityEstablishment = new IdentityEstablishmentState(),
                    sendBaselines = new RootSendBaselineState(),
                    interestControls = new InterestControlState()
                });

                interest?.InitializePlayer(player);
            }

            _pendingFullSync.Clear();
        }

        private void ReadFullFrame(BitPacker frame, ulong stateTick, ulong inputTick, ulong serverTick)
        {
            frame.ResetPositionAndMode(true);
            _pendingStateRepairs.Clear();
            _stateRepairScratch.Clear();

            tickRate = Packer<PackedInt>.Read(frame);
            tickDelta = Packer<float>.Read(frame);
            _sessionSeed = Packer<uint>.Read(frame);

            ReadFirstStateEntries(frame, stateTick, serverTick, false);

            int inputCount = Packer<PackedInt>.Read(frame);
            if (inputCount >= 0)
            {
                for (var i = 0; i < inputCount; ++i)
                    _systems[i].ReadFirstInput(inputTick, frame);
            }
            else
            {
                ReadFilteredFirstInputEntries(frame, inputTick, -inputCount - 1);
            }

            ReadFirstStateEntries(frame, stateTick, serverTick, true);

            SyncAllLocalInterestSideEffects();
            SyncTransforms();
        }

        private void WriteFilteredFirstInputEntries(PlayerID receiver, BitPacker frame, BitPacker scratch)
        {
            FillInputSerializeFlags(receiver);
            int count = 0;

            for (var i = 0; i < _systemsCount; i++)
            {
                if (_inputSerializeFlags[i] && _systems[i].hasInput)
                    count++;
            }

            Packer<PackedInt>.Write(frame, -(count + 1));

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (!system.hasInput || !_inputSerializeFlags[i])
                    continue;

                scratch.ResetPositionAndMode(false);
                system.WriteFirstInput(localTick, scratch);
                Packer<PredictedComponentID>.Write(frame, system.id);
                Packer<PackedUInt>.Write(frame, (PackedUInt)(uint)scratch.positionInBits);
                frame.WriteBitsWithoutConsumingIt(scratch, scratch.positionInBits);
            }
        }

        private void ReadFilteredFirstInputEntries(BitPacker frame, ulong inputTick, int count)
        {
            for (var i = 0; i < count; i++)
            {
                PredictedComponentID id = default;
                Packer<PredictedComponentID>.Read(frame, ref id);
                PackedUInt payloadBits = default;
                Packer<PackedUInt>.Read(frame, ref payloadBits);
                int payloadEnd = frame.positionInBits + (int)payloadBits.value;

                if (_instanceMap.TryGetValue(id, out var system) && CanApplyLocalInput(system, inputTick))
                    system.ReadFirstInput(inputTick, frame);

                CompleteStateEntry(frame, payloadEnd);
            }
        }

        readonly List<PlayerPacker> _clientFrames = new (16);
        readonly Dictionary<PredictedComponentID, ulong> _pendingStateRepairs = new ();
        readonly List<PredictedComponentID> _stateRepairScratch = new ();

        public bool cachedIsServer { get; private set; }

        private void OnPreTick()
        {
            cachedIsServer = isServer;
            localTickInContext = localTick;

            if (cachedIsServer)
                interest?.RefreshServerTargets(_systems, _systemsCount);

            if (!cachedIsServer)
            {
                PruneStateEntryRepairs();
                if (interest != null && localTick % (ulong)Math.Max(1, tickRate) == 0)
                    interest.PruneStaleLocalTiers();
            }

            if (!cachedIsServer && _pauseAdvanceTicks > 0)
            {
                _pauseAdvanceTicks--;
                return;
            }

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
                if (cachedIsServer || IsLocallyRelevant(system))
                    system.PrepareInput(cachedIsServer, controller, localTick, _inputQueueSettings.extrapolateForMissing);
            }

            if (!cachedIsServer)
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].SyncEffectivePolicySideEffects();
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

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunPrepareSimulationInputs(localTick, delta);
            }

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

            DoPhysicsPass();

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

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].RunPostSimulate();

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

        private const int InputMtu = 1024;
        private const ulong MaxInputWindow = 32;

        private ulong _inputAckTick;

        private void FinalizeInputOnClient(DisposableList<PredictedIdentity> ownedIdentities)
        {
            for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                _systems[systemIdx].RunGetLatestUnityState();

            ulong firstTick = _inputAckTick + 1;

            if (localTick >= MaxInputWindow && firstTick < localTick - MaxInputWindow + 1)
                firstTick = localTick - MaxInputWindow + 1;

            if (firstTick > localTick)
                firstTick = localTick;

            using var payload = BitPackerPool.Get();
            using var block = BitPackerPool.Get();

            uint tickCount = 0;
            var count = ownedIdentities.Count;

            for (ulong tick = firstTick; tick <= localTick; tick++)
            {
                block.ResetPositionAndMode(false);
                uint writtenCount = 0;

                for (var ownedIdx = 0; ownedIdx < count; ownedIdx++)
                {
                    var owned = ownedIdentities[ownedIdx];
                    if (owned && owned.hasInput)
                    {
                        Packer<PredictedComponentID>.Write(block, owned.id);
                        owned.WriteFirstInput(tick, block);
                        writtenCount += 1;
                    }
                }

                int blockBits = block.positionInBits;
                Packer<PackedUInt>.Write(payload, (PackedUInt)(uint)blockBits);
                Packer<PackedUInt>.Write(payload, (PackedUInt)writtenCount);
                payload.WriteBitsWithoutConsumingIt(block, blockBits);
                tickCount += 1;
            }

            if (payload.positionInBytes >= InputMtu)
                SendInputToServerFragmented(firstTick, tickCount, _verifiedServerTick, payload);
            else SendInputToServer(firstTick, tickCount, _verifiedServerTick, payload);
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

            PackedInt packedInputSystemCount = _systemsCount;
            PackedInt packedTickRate = tickRate;

            ulong maxAckLag = 0;
            using var stateScratch = BitPackerPool.Get();
            using var inputScratch = BitPackerPool.Get();

            for (var j = 0; j < fCount; j++)
            {
                var clientFrame = _clientFrames[j];
                clientFrame.packer.ResetPositionAndMode(false);

                var frame = clientFrame.packer;
                var player = clientFrame.player;

                ulong baselineTick = 0;
                if (_clientTicks.TryGetValue(player, out var ackQueue))
                    baselineTick = ackQueue.ackedServerTick;

                ulong ackLag = localTick > baselineTick ? localTick - baselineTick : 0;
                if (ackLag > maxAckLag)
                    maxAckLag = ackLag;

                if (!clientFrame.fullFrame && baselineTick > 0 &&
                    localTick > baselineTick && localTick - baselineTick > (ulong)(tickRate * 8))
                {
                    clientFrame.fullFrame = true;
                    _clientFrames[j] = clientFrame;
                }

                var fullFrame = clientFrame.fullFrame;
                var identityEstablishment = clientFrame.identityEstablishment;
                var sendBaselines = clientFrame.sendBaselines;
                var interestControls = clientFrame.interestControls;
                interestControls?.PrepareForFrame(localTick);
                if (localTick > (ulong)(tickRate * 10) && localTick % (ulong)Math.Max(1, tickRate) == 0)
                    sendBaselines.PruneBefore(localTick - (ulong)(tickRate * 10));

                bool filtered = RequiresFilteredStateEntries(player, interestControls);
                if (fullFrame || filtered)
                    FillSerializeFlags(player, interestControls, baselineTick, fullFrame);

                if (fullFrame)
                {
                    using var _ = WriteFullFrameMarker.Auto();

                    identityEstablishment.Reset();
                    sendBaselines.Clear();

                    Packer<PackedInt>.Write(frame, packedTickRate);
                    Packer<float>.Write(frame, tickDelta);
                    Packer<uint>.Write(frame, _sessionSeed);
                    WriteFirstStateEntries(
                        frame,
                        stateScratch,
                        identityEstablishment,
                        sendBaselines,
                        interestControls,
                        false);

                    if (interest != null && interest.HasCulledRoots(player))
                        WriteFilteredFirstInputEntries(player, frame, inputScratch);
                    else
                    {
                        Packer<PackedInt>.Write(frame, packedInputSystemCount);
                        for (var i = 0; i < _systemsCount; i++)
                            _systems[i].WriteFirstInput(localTick, frame);
                    }
                }
                else
                {
                    bool forceHierarchyAbsolute = filtered && PrepareIdentityEstablishment(
                        identityEstablishment,
                        baselineTick);

                    using (WriteInputHistoryMarker.Auto())
                        WriteInputHistory(player, frame, baselineTick);

                    using (WriteStateDeltasMarker.Auto())
                    {
                        Packer<bool>.Write(frame, filtered);

                        if (filtered)
                        {
                            WriteCurrentStateEntries(
                                player,
                                frame,
                                stateScratch,
                                identityEstablishment,
                                sendBaselines,
                                interestControls,
                                baselineTick,
                                forceHierarchyAbsolute,
                                false);
                        }
                        else
                        {
                            WritePositionalStateEntries(player, frame, sendBaselines, baselineTick, false);
                        }
                    }
                }
            }

            lastMaxAckLagTicks = maxAckLag;
        }

        private bool RequiresFilteredStateEntries(PlayerID player, InterestControlState interestControls)
        {
            if (interest == null)
                return false;
            return interest.HasSerializationAffectingRoots(player) ||
                   interestControls != null && interestControls.hasUnackedReentries;
        }

        private void WritePositionalStateEntries(
            PlayerID receiver,
            BitPacker frame,
            RootSendBaselineState sendBaselines,
            ulong ackedTick,
            bool eventHandlers)
        {
            PackedInt count = _systemsCount;
            Packer<PackedInt>.Write(frame, count);

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler != eventHandlers)
                    continue;

                var root = system.rootObjectId;
                system.RunWriteCurrentState(receiver, frame, sendBaselines.GetBaseline(root, ackedTick));
                sendBaselines.MarkSent(root, localTick);
            }
        }

        private void ReadPositionalStateEntries(
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool eventHandlers)
        {
            PackedInt packedCount = default;
            Packer<PackedInt>.Read(frame, ref packedCount);
            int count = packedCount;

            for (var i = 0; i < count; i++)
            {
                if (i >= _systemsCount)
                    break;

                var system = _systems[i];
                if (system.isEventHandler != eventHandlers)
                    continue;

                if (!eventHandlers && _validateDeterministicData && system.isDeterministic)
                    system.RunRollback(stateTick);
                bool softCorrected = system.UsesSoftCorrectionTimeline();
                if (!softCorrected)
                    system.RunClearFuture(stateTick);
                system.RunReadState(stateTick, frame, baselineTick, serverTick);
                if (!softCorrected)
                    system.RunRollback(stateTick);
                system.lastVerifiedTick = stateTick;
            }
        }

        private bool PrepareIdentityEstablishment(
            IdentityEstablishmentState identityEstablishment,
            ulong ackedTick)
        {
            bool requiresHierarchyBarrier = false;

            for (var i = 0; i < _systemsCount; i++)
            {
                if (!_stateSerializeFlags[i])
                    continue;

                requiresHierarchyBarrier |= identityEstablishment.Prepare(_systems[i].id, ackedTick, localTick);
            }

            return requiresHierarchyBarrier;
        }

        private uint CountStateEntries(bool eventHandlers)
        {
            uint count = 0;

            for (var i = 0; i < _systemsCount; i++)
            {
                if (_stateSerializeFlags[i] && _systems[i].isEventHandler == eventHandlers)
                    count++;
            }

            return count;
        }

        private void WriteFirstStateEntries(
            BitPacker frame,
            BitPacker scratch,
            IdentityEstablishmentState identityEstablishment,
            RootSendBaselineState sendBaselines,
            InterestControlState interestControls,
            bool eventHandlers)
        {
            Packer<PackedUInt>.Write(frame, (PackedUInt)CountStateEntries(eventHandlers));

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler != eventHandlers || !_stateSerializeFlags[i])
                    continue;

                var root = system.rootObjectId;
                scratch.ResetPositionAndMode(false);
                if (interestControls != null && interestControls.ConsumeBaselineReset(root))
                    sendBaselines.Reset(root);
                system.RunWriteFirstState(localTick, scratch);
                WriteStateEntry(frame, scratch, system.id, true);
                identityEstablishment.MarkSent(system.id, localTick);
                sendBaselines.MarkSent(root, localTick);
            }
        }

        private void WriteCurrentStateEntries(
            PlayerID receiver,
            BitPacker frame,
            BitPacker scratch,
            IdentityEstablishmentState identityEstablishment,
            RootSendBaselineState sendBaselines,
            InterestControlState interestControls,
            ulong baselineTick,
            bool forceHierarchyAbsolute,
            bool eventHandlers)
        {
            Packer<PackedUInt>.Write(frame, (PackedUInt)CountStateEntries(eventHandlers));

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler != eventHandlers || !_stateSerializeFlags[i])
                    continue;

                var root = system.rootObjectId;
                scratch.ResetPositionAndMode(false);
                bool reentering = interestControls != null &&
                                   interestControls.RequiresAbsolute(root, baselineTick);
                bool absolute = identityEstablishment.RequiresAbsolute(system.id) ||
                                forceHierarchyAbsolute && system == hierarchy ||
                                reentering;

                if (absolute)
                {
                    if (reentering && interestControls.ConsumeBaselineReset(root))
                        sendBaselines.Reset(root);
                    system.RunWriteAbsoluteState(localTick, scratch);
                }
                else
                {
                    ulong identityBaseline = sendBaselines.GetBaseline(root, baselineTick);
                    system.RunWriteCurrentState(receiver, scratch, identityBaseline);
                }

                WriteStateEntry(frame, scratch, system.id, absolute);
                sendBaselines.MarkSent(root, localTick);
            }
        }

        private static void WriteStateEntry(
            BitPacker frame,
            BitPacker payload,
            PredictedComponentID id,
            bool absolute)
        {
            Packer<PredictedComponentID>.Write(frame, id);
            Packer<bool>.Write(frame, absolute);
            Packer<PackedUInt>.Write(frame, (PackedUInt)(uint)payload.positionInBits);
            frame.WriteBitsWithoutConsumingIt(payload, payload.positionInBits);
        }

        private void ReadFirstStateEntries(
            BitPacker frame,
            ulong stateTick,
            ulong serverTick,
            bool eventHandlers)
        {
            PackedUInt entryCount = default;
            Packer<PackedUInt>.Read(frame, ref entryCount);

            for (uint i = 0; i < entryCount.value; i++)
            {
                if (!TryReadStateEntry(
                        frame,
                        eventHandlers,
                        out var system,
                        out _,
                        out var payloadEnd))
                    continue;

                if (interest != null && !interest.CanApplyLocalAbsolute(system.rootObjectId, serverTick))
                {
                    CompleteStateEntry(frame, payloadEnd);
                    continue;
                }

                ReadAbsoluteStateEntry(system, frame, stateTick, serverTick, eventHandlers, true);
                CompleteStateEntry(frame, payloadEnd);
            }
        }

        private void ReadCurrentStateEntries(
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick,
            bool eventHandlers)
        {
            PackedUInt entryCount = default;
            Packer<PackedUInt>.Read(frame, ref entryCount);

            for (uint i = 0; i < entryCount.value; i++)
            {
                if (!TryReadStateEntry(
                        frame,
                        eventHandlers,
                        out var system,
                        out var absolute,
                        out var payloadEnd))
                    continue;

                if (absolute)
                {
                    if (interest != null && !interest.CanApplyLocalAbsolute(system.rootObjectId, serverTick))
                    {
                        CompleteStateEntry(frame, payloadEnd);
                        continue;
                    }

                    ReadAbsoluteStateEntry(system, frame, stateTick, serverTick, eventHandlers, false);
                    CompleteStateEntry(frame, payloadEnd);
                    continue;
                }

                if (system.IsLocallyDormant())
                {
                    CompleteStateEntry(frame, payloadEnd);
                    continue;
                }

                if (!eventHandlers && _validateDeterministicData && system.isDeterministic)
                    system.RunRollback(stateTick);
                bool softCorrected = system.UsesSoftCorrectionTimeline();
                if (!softCorrected)
                    system.RunClearFuture(stateTick);
                system.RunReadState(stateTick, frame, baselineTick, serverTick);
                if (!softCorrected)
                    system.RunRollback(stateTick);
                system.lastVerifiedTick = stateTick;
                CompleteStateEntry(frame, payloadEnd);
            }
        }

        private void ReadAbsoluteStateEntry(
            PredictedIdentity system,
            BitPacker frame,
            ulong stateTick,
            ulong serverTick,
            bool eventHandlers,
            bool firstState)
        {
            system.RunClearFuture(stateTick);
            if (firstState)
                system.RunReadFirstState(stateTick, frame, serverTick);
            else
                system.RunReadAbsoluteState(stateTick, frame, serverTick);
            system.RunRollback(stateTick, true);
            if (!eventHandlers)
                system.RunResetInterpolation();
            system.lastVerifiedTick = stateTick;
            interest?.ConfirmLocalAbsolute(system.rootObjectId, serverTick);
        }

        private bool TryReadStateEntry(
            BitPacker frame,
            bool eventHandlers,
            out PredictedIdentity system,
            out bool absolute,
            out int payloadEnd)
        {
            PredictedComponentID id = default;
            Packer<PredictedComponentID>.Read(frame, ref id);
            absolute = Packer<bool>.Read(frame);
            PackedUInt payloadBits = default;
            Packer<PackedUInt>.Read(frame, ref payloadBits);
            payloadEnd = frame.positionInBits + (int)payloadBits.value;

            if (_instanceMap.TryGetValue(id, out system))
            {
                _pendingStateRepairs.Remove(id);

                if (system.isEventHandler == eventHandlers)
                    return true;

                frame.SkipBits((int)payloadBits.value);
                system = null;
                return false;
            }

            if (absolute)
                _pendingStateRepairs.Remove(id);
            else
                TryRequestStateEntryRepair(id);

            frame.SkipBits((int)payloadBits.value);
            system = null;
            return false;
        }

        private void TryRequestStateEntryRepair(PredictedComponentID id)
        {
            ulong retryTicks = (ulong)Math.Max(1, tickRate);

            if (_pendingStateRepairs.TryGetValue(id, out var requestTick) &&
                localTick >= requestTick && localTick - requestTick < retryTicks)
                return;

            _pendingStateRepairs[id] = localTick;
            RequestStateEntryRepair(id);
        }

        private void PruneStateEntryRepairs()
        {
            if (_pendingStateRepairs.Count == 0)
                return;

            ulong interval = (ulong)Math.Max(1, tickRate);
            if (localTick % interval != 0)
                return;

            ulong lifetime = interval * 10;
            _stateRepairScratch.Clear();

            foreach (var repair in _pendingStateRepairs)
            {
                if (localTick < repair.Value || localTick - repair.Value >= lifetime)
                    _stateRepairScratch.Add(repair.Key);
            }

            for (var i = 0; i < _stateRepairScratch.Count; i++)
                _pendingStateRepairs.Remove(_stateRepairScratch[i]);

            _stateRepairScratch.Clear();
        }

        private static void CompleteStateEntry(BitPacker frame, int payloadEnd)
        {
            int remainingBits = payloadEnd - frame.positionInBits;
            if (remainingBits < 0)
                throw new InvalidOperationException("State entry payload exceeded its declared bit length.");
            if (remainingBits > 0)
                frame.SkipBits(remainingBits);
        }

        /// <summary>
        /// Largest gap, in ticks, between the current server tick and any connected client's
        /// last acked frame at the time the previous server frame was written. Diagnostic only.
        /// </summary>
        public ulong lastMaxAckLagTicks { get; private set; }

        private struct InputBlockEntry
        {
            public int systemIndex;
            public int bitOrigin;
            public int bitCount;
        }

        private struct CachedInputBlock
        {
            public ulong tick;
            public uint version;
            public BitPacker packer;
            public List<InputBlockEntry> entries;
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
                _inputBlockCache[i] = default;
            }

            _inputBlockCache = null;
        }

        private BitPacker GetInputBlockForTick(ulong tick, out List<InputBlockEntry> entries)
        {
            _inputBlockCache ??= new CachedInputBlock[(int)MaxInputWindow + 1];

            var index = (int)(tick % (ulong)_inputBlockCache.Length);
            ref var slot = ref _inputBlockCache[index];

            if (slot.packer != null && slot.tick == tick && slot.version == _inputBlockVersion)
            {
                entries = slot.entries;
                return slot.packer;
            }

            slot.packer ??= BitPackerPool.Get();
            slot.entries ??= new List<InputBlockEntry>(16);
            slot.tick = tick;
            slot.version = _inputBlockVersion;

            var block = slot.packer;
            block.ResetPositionAndMode(false);
            entries = slot.entries;
            entries.Clear();

            uint entryCount = 0;
            for (var i = 0; i < _systemsCount; i++)
            {
                var sys = _systems[i];
                if (sys.hasInput && sys.HasInputAt(tick))
                    entryCount++;
            }

            Packer<PackedUInt>.Write(block, (PackedUInt)entryCount);

            if (entryCount == 0)
                return block;

            using var entryScratch = BitPackerPool.Get();

            for (var i = 0; i < _systemsCount; i++)
            {
                var sys = _systems[i];
                if (!sys.hasInput || !sys.HasInputAt(tick))
                    continue;

                int entryOrigin = block.positionInBits;
                Packer<PredictedComponentID>.Write(block, sys.id);
                entryScratch.ResetPositionAndMode(false);
                sys.WriteFirstInput(tick, entryScratch);
                int bits = entryScratch.positionInBits;
                Packer<PackedUInt>.Write(block, (PackedUInt)(uint)bits);
                block.WriteBitsWithoutConsumingIt(entryScratch, bits);
                entries.Add(new InputBlockEntry
                {
                    systemIndex = i,
                    bitOrigin = entryOrigin,
                    bitCount = block.positionInBits - entryOrigin
                });
            }

            return block;
        }

        private void WriteInputHistory(PlayerID receiver, BitPacker frame, ulong baselineTick)
        {
            ulong from = baselineTick;
            if (localTick > MaxInputWindow && from < localTick - MaxInputWindow)
                from = localTick - MaxInputWindow;
            if (from > localTick)
                from = localTick;

            Packer<PackedUInt>.Write(frame, (PackedUInt)(uint)(localTick - from));

            bool filtered = interest != null && interest.HasCulledRoots(receiver);
            if (filtered)
                FillInputSerializeFlags(receiver);

            for (ulong t = from + 1; t <= localTick; t++)
            {
                var block = GetInputBlockForTick(t, out var entries);

                if (filtered)
                    WriteFilteredInputBlock(frame, block, entries);
                else
                    frame.WriteBitsWithoutConsumingIt(block, block.positionInBits);
            }
        }

        private void WriteFilteredInputBlock(BitPacker frame, BitPacker block, List<InputBlockEntry> entries)
        {
            uint entryCount = 0;

            for (var i = 0; i < entries.Count; i++)
            {
                if (_inputSerializeFlags[entries[i].systemIndex])
                    entryCount++;
            }

            Packer<PackedUInt>.Write(frame, (PackedUInt)entryCount);

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (_inputSerializeFlags[entry.systemIndex])
                    frame.WriteBitsWithoutConsumingIt(block, entry.bitOrigin, entry.bitCount);
            }
        }

        private void ReadInputHistory(BitPacker frame, ulong serverTick)
        {
            PackedUInt tickCount = default;
            Packer<PackedUInt>.Read(frame, ref tickCount);

            ulong from = serverTick - tickCount.value;

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

                    if (_instanceMap.TryGetValue(pid, out var system) && CanApplyLocalInput(system, t))
                        system.ReadFirstInput(t, frame);
                    else
                        frame.SkipBits((int)bits.value);
                }
            }
        }

        private bool CanApplyLocalInput(PredictedIdentity system, ulong inputTick)
        {
            if (IsLocallyRelevant(system))
                return true;
            return interest != null && interest.CanApplyLocalInput(system.rootObjectId, inputTick);
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
        }

        private void WriteEventHandles()
        {
            using var _ = WriteEventHandlesMarker.Auto();

            var fCount = _clientFrames.Count;
            using var stateScratch = BitPackerPool.Get();

            for (var j = 0; j < fCount; j++)
            {
                var clientFrame = _clientFrames[j];
                ulong baselineTick = 0;
                if (_clientTicks.TryGetValue(clientFrame.player, out var ackQueue))
                    baselineTick = ackQueue.ackedServerTick;

                if (clientFrame.fullFrame)
                {
                    FillSerializeFlags(
                        clientFrame.player,
                        clientFrame.interestControls,
                        baselineTick,
                        true);
                    WriteFirstStateEntries(
                        clientFrame.packer,
                        stateScratch,
                        clientFrame.identityEstablishment,
                        clientFrame.sendBaselines,
                        clientFrame.interestControls,
                        true);
                    continue;
                }

                bool filtered = RequiresFilteredStateEntries(clientFrame.player, clientFrame.interestControls);
                Packer<bool>.Write(clientFrame.packer, filtered);

                if (filtered)
                {
                    FillSerializeFlags(
                        clientFrame.player,
                        clientFrame.interestControls,
                        baselineTick,
                        false);
                    WriteCurrentStateEntries(
                        clientFrame.player,
                        clientFrame.packer,
                        stateScratch,
                        clientFrame.identityEstablishment,
                        clientFrame.sendBaselines,
                        clientFrame.interestControls,
                        baselineTick,
                        false,
                        true);
                }
                else
                {
                    WritePositionalStateEntries(
                        clientFrame.player,
                        clientFrame.packer,
                        clientFrame.sendBaselines,
                        baselineTick,
                        true);
                }
            }
        }

        private void SendFrameToOthers()
        {
            using var _ = SendFrameMarker.Auto();

            var fCount = _clientFrames.Count;
            using var controlPacker = BitPackerPool.Get();

            for (var j = 0; j < fCount; j++)
            {
                var clientFrame = _clientFrames[j];
                var player = clientFrame.player;
                var packer = clientFrame.packer;
                var deltaLen = packer.ToByteData().length;
                var fullFrame = clientFrame.fullFrame;
                var interestControls = clientFrame.interestControls;
                bool hasInterestControls = interestControls != null && interestControls.count > 0;
                bool hasUnackedReentries = interestControls != null && interestControls.hasUnackedReentries;

                TickBandwidthProfiler.OnWroteFrame(player, deltaLen * 8);

                ulong inputAck = 0;
                ulong baselineTick = 0;
                bool hasInputMargin = false;
                PackedInt inputMargin = 0;

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
                }

                if (hasInterestControls)
                {
                    controlPacker.ResetPositionAndMode(false);
                    WriteInterestControls(controlPacker, interestControls);
                    int controlLength = controlPacker.ToByteData().length;
                    SendFrameToRemoteWithInterest(
                        player,
                        localTick,
                        baselineTick,
                        inputAck,
                        fullFrame,
                        hasInputMargin,
                        inputMargin,
                        new BitPackerWithLength(controlLength, controlPacker),
                        new BitPackerWithLength(deltaLen, packer));
                    interestControls.MarkDelivered();
                }
                else if (fullFrame || hasUnackedReentries)
                    SendFrameToRemoteReliable(player, localTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, new BitPackerWithLength(deltaLen, packer));
                else SendFrameToRemote(player, localTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, new BitPackerWithLength(deltaLen, packer));
                if (fullFrame)
                {
                    clientFrame.fullFrame = false;
                    _clientFrames[j] = clientFrame;
                }
            }
        }

        private static void WriteInterestControls(BitPacker packer, InterestControlState controls)
        {
            Packer<PackedUInt>.Write(packer, (PackedUInt)(uint)controls.count);

            for (var i = 0; i < controls.count; i++)
            {
                var change = controls[i];
                Packer<PredictedObjectID>.Write(packer, change.root);
                Packer<byte>.Write(packer, change.tier);
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

        private void DoPhysicsPass()
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

            public void Dispose()
            {
                packer?.Dispose();
            }
        }

        readonly Queue<FrameDelta> _deltas = new ();

        [TargetRpc(channel: Channel.Unreliable, compressionLevel: CompressionLevel.Fast, mtuExceeded: MTUBehaviour.Fragment)]
        private void SendFrameToRemote([UsedImplicitly] PlayerID player, ulong serverTick, ulong baselineTick, ulong inputAck, bool fullFrame, bool hasInputMargin, PackedInt inputMargin, BitPackerWithLength delta)
        {
            HandleFrameFromServer(serverTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, delta);
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SendFrameToRemoteReliable([UsedImplicitly] PlayerID player, ulong serverTick, ulong baselineTick, ulong inputAck, bool fullFrame, bool hasInputMargin, PackedInt inputMargin, BitPackerWithLength delta)
        {
            HandleFrameFromServer(serverTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, delta);
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SendFrameToRemoteWithInterest([UsedImplicitly] PlayerID player, ulong serverTick,
            ulong baselineTick, ulong inputAck, bool fullFrame, bool hasInputMargin, PackedInt inputMargin,
            BitPackerWithLength controls, BitPackerWithLength delta)
        {
            HandleInterestControls(serverTick, controls);
            HandleFrameFromServer(serverTick, baselineTick, inputAck, fullFrame, hasInputMargin, inputMargin, delta);
        }

        private void HandleInterestControls(ulong serverTick, BitPackerWithLength controls)
        {
            controls.packer.SkipBytes(controls.originalLength);
            controls.packer.ResetPositionAndMode(true);
            PackedUInt count = default;
            Packer<PackedUInt>.Read(controls.packer, ref count);

            for (uint i = 0; i < count.value; i++)
            {
                PredictedObjectID root = default;
                Packer<PredictedObjectID>.Read(controls.packer, ref root);
                byte tier = Packer<byte>.Read(controls.packer);
                interest?.ReceiveLocalTier(root, tier, serverTick);
            }

            controls.packer.Dispose();
        }

        private void HandleFrameFromServer(ulong serverTick, ulong baselineTick, ulong inputAck, bool fullFrame, bool hasInputMargin, PackedInt inputMargin, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);

            if (inputAck > _inputAckTick)
                _inputAckTick = inputAck;

            if (hasInputMargin && serverTick >= _frameInputMarginTick)
            {
                _frameInputMargin = inputMargin;
                _frameInputMarginTick = serverTick;
                _hasFrameInputMargin = true;
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
                fullFrame = fullFrame
            });
        }

        private void RollbackToFrame(BitPacker frame, ulong stateTick, ulong inputTick, ulong baselineTick, ulong serverTick)
        {
            frame.ResetPositionAndMode(true);

            ReadInputHistory(frame, serverTick);

            if (_verifiedServerTick > 0 && serverTick > _verifiedServerTick + 1)
            {
                RollbackAllToVerified(_verifiedServerTick + 1);

                for (ulong t = _verifiedServerTick + 1; t < serverTick; t++)
                    SimulateFrame(t, HistorySaveMode.Full);

                SaveEnteringState(serverTick);
            }

            if (Packer<bool>.Read(frame))
                ReadCurrentStateEntries(frame, stateTick, baselineTick, serverTick, false);
            else
                ReadPositionalStateEntries(frame, stateTick, baselineTick, serverTick, false);

            if (Packer<bool>.Read(frame))
                ReadCurrentStateEntries(frame, stateTick, baselineTick, serverTick, true);
            else
                ReadPositionalStateEntries(frame, stateTick, baselineTick, serverTick, true);

            SyncAllLocalInterestSideEffects();
            SyncTransforms();
        }

        private void SyncAllLocalInterestSideEffects()
        {
            if (isServer || interest == null)
                return;

            var ownedRoots = GetLocallyOwnedRoots();

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                SyncLocalInterestSideEffects(system, ownedRoots.Contains(system.rootObjectId));
            }
        }

        private void ClearAllLocalInterestSideEffects()
        {
            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                system.UpdateLocalRelevance(true);
                system.SetInterestPolicyOverride(null);
                system.SetInterestSendIntervalTicks(1);
            }
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
        private ulong _pauseAdvanceTicks;
        private ulong _latestFrameServerTick;
        private bool _inputStarved;

        private const ulong MinLead = 2;
        private const ulong TargetLead = 3;
        private const ulong AbsoluteMaxLead = MaxInputWindow - 2;

        private const long InputMarginClamp = 64;
        private const long InputMarginLow = 1;
        private const long InputMarginTarget = 3;
        private const long InputMarginHigh = 6;
        private const long MaxLeadAdjustPerFrame = 16;

        private long _frameInputMargin;
        private ulong _frameInputMarginTick;
        private bool _hasFrameInputMargin;
        private ulong _leadAdjustGateTick;

        private void AdjustLeadFromInputMargin()
        {
            if (!_hasFrameInputMargin)
                return;

            long sampleInputTick = (long)_frameInputMarginTick + _frameInputMargin;
            if (sampleInputTick <= (long)_leadAdjustGateTick)
                return;

            if (_frameInputMargin < InputMarginLow)
            {
                long deficit = InputMarginTarget - _frameInputMargin;
                if (deficit > MaxLeadAdjustPerFrame)
                    deficit = MaxLeadAdjustPerFrame;

                localTick += (ulong)deficit;
                localTickInContext = localTick;
                _pauseAdvanceTicks = 0;
                _leadAdjustGateTick = localTick;
                _hasFrameInputMargin = false;
            }
            else if (_frameInputMargin > InputMarginHigh)
            {
                long excess = _frameInputMargin - InputMarginTarget;
                if (excess > MaxLeadAdjustPerFrame)
                    excess = MaxLeadAdjustPerFrame;

                _pauseAdvanceTicks = (ulong)excess;
                _leadAdjustGateTick = localTick;
                _hasFrameInputMargin = false;
            }
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
            if (cachedIsServer || _deltas.Count == 0)
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

            NotifyReplayStart();

            try
            {
                bool applied = false;
                bool firstContact = _latestFrameServerTick == 0;

                while (_deltas.Count > 0)
                {
                    using var frame = _deltas.Dequeue();

                    if (frame.serverTick > _latestFrameServerTick)
                    {
                        _latestFrameServerTick = frame.serverTick;
                        _inputStarved = frame.serverTick > frame.inputAck + MaxInputWindow;
                    }

                    if (frame.serverTick <= _verifiedServerTick)
                        continue;

                    isVerified = true;

                    if (frame.fullFrame)
                    {
                        ReadFullFrame(frame.packer, frame.serverTick, frame.serverTick, frame.serverTick);
                        SimulateFrame(frame.serverTick, HistorySaveMode.VerifiedFrame);
                        SaveEnteringState(frame.serverTick + 1);
                        _verifiedServerTick = frame.serverTick;
                    }
                    else
                    {
                        RollbackToFrame(frame.packer, frame.serverTick, frame.serverTick, frame.baselineTick, frame.serverTick);

                        if (_validateDeterministicData)
                        {
                            for (var i = 0; i < _systemsCount; i++)
                            {
                                if (_systems[i].isDeterministic && IsLocallyRelevant(_systems[i]))
                                    _systems[i].ValidateDeterministicState(frame.serverTick);
                            }
                        }

                        SimulateFrame(frame.serverTick, HistorySaveMode.VerifiedFrame);
                        SaveEnteringState(frame.serverTick + 1);
                        _verifiedServerTick = frame.serverTick;
                    }

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
                    }
                    else if (lead > AbsoluteMaxLead)
                    {
                        _pauseAdvanceTicks = lead - AbsoluteMaxLead;
                    }

                    SimulateFrame(_verifiedServerTick + 1, HistorySaveMode.Full);
                    ReplayToLatestTick(_verifiedServerTick + 2, HistorySaveMode.None);
                }

                SyncTransforms();
                UpdateInterpolation(true);
            }
            finally
            {
                NotifyReplayEnd();

                isVerified = false;
                isCatchingUpFrames = false;
                isReplaying = false;
                isSimulating = false;
            }

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
                if (system.IsLocallyDormant() ||
                    !system.UsesServerRelayTimeline() ||
                    !system.SkipsCurrentSimulationPhase())
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
            for (ulong simTick = verifiedTick; simTick < localTick; simTick++)
                SimulateFrame(simTick, saveMode);
        }

        private void SimulateFrame(ulong verifiedTick, HistorySaveMode saveMode)
        {
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

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunPrepareSimulationInputs(verifiedTick, delta);
            }

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

            DoPhysicsPass();

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

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].RunPostSimulate();

            for (var j = 0; j < _systemsCount; j++)
                _systems[j].RunGetLatestUnityState();

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
            public readonly Dictionary<ulong, InputQueueValue> byTick = new ();
            public int Count => byTick.Count;

            public void Clear()
            {
                foreach (var entry in byTick.Values)
                    entry.inputPacket.Dispose();
                byTick.Clear();
                rawHighestReceivedTick = 0;
            }
        }

        readonly Dictionary<PlayerID, InputQueue> _clientTicks = new ();

        [ServerRpc(requireOwnership: false)]
        private void RequestStateEntryRepair(PredictedComponentID id, RPCInfo info = default)
        {
            if (!_instanceMap.TryGetValue(id, out var requestedIdentity))
                return;

            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var clientFrame = _clientFrames[i];
                if (clientFrame.player != info.sender)
                    continue;

                var identityEstablishment = clientFrame.identityEstablishment;
                if (!identityEstablishment.Invalidate(id))
                    return;

                var rootObjectId = requestedIdentity.rootObjectId;
                for (var systemIndex = 0; systemIndex < _systemsCount; systemIndex++)
                {
                    var system = _systems[systemIndex];
                    if (!system.id.Equals(id) && system.rootObjectId.Equals(rootObjectId))
                        identityEstablishment.Invalidate(system.id);
                }

                return;
            }
        }

        [ServerRpc(requireOwnership: false, channel: Channel.Unreliable, mtuExceeded: MTUBehaviour.Fragment)]
        private void SendInputToServerFragmented(ulong firstTick, uint tickCount, ulong frameAck, BitPacker payload, RPCInfo info = default)
        {
            ReceivedInput(firstTick, tickCount, frameAck, payload, info);
        }

        [ServerRpc(requireOwnership: false, channel: Channel.UnreliableSequenced)]
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
                        ticks.rawHighestReceivedTick = newestTick;
                }

                for (uint i = 0; i < tickCount; i++)
                {
                    PackedUInt blockBits = default;
                    Packer<PackedUInt>.Read(payload, ref blockBits);
                    PackedUInt count = default;
                    Packer<PackedUInt>.Read(payload, ref count);

                    ulong tick = firstTick + i;

                    bool tooOld = tick < localTick || tick <= ticks.lastConsumedTick;
                    bool tooFar = tick > localTick + MaxInputWindow * 2;

                    if (tooOld || tooFar || ticks.byTick.ContainsKey(tick))
                    {
                        payload.SkipBits((int)blockBits.value);
                        continue;
                    }

                    var slice = BitPackerPool.Get();
                    slice.WriteBits(payload, (int)blockBits.value);
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

        internal bool IsLocallyRelevant(PredictedIdentity system)
        {
            if (cachedIsServer || !system || system.rootObjectId.instanceId.value == 1)
                return true;
            if (isSpawned && system.owner == localPlayer)
                return true;
            return interest == null || interest.IsLocallyRelevant(system.rootObjectId);
        }

        private readonly HashSet<PredictedObjectID> _ownedRootsCache = new ();
        private bool _ownedRootsCacheValid;
        private PlayerID? _ownedRootsCachePlayer;

        private HashSet<PredictedObjectID> GetLocallyOwnedRoots()
        {
            var player = isSpawned ? localPlayer : null;
            if (_ownedRootsCacheValid && Nullable.Equals(_ownedRootsCachePlayer, player))
                return _ownedRootsCache;

            _ownedRootsCache.Clear();
            _ownedRootsCachePlayer = player;
            _ownedRootsCacheValid = true;

            if (player.HasValue)
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (system.owner == player)
                        _ownedRootsCache.Add(system.rootObjectId);
                }
            }

            return _ownedRootsCache;
        }

        private void InvalidateLocallyOwnedRoots()
        {
            _ownedRootsCacheValid = false;
        }

        internal void SyncLocalInterestSideEffects(PredictedIdentity system)
        {
            if (!system)
                return;

            SyncLocalInterestSideEffects(system, IsRootLocallyOwned(system.rootObjectId));
        }

        private void SyncLocalInterestSideEffects(PredictedIdentity system, bool rootOwned)
        {
            var root = system.rootObjectId;
            bool exempt = isServer || root.instanceId.value == 1 || rootOwned;
            bool relevant = exempt || interest == null || interest.IsLocallyRelevant(root);
            system.UpdateLocalRelevance(relevant);
            PredictionPolicy? policyOverride = null;
            if (!exempt && interest != null)
                policyOverride = interest.GetLocalSuggestedPolicy(root);
            system.SetInterestPolicyOverride(policyOverride);
            system.SetInterestSendIntervalTicks(exempt || interest == null
                ? 1
                : interest.GetLocalSendIntervalTicks(root));
        }

        private bool IsRootLocallyOwned(PredictedObjectID root)
        {
            return GetLocallyOwnedRoots().Contains(root);
        }

        private bool ShouldSerializeIdentity(
            PlayerID player,
            PredictedIdentity system,
            InterestControlState interestControls,
            ulong baselineTick,
            bool forceAllRelevant)
        {
            if (interest == null || system.owner == player)
                return true;

            var root = system.rootObjectId;
            if (root.instanceId.value == 1)
                return true;

            bool force = forceAllRelevant ||
                         interestControls != null && interestControls.RequiresAbsolute(root, baselineTick);
            return interest.ShouldSerializeState(player, root, localTick, force);
        }

        private bool[] _stateSerializeFlags = Array.Empty<bool>();
        private bool[] _inputSerializeFlags = Array.Empty<bool>();

        private void FillSerializeFlags(
            PlayerID receiver,
            InterestControlState interestControls,
            ulong baselineTick,
            bool forceAllRelevant)
        {
            if (_stateSerializeFlags.Length < _systemsCount)
                _stateSerializeFlags = new bool[Mathf.NextPowerOfTwo(_systemsCount)];

            for (var i = 0; i < _systemsCount; i++)
                _stateSerializeFlags[i] = ShouldSerializeIdentity(
                    receiver,
                    _systems[i],
                    interestControls,
                    baselineTick,
                    forceAllRelevant);
        }

        private void FillInputSerializeFlags(PlayerID receiver)
        {
            if (_inputSerializeFlags.Length < _systemsCount)
                _inputSerializeFlags = new bool[Mathf.NextPowerOfTwo(_systemsCount)];

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                _inputSerializeFlags[i] = interest == null || system.owner == receiver ||
                                          system.rootObjectId.instanceId.value == 1 ||
                                          interest.IsRelevant(receiver, system.rootObjectId);
            }
        }

        internal void QueueInterestTierChange(PlayerID player, PredictedObjectID root, byte tier)
        {
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                if (frame.player != player)
                    continue;

                frame.interestControls ??= new InterestControlState();
                frame.interestControls.Queue(root, tier);
                _clientFrames[i] = frame;
                return;
            }
        }

        internal void RemoveInterestRoot(PredictedObjectID root)
        {
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                frame.sendBaselines?.Reset(root);
                frame.interestControls?.Remove(root);
            }
        }

        internal void HandleLocalInterestChanged(PredictedObjectID root)
        {
            bool rootOwned = IsRootLocallyOwned(root);
            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.rootObjectId.Equals(root))
                    SyncLocalInterestSideEffects(system, rootOwned);
            }
        }

        internal void HandleLocalOwnershipChanged(PredictedIdentity system)
        {
            if (!system)
                return;

            InvalidateLocallyOwnedRoots();
            HandleLocalInterestChanged(system.rootObjectId);
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
