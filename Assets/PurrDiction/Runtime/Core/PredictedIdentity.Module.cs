using System;
using System.Collections.Generic;
using System.Reflection;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Thrown when a peer decodes a full state whose static module roster differs from its own.
    /// Static modules (registered during ModuleSetup or LateAwake) frame the state payload, so
    /// they must be registered identically on every peer; this cannot be reconciled at runtime.
    /// </summary>
    public sealed class PredictedModuleRosterMismatchException : InvalidOperationException
    {
        public PredictedModuleRosterMismatchException(string message) : base(message) { }
    }

    public abstract partial class PredictedIdentity
    {
        private readonly List<PredictedModule> _modules = new();

        private History<DisposableList<uint>> _moduleSetVerified;

        private History<DisposableList<uint>> moduleSetVerified
            => _moduleSetVerified ??= predictionManager.GetVerifiedHistory<DisposableList<uint>>(id, out _);

        private void StoreVerifiedModuleSet(ulong serverTick, in DisposableList<uint> snapshot)
        {
            var store = moduleSetVerified;
            store.PruneByTickWindow(serverTick);

            int lastIndex = store.Count - 1;
            if (lastIndex >= 0 && store.GetEntryTick(lastIndex) <= serverTick)
            {
                var latest = store[lastIndex];
                if (latest.Equals(snapshot))
                    return;
            }

            store.Write(serverTick, snapshot.isDisposed ? DisposableList<uint>.Create(0) : snapshot.Duplicate());
        }

        private void StoreEmptyVerifiedModuleSet(ulong serverTick)
        {
            var empty = DisposableList<uint>.Create(0);
            try
            {
                StoreVerifiedModuleSet(serverTick, in empty);
            }
            finally
            {
                empty.Dispose();
            }
        }

        /// <summary>
        /// The live, ordered list of modules attached to this identity.
        /// Static modules come first, followed by dynamic modules in registration order.
        /// </summary>
        public IReadOnlyList<PredictedModule> modules => _modules;

        /// <summary>
        /// Returns the first module of type T attached to this identity, if any.
        /// </summary>
        public bool TryGetModule<T>(out T module) where T : PredictedModule
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_modules[i] is T match)
                {
                    module = match;
                    return true;
                }
            }
            module = null;
            return false;
        }

        /// <summary>
        /// Appends every module of type T attached to this identity to the given list, in registration order.
        /// </summary>
        public void GetModules<T>(List<T> results) where T : PredictedModule
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_modules[i] is T match)
                    results.Add(match);
            }
        }

        private int _staticModuleCount = -1;
        private bool _isApplyingModuleDiff;
        private bool _isRunningInitialModuleSetup;
        private History<DisposableList<uint>> _moduleHistory;

        private History<DisposableList<uint>> moduleHistory =>
            _moduleHistory ??= new History<DisposableList<uint>>(predictionManager.tickRate * 10);

        private List<PredictedModule> _softCorrectionLiveModules;
        private List<PredictedModule> _softCorrectionTemporaryModules;

        private void BeginInitialModuleSetup()
        {
            _isRunningInitialModuleSetup = true;
        }

        private void EndInitialModuleSetup()
        {
            _isRunningInitialModuleSetup = false;
        }

        protected void ModuleSetup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            ModuleSetup(world);
        }

        private void ModuleSetup(PredictionManager world)
        {
            if (_moduleHistory != null && _moduleHistory.Capacity != world.tickRate * 10)
            {
                _moduleHistory.Clear();
                _moduleHistory = null;
            }

            for (int i = 0; i < _modules.Count; i++)
                _modules[i].SetupInternal(this, world);
        }

        public T RegisterModule<T>(T module) where T : PredictedModule
        {
            if (_isApplyingModuleDiff)
                return module;

            bool isDynamic = predictionManager && predictionManager.isSimulating && !_isRunningInitialModuleSetup;

            if (isDynamic && _staticModuleCount < 0)
                _staticModuleCount = _modules.Count;

            module.registeredAtTick = isDynamic ? predictionManager.localTickInContext : 0;
            module.moduleIndex = _modules.Count;
            _modules.Add(module);

            if (predictionManager)
                module.SetupInternal(this, predictionManager);
            return module;
        }

        internal void RemoveModuleInternal(PredictedModule module)
        {
            if (_isApplyingModuleDiff)
                return;

            int index = _modules.IndexOf(module);
            if (index < 0)
                return;

            int staticCount = _staticModuleCount < 0 ? _modules.Count : _staticModuleCount;
            if (index < staticCount)
            {
                PurrLogger.LogError($"Cannot remove static module '{module.GetType().Name}'. Modules registered before the first simulation tick are not removable.");
                return;
            }

            module.OnRemovedInternal();
            _modules.RemoveAt(index);
            ReindexModulesFrom(index);
        }

        private void ReindexModulesFrom(int from)
        {
            for (int i = from; i < _modules.Count; i++)
                _modules[i].moduleIndex = i;
        }

        private bool HasDynamicModulesOrHistory()
        {
            return _staticModuleCount >= 0 || (_moduleHistory != null && _moduleHistory.Count > 0);
        }

        internal void SaveDynamicModuleSnapshot(ulong tick)
        {
            if (!HasDynamicModulesOrHistory())
                return;

            var history = moduleHistory;
            history.PruneByTickWindow(tick);

            int dynamicCount = _staticModuleCount < 0 ? 0 : _modules.Count - _staticModuleCount;

            if (history.Count > 0)
            {
                var last = history[^1];
                if (!last.isDisposed && last.Count == dynamicCount)
                {
                    bool unchanged = true;
                    for (int i = 0; i < dynamicCount; i++)
                    {
                        if (last[i] != _modules[_staticModuleCount + i].typeHash)
                        {
                            unchanged = false;
                            break;
                        }
                    }

                    if (unchanged)
                        return;
                }
            }

            var snapshot = DisposableList<uint>.Create(dynamicCount);
            for (int i = 0; i < dynamicCount; i++)
                snapshot.Add(_modules[_staticModuleCount + i].typeHash);

            history.Write(tick, snapshot);
        }

        internal void RollbackDynamicModules(ulong tick)
        {
            if (!HasDynamicModulesOrHistory())
                return;

            if (!moduleHistory.ReadOrPrevious(tick, out var target))
            {
                TearDownAllDynamic();
                return;
            }

            ApplyDynamicHashList(target);
        }

        internal void ReadDynamicModuleSnapshot(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            _moduleHistory?.PruneByTickWindow(tick);

            bool senderHasDynamics = Packer<bool>.Read(packer);
            if (!senderHasDynamics)
            {
                if (_moduleSetVerified != null || HasDynamicModulesOrHistory())
                    StoreEmptyVerifiedModuleSet(serverTick);
                ApplyEmptyDynamicModuleSnapshot(tick, UsesSoftCorrectionTimeline());
                return;
            }

            if (!moduleSetVerified.ReadOrPrevious(baselineTick, out var baseline))
                baseline = default;

            DisposableList<uint> incoming = default;
            DeltaPacker<DisposableList<uint>>.Read(packer, baseline, ref incoming);

            StoreVerifiedModuleSet(serverTick, in incoming);

            var owned = !incoming.isDisposed ? incoming : DisposableList<uint>.Create(0);
            moduleHistory.Write(tick, owned);
            ApplyDynamicModuleSnapshotForRead(tick, owned, UsesSoftCorrectionTimeline());
        }

        internal void ReadUnchangedDynamicModuleSnapshot(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            _moduleHistory?.PruneByTickWindow(tick);

            bool hasDynamics = HasDynamicModulesOrHistory();
            if (!hasDynamics && _moduleSetVerified == null)
                return;

            if (!moduleSetVerified.ReadOrPrevious(baselineTick, out var baseline))
            {
                if (hasDynamics)
                {
                    throw new InvalidOperationException(
                        $"Missing dynamic module topology baseline at tick {baselineTick}.");
                }

                StoreEmptyVerifiedModuleSet(serverTick);
                ApplyEmptyDynamicModuleSnapshot(tick, UsesSoftCorrectionTimeline());
                return;
            }

            StoreVerifiedModuleSet(serverTick, in baseline);
            if (!hasDynamics)
                return;

            var owned = baseline.isDisposed
                ? DisposableList<uint>.Create(0)
                : baseline.Duplicate();
            moduleHistory.Write(tick, owned);
            ApplyDynamicModuleSnapshotForRead(
                tick,
                owned,
                UsesSoftCorrectionTimeline());
        }

        internal bool WriteDynamicModuleSnapshot(PlayerID receiver, BitPacker packer, ulong baselineTick)
        {
            if (!HasDynamicModulesOrHistory())
            {
                Packer<bool>.Write(packer, false);
                return false;
            }

            Packer<bool>.Write(packer, true);

            int dynamicCount = _staticModuleCount < 0 ? 0 : _modules.Count - _staticModuleCount;
            var snapshot = DisposableList<uint>.Create(dynamicCount);
            try
            {
                for (int i = 0; i < dynamicCount; i++)
                    snapshot.Add(_modules[_staticModuleCount + i].typeHash);

                var store = moduleSetVerified;
                ulong tick = predictionManager.localTick;
                if (store.Count == 0 || store.MostRecentTick < tick)
                    StoreVerifiedModuleSet(tick, in snapshot);

                if (baselineTick > 0 && store.MostRecentTick <= baselineTick)
                {
                    Packer<bool>.Write(packer, false);
                    return false;
                }

                if (!store.ReadOrPrevious(baselineTick, out var baseline))
                    baseline = default;

                return DeltaPacker<DisposableList<uint>>.Write(packer, baseline, snapshot);
            }
            finally
            {
                snapshot.Dispose();
            }
        }

        private void GetStaticModuleRoster(out int count, out uint hash)
        {
            count = _staticModuleCount < 0 ? _modules.Count : _staticModuleCount;
            hash = 2166136261u;

            for (int i = 0; i < count; i++)
            {
                hash ^= _modules[i].typeHash;
                hash *= 16777619u;
            }
        }

        private string DescribeStaticModuleRoster()
        {
            GetStaticModuleRoster(out int count, out _);

            if (count == 0)
                return "none";

            var names = new string[count];
            for (int i = 0; i < count; i++)
                names[i] = _modules[i].GetType().Name;

            return string.Join(", ", names);
        }

        private void WriteStaticModuleRosterHeader(BitPacker packer)
        {
            GetStaticModuleRoster(out int count, out uint hash);
            Packer<PackedUInt>.Write(packer, (uint)count);
            if (count > 0)
                Packer<uint>.Write(packer, hash);
        }

        private void ValidateStaticModuleRosterHeader(BitPacker packer)
        {
            PackedUInt remoteCount = default;
            Packer<PackedUInt>.Read(packer, ref remoteCount);

            uint remoteHash = 0;
            if (remoteCount.value > 0)
                remoteHash = Packer<uint>.Read(packer);

            GetStaticModuleRoster(out int localCount, out uint localHash);

            if (remoteCount.value == (uint)localCount &&
                (remoteCount.value == 0 || remoteHash == localHash))
                return;

            throw new PredictedModuleRosterMismatchException(
                $"Static module roster mismatch on '{GetType().Name}' {id}: " +
                $"the sender has {remoteCount.value} static modules (roster hash {remoteHash:X8}), " +
                $"this peer has {localCount} ({DescribeStaticModuleRoster()}). " +
                "Module registration must run identically on every peer; check owner-conditional " +
                "RegisterModule calls (for example isOwner branches in LateAwake).");
        }

        internal void WriteFirstDynamicModuleSnapshot(ulong tick, BitPacker packer)
        {
            WriteStaticModuleRosterHeader(packer);

            if (!HasDynamicModulesOrHistory())
            {
                Packer<bool>.Write(packer, false);
                return;
            }

            Packer<bool>.Write(packer, true);

            if (_moduleHistory != null && tick > 0 && _moduleHistory.ReadOrPrevious(tick, out var historical))
            {
                var seedStore = moduleSetVerified;
                if (seedStore.Count == 0 || seedStore.MostRecentTick < tick)
                    StoreVerifiedModuleSet(tick, in historical);
                Packer<DisposableList<uint>>.Write(packer, historical);
                return;
            }

            int dynamicCount = _staticModuleCount < 0 ? 0 : _modules.Count - _staticModuleCount;
            var snapshot = DisposableList<uint>.Create(dynamicCount);
            try
            {
                for (int i = 0; i < dynamicCount; i++)
                    snapshot.Add(_modules[_staticModuleCount + i].typeHash);

                var store = moduleSetVerified;
                if (store.Count == 0 || store.MostRecentTick < tick)
                    StoreVerifiedModuleSet(tick, in snapshot);

                Packer<DisposableList<uint>>.Write(packer, snapshot);
            }
            finally
            {
                snapshot.Dispose();
            }
        }

        internal void ReadFirstDynamicModuleSnapshot(ulong tick, BitPacker packer, ulong serverTick)
        {
            ValidateStaticModuleRosterHeader(packer);

            _moduleHistory?.PruneByTickWindow(tick);

            bool senderHasDynamics = Packer<bool>.Read(packer);
            if (!senderHasDynamics)
            {
                if (_moduleSetVerified != null || HasDynamicModulesOrHistory())
                    StoreEmptyVerifiedModuleSet(serverTick);
                ApplyEmptyDynamicModuleSnapshot(tick);
                return;
            }

            DisposableList<uint> incoming = default;
            Packer<DisposableList<uint>>.Read(packer, ref incoming);

            StoreVerifiedModuleSet(serverTick, in incoming);

            moduleHistory.Write(tick, incoming);
            ApplyDynamicHashList(incoming);
        }

        private void ApplyEmptyDynamicModuleSnapshot(ulong tick, bool preserveLiveTopology = false)
        {
            if (!HasDynamicModulesOrHistory())
                return;

            var history = moduleHistory;
            if (history.ReadOrPrevious(tick, out var previous) &&
                !previous.isDisposed && previous.Count == 0)
            {
                ApplyDynamicModuleSnapshotForRead(tick, previous, preserveLiveTopology);
                return;
            }

            var empty = DisposableList<uint>.Create(0);
            history.Write(tick, empty);
            ApplyDynamicModuleSnapshotForRead(tick, empty, preserveLiveTopology);
        }

        private void ApplyDynamicModuleSnapshotForRead(
            ulong tick,
            DisposableList<uint> target,
            bool preserveLiveTopology)
        {
            if (preserveLiveTopology)
                BeginSoftCorrectionDynamicModuleRead(tick, target);
            else
                ApplyDynamicHashList(target);
        }

        // Borrow the authoritative topology for decoding without disposing later live modules.
        // ReadModules restores the live list after consuming the payload.
        internal void BeginSoftCorrectionDynamicModuleRead(ulong tick, DisposableList<uint> target)
        {
            EndSoftCorrectionDynamicModuleRead();

            int staticCount = _staticModuleCount < 0 ? _modules.Count : _staticModuleCount;
            var liveModules = ListPool<PredictedModule>.Instantiate();
            var temporaryModules = ListPool<PredictedModule>.Instantiate();
            liveModules.AddRange(_modules);

            _softCorrectionLiveModules = liveModules;
            _softCorrectionTemporaryModules = temporaryModules;

            try
            {
                if (_modules.Count > staticCount)
                    _modules.RemoveRange(staticCount, _modules.Count - staticCount);

                bool wasApplyingModuleDiff = _isApplyingModuleDiff;
                _isApplyingModuleDiff = true;
                try
                {
                    for (int targetIndex = 0; targetIndex < target.Count; targetIndex++)
                    {
                        uint targetHash = target[targetIndex];
                        PredictedModule module = null;

                        for (int liveIndex = staticCount; liveIndex < liveModules.Count; liveIndex++)
                        {
                            var candidate = liveModules[liveIndex];
                            if (candidate.typeHash != targetHash || candidate.registeredAtTick > tick)
                                continue;

                            bool alreadyClaimed = false;
                            for (int readIndex = staticCount; readIndex < _modules.Count; readIndex++)
                            {
                                if (!ReferenceEquals(_modules[readIndex], candidate))
                                    continue;

                                alreadyClaimed = true;
                                break;
                            }

                            if (alreadyClaimed)
                                continue;

                            module = candidate;
                            _modules.Add(module);
                            module.moduleIndex = _modules.Count - 1;
                            break;
                        }

                        if (module != null)
                            continue;

                        module = InstantiateDynamicAt(targetHash, _modules.Count);
                        if (module == null)
                        {
                            throw new InvalidOperationException(
                                $"Failed to construct authoritative dynamic module with hash {targetHash} for payload decoding.");
                        }

                        temporaryModules.Add(module);
                    }
                }
                finally
                {
                    _isApplyingModuleDiff = wasApplyingModuleDiff;
                }
            }
            catch
            {
                EndSoftCorrectionDynamicModuleRead();
                throw;
            }
        }

        internal void EndSoftCorrectionDynamicModuleRead()
        {
            if (_softCorrectionLiveModules == null)
                return;

            var liveModules = _softCorrectionLiveModules;
            var temporaryModules = _softCorrectionTemporaryModules;
            _softCorrectionLiveModules = null;
            _softCorrectionTemporaryModules = null;
            bool wasApplyingModuleDiff = _isApplyingModuleDiff;
            _isApplyingModuleDiff = true;
            Exception removalException = null;

            try
            {
                if (temporaryModules != null)
                {
                    for (int i = temporaryModules.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            temporaryModules[i].OnRemovedInternal();
                        }
                        catch (Exception e)
                        {
                            removalException ??= e;
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    _modules.Clear();
                    _modules.AddRange(liveModules);
                    ReindexModulesFrom(0);
                }
                finally
                {
                    _isApplyingModuleDiff = wasApplyingModuleDiff;
                    ListPool<PredictedModule>.Destroy(liveModules);
                    if (temporaryModules != null)
                        ListPool<PredictedModule>.Destroy(temporaryModules);
                }
            }

            if (removalException != null)
                throw removalException;
        }

        internal void ClearFutureDynamicModules(ulong tick)
        {
            _moduleHistory?.ClearFuture(tick);
        }

        private void ResetModulesForReuse(PredictionManager world)
        {
            ResetModulesForPool();
            ModuleSetup(world);
        }

        private void ResetModulesForPool()
        {
            TearDownAllDynamic();
            _moduleHistory?.Clear();
            _staticModuleCount = -1;
        }

        private void ReleaseModuleStateForPool()
        {
            _moduleHistory?.Clear();

            for (int i = 0; i < _modules.Count; i++)
                _modules[i].ReleaseStateForPoolInternal();
        }

        private void TearDownAllModules()
        {
            for (int i = _modules.Count - 1; i >= 0; i--)
                _modules[i].OnRemovedInternal();
            _modules.Clear();
            _moduleHistory?.Clear();
        }

        private void TriggerModuleDestroyedEvents()
        {
            for (int i = _modules.Count - 1; i >= 0; i--)
                _modules[i].TriggerDestroyedEvent();
        }

        private void TearDownAllDynamic()
        {
            if (_staticModuleCount < 0) return;

            _isApplyingModuleDiff = true;
            try
            {
                for (int i = _modules.Count - 1; i >= _staticModuleCount; i--)
                {
                    _modules[i].OnRemovedInternal();
                    _modules.RemoveAt(i);
                }
            }
            finally
            {
                _isApplyingModuleDiff = false;
            }
        }

        private void ApplyDynamicHashList(DisposableList<uint> target)
        {
            int staticCount = _staticModuleCount < 0 ? _modules.Count : _staticModuleCount;
            int currentDynamicCount = _modules.Count - staticCount;

            if (currentDynamicCount == target.Count)
            {
                bool same = true;
                for (int i = 0; i < currentDynamicCount; i++)
                {
                    if (_modules[staticCount + i].typeHash != target[i])
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return;
            }

            var current = DisposableList<uint>.Create(currentDynamicCount);
            for (int i = 0; i < currentDynamicCount; i++)
                current.Add(_modules[staticCount + i].typeHash);

            if (_staticModuleCount < 0 && target.Count > 0)
                _staticModuleCount = _modules.Count;

            staticCount = _staticModuleCount < 0 ? _modules.Count : _staticModuleCount;

            var ops = MyersDiff.Diff(current, target);
            _isApplyingModuleDiff = true;
            try
            {
                int offset = 0;
                for (int i = 0; i < ops.Count; i++)
                {
                    var op = ops[i];
                    switch (op.type)
                    {
                        case OperationType.Add:
                        {
                            for (int j = 0; j < op.values.Count; j++)
                                InstantiateDynamicAt(op.values[j], _modules.Count);
                            offset += op.values.Count;
                            op.values.Dispose();
                            break;
                        }
                        case OperationType.Insert:
                        {
                            int start = staticCount + op.index + offset;
                            for (int j = 0; j < op.values.Count; j++)
                                InstantiateDynamicAt(op.values[j], start + j);
                            offset += op.values.Count;
                            op.values.Dispose();
                            break;
                        }
                        case OperationType.Delete:
                        {
                            int start = staticCount + op.index + offset;
                            for (int j = 0; j < op.length; j++)
                            {
                                var module = _modules[start];
                                module.OnRemovedInternal();
                                _modules.RemoveAt(start);
                            }
                            offset -= op.length;
                            break;
                        }
                        case OperationType.End:
                        default:
                            break;
                    }
                }
            }
            finally
            {
                _isApplyingModuleDiff = false;
                ops.Dispose();
                current.Dispose();
            }

            ReindexModulesFrom(staticCount);
        }

        private static readonly Dictionary<Type, ConstructorInfo> _moduleConstructorCache = new();

        private static ConstructorInfo ResolveModuleConstructor(Type type)
        {
            if (_moduleConstructorCache.TryGetValue(type, out var cached))
                return cached;

            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            ConstructorInfo match = null;
            for (int i = 0; i < ctors.Length; i++)
            {
                var ps = ctors[i].GetParameters();
                if (ps.Length < 1) continue;
                if (!typeof(PredictedIdentity).IsAssignableFrom(ps[0].ParameterType)) continue;

                bool allOptional = true;
                for (int p = 1; p < ps.Length; p++)
                {
                    if (!ps[p].IsOptional) { allOptional = false; break; }
                }
                if (!allOptional) continue;

                match = ctors[i];
                break;
            }

            _moduleConstructorCache[type] = match;
            return match;
        }

        private PredictedModule InstantiateDynamicAt(uint typeHash, int absoluteIndex)
        {
            if (!Hasher.TryGetType(typeHash, out var type))
            {
                PurrLogger.LogError($"Dynamic module reconcile failed. Type with hash {typeHash} is not registered.");
                return null;
            }

            var ctor = ResolveModuleConstructor(type);
            if (ctor == null)
            {
                PurrLogger.LogError($"Dynamic module reconcile failed to construct '{type.Name}'. Module must expose a public constructor whose first parameter is PredictedIdentity (any additional parameters must be optional).");
                return null;
            }

            var parameters = ctor.GetParameters();
            object[] args;
            if (parameters.Length == 1)
            {
                args = new object[] { this };
            }
            else
            {
                args = new object[parameters.Length];
                args[0] = this;
                for (int i = 1; i < parameters.Length; i++)
                    args[i] = Type.Missing;
            }

            PredictedModule module;
            try
            {
                module = (PredictedModule)ctor.Invoke(args);
            }
            catch (Exception e)
            {
                PurrLogger.LogError($"Dynamic module reconcile failed to construct '{type.Name}': {e.Message}");
                return null;
            }

            _modules.Insert(absoluteIndex, module);
            module.moduleIndex = absoluteIndex;

            try
            {
                if (predictionManager)
                    module.SetupInternal(this, predictionManager);
                return module;
            }
            catch
            {
                _modules.Remove(module);
                try
                {
                    module.OnRemovedInternal();
                }
                catch (Exception cleanupException)
                {
                    PurrLogger.LogException(cleanupException);
                }
                throw;
            }
        }

        internal void UpdateModuleView(float deltaTime)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].UpdateViewInternal(deltaTime);
        }

        internal void LateUpdateModuleView(float deltaTime)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].LateUpdateViewInternal(deltaTime);
        }

        protected void SimulateModules(ulong tick, float delta)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].SimulateInternal(tick, delta);
        }

        protected void LateSimulateModules(float delta)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].LateSimulateInternal(delta);
        }

        protected void RollbackModules(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].RollbackInternal(tick);
        }

        protected void SaveModulesState(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].SaveStateInternal(tick);
        }

        private bool HasUnchangedStateBaselineModules(ulong baselineTick)
        {
            if (HasDynamicModulesOrHistory() &&
                !moduleSetVerified.ReadOrPrevious(baselineTick, out _))
            {
                return false;
            }

            for (int i = 0; i < _modules.Count; i++)
            {
                if (!_modules[i].HasUnchangedStateBaselineInternal(baselineTick))
                    return false;
            }

            return true;
        }

        protected bool WriteModules(PlayerID receiver, BitPacker packer, ulong baselineTick)
        {
            bool didWriteAny = false;
            for (int i = 0; i < _modules.Count; i++)
            {
                didWriteAny |= _modules[i].WriteStateInternal(receiver, packer, baselineTick);
            }
            return didWriteAny;
        }

        private void ReadUnchangedModules(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            try
            {
                for (int i = 0; i < _modules.Count; i++)
                {
                    var module = _modules[i];
                    if (!module.HasUnchangedStateBaselineInternal(baselineTick))
                    {
                        throw new InvalidOperationException(
                            $"Module {module.GetType().Name} has no acknowledged baseline " +
                            $"for omitted state at tick {baselineTick}.");
                    }

                    module.ReadUnchangedStateInternal(tick, baselineTick, serverTick);
                }
            }
            finally
            {
                EndSoftCorrectionDynamicModuleRead();
            }
        }

        protected void ReadModules(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            try
            {
                for (int i = 0; i < _modules.Count; i++)
                {
                    _modules[i].ReadStateInternal(tick, packer, baselineTick, serverTick);
                }
            }
            finally
            {
                EndSoftCorrectionDynamicModuleRead();
            }
        }

        protected void UpdateModulesInterpolation(float delta, bool accumulateError)
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].UpdateInterpolationInternal(delta, accumulateError);
        }

        protected void ResetModulesInterpolation()
        {
            for (int i = 0; i < _modules.Count; i++) _modules[i].ResetInterpolationInternal();
        }

        protected void WriteFirstStateModules(ulong tick, BitPacker packer)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].WriteFirstStateInternal(tick, packer);
            }
        }

        protected void ReadFirstStateModules(ulong tick, BitPacker packer, ulong serverTick)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].ReadFirstStateInternal(tick, packer, serverTick);
            }
        }

        protected void ClearFutureModules(ulong tick)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].ClearFutureInternal(tick);
            }
        }
    }
}
