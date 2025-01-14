using System;
using System.Collections.Generic;
using FixMath.NET;
using PurrNet.Logging;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction
{
    [DefaultExecutionOrder(-1000)]
    public class PredictionManager : NetworkIdentity, ITick
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();
        
        static readonly Dictionary<int, PredictionManager> _instances = new ();
        
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
        }

        private ulong _localTick;
        
        public Fix64 tickDelta { get; private set; }

        public int tickRate { get; private set; }
        
        public PredictedHierarchy hierarchy { get; private set; }
        
        private bool _isServer;
        
        protected override void OnSpawned()
        {
            _isServer = networkManager.isServer;
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

        public void OnTick(float delta)
        {
            using var frame = BitPackerPool.Get();
            int count = _systems.Count;
            
            for (var i = 0; i < count; i++)
            {
                // get input
                _systems[i].PreSimulate(_localTick);
                
                // write state before simulation
                _systems[i].WriteState(_localTick, frame, _isServer);
                
                // simulate
                _systems[i].Simulate(_localTick, tickDelta);
            }

            if (!_isServer)
            {
                PurrLogger.Log($"Sending input to server for tick {_localTick}, systems: {count}, packer: {frame.positionInBits}");
                SendInputToServer(_localTick, frame);
            }
            _localTick += 1;
        }
        
        [ServerRpc]
        private void SendInputToServer(ulong tick, BitPacker packer)
        {
            int count = _systems.Count;
            
            for (var i = 0; i < count; i++)
                _systems[i].ReadState(tick, packer, _isServer);

            packer.Dispose();
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


        public GameObject Create(GameObject prefab)
        {
            return Instantiate(prefab);
        }

        public void Delete(GameObject instance)
        {
            Destroy(instance);
        }
    }
}
