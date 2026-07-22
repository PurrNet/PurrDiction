using System;
using System.Collections.Generic;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity : MonoBehaviour
    {
        public ulong? lastVerifiedTick { get; internal set; }

        public virtual string GetExtraString()
        {
            return string.Empty;
        }

        protected readonly ProfilerMarker simulateMarker;

        private static readonly Dictionary<Type, ProfilerMarker> _simulateMarkers = new ();

        protected PredictedIdentity()
        {
            var type = GetType();
            if (!_simulateMarkers.TryGetValue(type, out simulateMarker))
            {
                simulateMarker = new ProfilerMarker($"{type.Name}.Simulate");
                _simulateMarkers.Add(type, simulateMarker);
            }
        }

        public PredictionManager predictionManager { get; protected set; }

        /// <summary>
        /// Represents the identifier of the owner associated with this object.
        /// Used to track ownership, enabling control over inputs.
        /// </summary>
        public PlayerID? owner;

        /// <summary>
        /// The unique identifier for this object.
        /// Can be used to identify the object across the network.
        /// </summary>
        public PredictedComponentID id;

        /// <summary>
        /// Id of the root piece of the spawn instance this component belongs to.
        /// Use this instead of id.objectId when comparing whether two components
        /// come from the same spawned prefab: id.objectId identifies the piece
        /// (the GameObject carrying this component), not the whole instance.
        /// </summary>
        public PredictedObjectID rootObjectId
        {
            get
            {
                if (_hasCachedRootObjectId)
                    return _cachedRootObjectId;

                var manager = predictionManager;
                if (manager && manager.hierarchy && manager.hierarchy.TryGetRootId(id.objectId, out var rootId))
                {
                    _cachedRootObjectId = rootId;
                    _hasCachedRootObjectId = true;
                    return rootId;
                }

                return id.objectId;
            }
        }

        private PredictedObjectID _cachedRootObjectId;
        private bool _hasCachedRootObjectId;

        internal bool isFreshSpawn = true;
        internal bool preservesStateOnSetup { get; private set; }
        private bool _simulateSoftCorrectionDuringReplay;
        private bool _skipReplaySpawnInitialization;

        public virtual bool hasInput => false;

        internal virtual bool isEventHandler => false;

        public virtual bool controlsTransformPolicy => false;

        /// <summary>
        /// True when this non-deterministic identity consumes verified state as a correction target
        /// instead of requiring rollback. Implementations must override OnVerifiedStateReceived and
        /// apply the resulting correction during live simulation. Deterministic identities cannot use
        /// SoftCorrection even if a subclass overrides this property.
        /// </summary>
        public virtual bool supportsSoftCorrection => false;

        [Header("Predicted Identity")]
        [SerializeField, Tooltip("Use the nearest PredictionPolicyScope by default, or explicitly override it for this identity.")]
        private PredictionPolicySource _predictionPolicySource = PredictionPolicySource.UseScope;

        [SerializeField, Tooltip("Local prediction policy used when this identity overrides its scope, or when no PredictionPolicyScope is found.")]
        private PredictionPolicy _predictionPolicy = PredictionPolicy.FullPrediction;

        /// <summary>
        /// How this identity participates in client-side prediction and reconciliation.
        /// See <see cref="PredictionPolicy"/> for the semantics of each mode.
        /// </summary>
        public PredictionPolicy predictionPolicy { get; private set; }

        private PredictionPolicy _lastRegisteredPredictionPolicy;
        private bool _hasLastRegisteredPredictionPolicy;
        private bool _hasPendingSetupPolicyChange;
        private PredictionPolicy _pendingSetupOldPolicy;
        private PredictionPolicy _pendingSetupNewPolicy;
        private bool _isResolvingSetupPredictionPolicy;

        /// <summary>
        /// The configured (serialized) policy, re-applied on every registration including pooled reuse.
        /// Setting this on a spawned identity applies the change immediately via <see cref="SetPredictionPolicy"/>.
        /// </summary>
        public PredictionPolicy configuredPredictionPolicy
        {
            get => _predictionPolicy;
            set
            {
                _predictionPolicy = NormalizePredictionPolicy(value, predictionManager);
                if (predictionManager && UsesConfiguredPredictionPolicy())
                    SetPredictionPolicy(ResolvePredictionPolicy());
            }
        }

        public PredictionPolicySource predictionPolicySource
        {
            get => _predictionPolicySource;
            set
            {
                if (_predictionPolicySource == value)
                    return;

                _predictionPolicySource = value;
                RefreshResolvedPredictionPolicy();
            }
        }

        internal bool OverridesPredictionPolicyScope()
            => _predictionPolicySource == PredictionPolicySource.OverrideScope;

        private bool UsesConfiguredPredictionPolicy()
            => OverridesPredictionPolicyScope() || !TryGetPredictionPolicyScope(out _);

        public bool TryGetPredictionPolicyScope(out PredictionPolicyScope scope)
        {
            var current = transform;
            while (current)
            {
                if (current.TryGetComponent(out scope) && scope.isActiveAndEnabled)
                    return true;

                current = current.parent;
            }

            scope = null;
            return false;
        }

        protected virtual PredictionPolicy ResolvePredictionPolicy()
        {
            if (!OverridesPredictionPolicyScope())
            {
                var hasScope = _isResolvingSetupPredictionPolicy
                    ? TryGetPredictionPolicyScopeForSetup(out var scope)
                    : TryGetPredictionPolicyScope(out scope);

                if (hasScope)
                {
                    var policy = _isResolvingSetupPredictionPolicy
                        ? scope.ResolvePolicyForSetup()
                        : scope.ResolvePolicy();
                    return NormalizePredictionPolicy(policy, false);
                }
            }

            return NormalizePredictionPolicy(_predictionPolicy, false);
        }

        protected virtual PredictionPolicy ResolveSetupPredictionPolicy()
        {
            var wasResolvingSetupPolicy = _isResolvingSetupPredictionPolicy;
            _isResolvingSetupPredictionPolicy = true;
            try
            {
                return ResolvePredictionPolicy();
            }
            finally
            {
                _isResolvingSetupPredictionPolicy = wasResolvingSetupPolicy;
            }
        }

        private bool TryGetPredictionPolicyScopeForSetup(out PredictionPolicyScope scope)
        {
            var current = transform;
            while (current)
            {
                if (current.TryGetComponent(out scope) && scope.enabled)
                    return true;

                current = current.parent;
            }

            scope = null;
            return false;
        }

        internal PredictionPolicy ResolvePredictionPolicyForSetup()
            => ResolveSetupPredictionPolicy();

        internal PredictionPolicy previousRegisteredPredictionPolicy
            => _hasLastRegisteredPredictionPolicy
                ? _lastRegisteredPredictionPolicy
                : predictionPolicy;

        internal void RecordCompletedRegistrationPolicy()
        {
            _lastRegisteredPredictionPolicy = predictionPolicy;
            _hasLastRegisteredPredictionPolicy = true;
        }

        /// <summary>
        /// Returns the currently applied policy for a registered identity, or resolves the
        /// configured source for an identity that has not been registered yet.
        /// </summary>
        public PredictionPolicy GetResolvedPredictionPolicy()
            => predictionManager && !isFreshSpawn ? predictionPolicy : ResolvePredictionPolicy();

        internal PredictionPolicy ResolveDelegatedPredictionPolicy()
            => GetResolvedPredictionPolicy();

        internal void RefreshResolvedPredictionPolicy()
        {
            if (predictionManager)
                SetPredictionPolicy(ResolvePredictionPolicy());
        }

        protected virtual void OnTransformParentChanged()
        {
            RefreshResolvedPredictionPolicy();

            var manager = predictionManager;
            if (manager && manager.hierarchy)
                manager.hierarchy.NotifyInstanceParentChanged(gameObject);
        }

        /// <summary>
        /// Configures and applies a persistent local override. Subsequent scope updates do
        /// not replace this policy until <see cref="UsePredictionPolicyScope"/> is called.
        /// </summary>
        public void SetPredictionPolicyOverride(PredictionPolicy policy)
        {
            _predictionPolicy = NormalizePredictionPolicy(policy, predictionManager);
            _predictionPolicySource = PredictionPolicySource.OverrideScope;
            RefreshResolvedPredictionPolicy();
        }

        /// <summary>
        /// Returns this identity to its nearest active scope. If no scope exists, the local
        /// configured policy remains the fallback.
        /// </summary>
        public void UsePredictionPolicyScope()
        {
            _predictionPolicySource = PredictionPolicySource.UseScope;
            RefreshResolvedPredictionPolicy();
        }

        /// <summary>
        /// Changes the currently applied prediction policy at runtime without changing its
        /// configured source. Use <see cref="SetPredictionPolicyOverride"/> for a local policy
        /// that must survive later scope refreshes. Deterministic identities support
        /// <see cref="PredictionPolicy.FullPrediction"/>, <see cref="PredictionPolicy.ServerRelay"/>,
        /// and <see cref="PredictionPolicy.PredictedIfOwned"/>. Switching mid-game is safest at
        /// ownership changes; the next reconcile realigns the identity with its new timeline.
        /// </summary>
        public void SetPredictionPolicy(PredictionPolicy policy)
        {
            policy = NormalizePredictionPolicy(policy, true);

            CancelPendingPredictionPolicySetup();

            if (predictionPolicy == policy)
                return;

            var oldPolicy = predictionPolicy;
            predictionPolicy = policy;
            OnPredictionPolicyChanged(oldPolicy, policy);
            if (predictionManager)
                predictionManager.HandlePredictionPolicyChanged(this, oldPolicy, policy);
        }

        private void PreparePredictionPolicyForSetup(PredictionPolicy policy)
        {
            CancelPendingPredictionPolicySetup();
            policy = NormalizePredictionPolicy(policy, true);

            if (predictionPolicy == policy)
                return;

            _pendingSetupOldPolicy = predictionPolicy;
            _pendingSetupNewPolicy = policy;
            predictionPolicy = policy;
            _hasPendingSetupPolicyChange = true;
        }

        internal void CompletePredictionPolicySetup()
        {
            if (!_hasPendingSetupPolicyChange)
                return;

            var oldPolicy = _pendingSetupOldPolicy;
            var newPolicy = _pendingSetupNewPolicy;
            _hasPendingSetupPolicyChange = false;
            _pendingSetupOldPolicy = default;
            _pendingSetupNewPolicy = default;

            OnPredictionPolicyChanged(oldPolicy, newPolicy);
            if (predictionManager)
                predictionManager.HandlePredictionPolicyChanged(this, oldPolicy, newPolicy);
        }

        internal void CancelPendingPredictionPolicySetup()
        {
            if (!_hasPendingSetupPolicyChange)
                return;

            predictionPolicy = _pendingSetupOldPolicy;
            _hasPendingSetupPolicyChange = false;
            _pendingSetupOldPolicy = default;
            _pendingSetupNewPolicy = default;
        }

        private PredictionPolicy NormalizePredictionPolicy(PredictionPolicy policy, bool log)
        {
            if (policy != PredictionPolicy.SoftCorrection || (!isDeterministic && supportsSoftCorrection))
                return policy;

            if (log)
            {
                var reason = isDeterministic
                    ? "deterministic identities do not receive authoritative state deltas to correct against"
                    : "the identity does not implement verified-state correction";
                Logging.PurrLogger.LogError(
                    $"{GetType().Name} does not support {nameof(PredictionPolicy.SoftCorrection)} because {reason}.",
                    this);
            }

            return PredictionPolicy.FullPrediction;
        }

        protected virtual void OnPredictionPolicyChanged(PredictionPolicy oldPolicy, PredictionPolicy newPolicy) { }

        protected void SyncControlledTransformPolicy(PredictionPolicy policy)
        {
            if (!controlsTransformPolicy)
                return;

            if (TryGetComponent(out PredictedTransform predictedTransform) && predictedTransform != this)
                predictedTransform.SetPredictionPolicy(policy);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal PredictionPolicy EffectivePolicy()
        {
            if (predictionPolicy != PredictionPolicy.PredictedIfOwned)
                return predictionPolicy;
            return IsOwner() ? PredictionPolicy.FullPrediction : PredictionPolicy.ServerRelay;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool UsesSoftCorrectionTimeline()
            => predictionPolicy == PredictionPolicy.SoftCorrection;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool UsesServerRelayTimeline()
            => EffectivePolicy() == PredictionPolicy.ServerRelay;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool UsesFullPredictionTimeline()
            => EffectivePolicy() == PredictionPolicy.FullPrediction;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool TracksEffectivePolicyChanges()
            => predictionPolicy == PredictionPolicy.PredictedIfOwned;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool AccumulatesRollbackInterpolationError()
            => UsesFullPredictionTimeline();

        internal bool IsSoftCorrectionReplaySimulating()
            => _simulateSoftCorrectionDuringReplay;

        internal bool SkipsReplaySpawnInitialization()
            => _skipReplaySpawnInitialization;

        public bool shouldSkipReplaySpawnInitialization => _skipReplaySpawnInitialization;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool SkipsCurrentSimulationPhase()
        {
            var manager = predictionManager;
            if (manager.cachedIsServer)
                return false;

            if (IsLocallyDormant())
                return true;

            if (UsesFullPredictionTimeline())
                return false;

            if (UsesSoftCorrectionTimeline())
                return manager.isReplaying && !_simulateSoftCorrectionDuringReplay;

            return !(manager.isReplaying && manager.isVerified);
        }

        internal virtual void OnReplayStart() { }

        internal virtual void OnReplayEnd() { }

        internal virtual void SyncEffectivePolicySideEffects() { }

        internal virtual void SyncLocalRelevanceSideEffects(bool relevant) { }

        internal void UpdateLocalRelevance(bool relevant)
        {
            _locallyDormant = !relevant;
            SyncLocalRelevanceSideEffects(relevant);
        }

        private bool _locallyDormant;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool IsLocallyDormant()
        {
            return _locallyDormant;
        }

        [UsedByIL]
        public bool IsSimulating()
        {
            return predictionManager.isSimulating;
        }

        public virtual void OnPreSetup() {  }

        internal virtual void OnPrepareSimulationInputs(ulong tick, float delta) {  }

        public virtual void ResetState()
        {
            CancelPendingPredictionPolicySetup();
            isServer = false;
            isFreshSpawn = true;
            preservesStateOnSetup = false;
            _simulateSoftCorrectionDuringReplay = false;
            _skipReplaySpawnInitialization = false;
            _locallyDormant = false;
            _hasCachedRootObjectId = false;
            owner = null;
            id = default;
            ResetModulesForPool();
            OnRemovedFromPool();
        }

        internal void SetPreserveStateOnSetup(bool preserve)
        {
            preservesStateOnSetup = preserve;
        }

        internal void SetSoftCorrectionReplaySimulation(bool simulate)
        {
            _simulateSoftCorrectionDuringReplay = simulate;
        }

        internal void SetSkipReplaySpawnInitialization(bool skip)
        {
            _skipReplaySpawnInitialization = skip;
        }

        internal void SetOwner(PlayerID? player, bool syncPolicySideEffects = true)
        {
            owner = player;
            OnOwnerAssigned(player);

            if (syncPolicySideEffects)
                SyncEffectivePolicySideEffects();
        }

        protected virtual void OnOwnerAssigned(PlayerID? player) { }

        private History<PredictedIdentityState> _metadataVerified;

        private History<PredictedIdentityState> metadataVerified
            => _metadataVerified ??= predictionManager.GetVerifiedHistory<PredictedIdentityState>(id, out _);

        internal void RefreshMetadataLedger(ulong tick, in PredictedIdentityState metadata)
        {
            var store = metadataVerified;
            if (store.Count > 0 && store.MostRecentTick >= tick)
                return;

            StoreVerifiedMetadata(tick, in metadata);
        }

        internal void StoreVerifiedMetadata(ulong serverTick, in PredictedIdentityState metadata)
        {
            var store = metadataVerified;
            store.PruneByTickWindow(serverTick);

            int lastIndex = store.Count - 1;
            if (lastIndex >= 0 && store.GetEntryTick(lastIndex) <= serverTick)
            {
                var latest = store[lastIndex];
                var current = metadata;
                if (Packer.AreEqualRef(ref latest, ref current))
                    return;
            }

            store.Write(serverTick, metadata);
        }

        internal bool WritePredictionMetadata(BitPacker packer, ulong baselineTick, in PredictedIdentityState metadata)
        {
            RefreshMetadataLedger(predictionManager.localTick, in metadata);
            var store = metadataVerified;

            if (baselineTick > 0 && store.MostRecentTick <= baselineTick)
            {
                Packer<bool>.Write(packer, false);
                return false;
            }

            if (!store.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            Packer<bool>.Write(packer, true);
            DeltaPacker<PredictedIdentityState>.Write(packer, baseline, metadata);
            return true;
        }

        internal void ReadPredictionMetadata(BitPacker packer, ulong baselineTick, ulong serverTick, ref PredictedIdentityState metadata)
        {
            bool changed = Packer<bool>.Read(packer);

            if (!metadataVerified.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            if (changed)
            {
                DeltaPacker<PredictedIdentityState>.Read(packer, baseline, ref metadata);
                StoreVerifiedMetadata(serverTick, in metadata);
            }
            else metadata = baseline;
        }

        internal void TriggerOnRemovedFromPool()
        {
            OnRemovedFromPool();
        }

        protected virtual void OnRemovedFromPool() {}

        protected virtual void OnAddedToPool() {}

        protected virtual void LateAwake() {}

        protected virtual void Destroyed() {}

        private bool _destroyedFired;

        internal void TriggerDestroyedEvent()
        {
            if (_destroyedFired)
                return;

            _destroyedFired = true;
            Destroyed();
            TriggerModuleDestroyedEvents();
        }

        public bool isServer { get; private set; }

        public SceneID sceneId { get; private set; }

        internal ulong lastChangedStateTick;

        internal virtual void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            isServer = manager.isServer;
            this.id = id;
            _hasCachedRootObjectId = false;
            _destroyedFired = false;
            predictionManager = world;
            sceneId = world.sceneId;
            lastChangedStateTick = world.localTick + 1;
            _metadataVerified = null;
            _moduleSetVerified = null;
            SetOwner(owner, false);
            PreparePredictionPolicyForSetup(ResolvePredictionPolicyForSetup());

            if (!isFreshSpawn)
            {
                if (preservesStateOnSetup && UsesSoftCorrectionTimeline())
                    ModuleSetup(world);
                else ResetModulesForReuse(world);
                return;
            }

            isFreshSpawn = false;

            BeginInitialModuleSetup();
            try
            {
                ModuleSetup(world);
                LateAwake();
            }
            finally
            {
                EndInitialModuleSetup();
            }
        }

        protected virtual void OnDestroy()
        {
            TriggerDestroyedEvent();
            TearDownAllModules();

            if (predictionManager)
            {
                if (predictionManager.hierarchy && gameObject.scene.isLoaded && IsLastLiveIdentityOnGameObject())
                    predictionManager.hierarchy.NotifyPieceDestroyed(gameObject);

                predictionManager.UnregisterInstance(this);
            }
        }

        private bool IsLastLiveIdentityOnGameObject()
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            gameObject.GetComponents(identities);

            bool last = true;

            for (var i = 0; i < identities.Count; i++)
            {
                if (identities[i] && !ReferenceEquals(identities[i], this))
                {
                    last = false;
                    break;
                }
            }

            ListPool<PredictedIdentity>.Destroy(identities);
            return last;
        }

        public bool isOwner => IsOwner();

        public bool isRelevant => !IsLocallyDormant();

        public bool isController
        {
            get
            {
                if (!predictionManager)
                    return false;

                var player = predictionManager.isSpawned ? predictionManager.localPlayer ?? default : default;
                return IsOwner(player, predictionManager.cachedIsServer);
            }
        }

        public bool IsOwner()
        {
            if (predictionManager && predictionManager.isSpawned && owner == predictionManager.localPlayer)
                return true;
            return false;
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
            {
                if (owner.Value.isBot)
                    return asServer;
                return owner == player;
            }
            return asServer;
        }

        internal abstract void SimulateTick(ulong tick, float delta);

        internal abstract void LateSimulateTick(float delta);

        public virtual void PostSimulate() {}

        internal abstract void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate);

        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(float delta, bool accumulateError);

        public abstract void ResetInterpolation();

        private PlayerID? _lastOwner;

        public virtual bool isDeterministic => false;

        /// <summary>
        /// Called once when owner changes
        /// This is meant to be used for view/visuals only and not part of the simulation
        /// </summary>
        public virtual void OnViewOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner) { }

        internal virtual void UpdateView(float deltaTime)
        {
            if (owner != _lastOwner)
            {
                OnViewOwnerChanged(_lastOwner, owner);
                _lastOwner = owner;
            }
        }

        internal virtual void LateUpdateView(float deltaTime) { }

        internal abstract void GetLatestUnityState();

        internal abstract void WriteFirstState(ulong tick, BitPacker packer);

        internal virtual void WriteAbsoluteState(ulong tick, BitPacker packer)
        {
            WriteFirstState(tick, packer);
        }

        internal virtual void ReadAbsoluteState(ulong tick, BitPacker packer, ulong serverTick)
        {
            ReadFirstState(tick, packer, serverTick);
        }

        internal abstract bool WriteCurrentState(PlayerID receiver, BitPacker packer, ulong baselineTick);

        internal abstract void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick);

        internal abstract void ReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick);

        internal abstract void QueueInput(BitPacker packer, PlayerID sender);

        public GameObject GetRoot()
        {
            var current = transform;

            while (current.parent != null)
            {
                if (current.parent.GetComponent<PredictedIdentity>() == null)
                    break;

                current = current.parent;
            }

            return current.gameObject;
        }

        internal void TriggerOnPooledEvent()
        {
            ReleasePredictionStateForPool();
            OnAddedToPool();
        }

        internal virtual void ReleasePredictionStateForPool()
        {
            ReleaseModuleStateForPool();
        }

        public abstract void WriteFirstInput(ulong localTick, BitPacker packer);

        public abstract void ReadFirstInput(ulong localTick, BitPacker packer);

        internal abstract void ClearFuture(ulong stateTick);

        internal virtual bool HasInputAt(ulong tick) => false;

        internal virtual void ValidateDeterministicState(ulong serverTick) { }
    }
}
