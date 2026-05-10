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
    public abstract partial class PredictedIdentity
    {
        private readonly List<PredictedModule> _modules = new();
        internal IReadOnlyList<PredictedModule> Modules => _modules;

        private int _staticModuleCount = -1;
        private bool _isApplyingModuleDiff;
        private History<DisposableList<uint>> _moduleHistory;

        private readonly struct DynamicModulesKey : IStableHashable
        {
            private readonly SceneID _scene;
            private readonly PredictedComponentID _id;

            public DynamicModulesKey(SceneID scene, PredictedComponentID id)
            {
                _scene = scene;
                _id = id;
            }

            public uint GetStableHash()
            {
                const uint Off = 2166136261u;
                const uint Pri = 16777619u;
                uint h = Off;
                h = (h ^ 0xD9B61EA1u) * Pri;
                h = (h ^ _id.componentId.value) * Pri;
                h = (h ^ _id.objectId.instanceId.value) * Pri;
                h = (h ^ _scene.id.value) * Pri;
                return h;
            }
        }

        private DynamicModulesKey DynamicKey => new(sceneId, id);

        protected void ModuleSetup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            if (_moduleHistory == null)
                _moduleHistory = new History<DisposableList<uint>>(world.tickRate * 10);

            for (int i = 0; i < _modules.Count; i++)
                _modules[i].SetupInternal(this, world);
        }

        public T RegisterModule<T>(T module) where T : PredictedModule
        {
            if (_isApplyingModuleDiff)
                return module;

            bool isDynamic = predictionManager && predictionManager.isSimulating;

            if (isDynamic && _staticModuleCount < 0)
                _staticModuleCount = _modules.Count;

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

            int dynamicCount = _staticModuleCount < 0 ? 0 : _modules.Count - _staticModuleCount;
            var snapshot = DisposableList<uint>.Create(dynamicCount);
            for (int i = 0; i < dynamicCount; i++)
                snapshot.Add(_modules[_staticModuleCount + i].typeHash);

            _moduleHistory.Write(tick, snapshot);
        }

        internal void RollbackDynamicModules(ulong tick)
        {
            if (!HasDynamicModulesOrHistory())
                return;

            if (!_moduleHistory.ReadOrPrevious(tick, out var target))
            {
                TearDownAllDynamic();
                return;
            }

            ApplyDynamicHashList(target);
        }

        internal void ReadDynamicModuleSnapshot(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            bool senderHasDynamics = Packer<bool>.Read(packer);
            if (!senderHasDynamics)
            {
                if (HasDynamicModulesOrHistory() && _moduleHistory != null)
                {
                    var empty = DisposableList<uint>.Create(0);
                    _moduleHistory.Write(tick, empty);
                    ApplyDynamicHashList(empty);
                }
                return;
            }

            DisposableList<uint> incoming = default;
            deltaModule.ReadReliable(packer, DynamicKey, ref incoming);

            if (_moduleHistory == null)
                return;

            var owned = incoming.list != null ? incoming.Duplicate() : DisposableList<uint>.Create(0);
            _moduleHistory.Write(tick, owned);
            ApplyDynamicHashList(owned);
        }

        internal bool WriteDynamicModuleSnapshot(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
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

                return deltaModule.WriteReliable(packer, receiver, DynamicKey, snapshot);
            }
            finally
            {
                snapshot.Dispose();
            }
        }

        internal void WriteFirstDynamicModuleSnapshot(ulong tick, BitPacker packer)
        {
            if (!HasDynamicModulesOrHistory())
            {
                Packer<bool>.Write(packer, false);
                return;
            }

            Packer<bool>.Write(packer, true);

            if (_moduleHistory != null && tick > 0 && _moduleHistory.ReadOrPrevious(tick, out var historical))
            {
                Packer<DisposableList<uint>>.Write(packer, historical);
                return;
            }

            int dynamicCount = _staticModuleCount < 0 ? 0 : _modules.Count - _staticModuleCount;
            var snapshot = DisposableList<uint>.Create(dynamicCount);
            try
            {
                for (int i = 0; i < dynamicCount; i++)
                    snapshot.Add(_modules[_staticModuleCount + i].typeHash);
                Packer<DisposableList<uint>>.Write(packer, snapshot);
            }
            finally
            {
                snapshot.Dispose();
            }
        }

        internal void ReadFirstDynamicModuleSnapshot(ulong tick, BitPacker packer)
        {
            bool senderHasDynamics = Packer<bool>.Read(packer);
            if (!senderHasDynamics)
            {
                if (HasDynamicModulesOrHistory() && _moduleHistory != null)
                {
                    var empty = DisposableList<uint>.Create(0);
                    _moduleHistory.Write(tick, empty);
                    ApplyDynamicHashList(empty);
                }
                return;
            }

            DisposableList<uint> incoming = default;
            Packer<DisposableList<uint>>.Read(packer, ref incoming);

            if (_moduleHistory == null)
            {
                if (incoming.list != null) incoming.Dispose();
                return;
            }

            _moduleHistory.Write(tick, incoming);
            ApplyDynamicHashList(incoming);
        }

        internal void ClearFutureDynamicModules(ulong tick)
        {
            _moduleHistory?.ClearFuture(tick);
        }

        private void TearDownAllModules()
        {
            for (int i = _modules.Count - 1; i >= 0; i--)
                _modules[i].OnRemovedInternal();
            _modules.Clear();
            _moduleHistory?.Clear();
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

        private void InstantiateDynamicAt(uint typeHash, int absoluteIndex)
        {
            if (!Hasher.TryGetType(typeHash, out var type))
            {
                PurrLogger.LogError($"Dynamic module reconcile failed. Type with hash {typeHash} is not registered.");
                return;
            }

            var ctor = ResolveModuleConstructor(type);
            if (ctor == null)
            {
                PurrLogger.LogError($"Dynamic module reconcile failed to construct '{type.Name}'. Module must expose a public constructor whose first parameter is PredictedIdentity (any additional parameters must be optional).");
                return;
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
                return;
            }

            _modules.Insert(absoluteIndex, module);
            module.moduleIndex = absoluteIndex;

            if (predictionManager)
                module.SetupInternal(this, predictionManager);
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

        protected bool WriteModules(PlayerID receiver, BitPacker packer, DeltaModule deltaModule)
        {
            bool didWriteAny = false;
            for (int i = 0; i < _modules.Count; i++)
            {
                didWriteAny |= _modules[i].WriteStateInternal(receiver, packer, deltaModule);
            }
            return didWriteAny;
        }

        protected void ReadModules(ulong tick, BitPacker packer, DeltaModule deltaModule)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].ReadStateInternal(tick, packer, deltaModule);
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

        protected void ReadFirstStateModules(ulong tick, BitPacker packer)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                _modules[i].ReadFirstStateInternal(tick, packer);
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
