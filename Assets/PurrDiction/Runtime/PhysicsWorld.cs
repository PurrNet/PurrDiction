using System.Collections.Generic;
using Jolt;
using UnityEngine;

namespace PurrNet.Prediction
{
    [DefaultExecutionOrder(-1000)]
    public class PredictedWorld : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();
        
        static readonly Dictionary<int, PredictedWorld> _instances = new ();
        
        [SerializeField] uint _maxBodies = 1024;
        [SerializeField] uint _maxBodyPairs = 1024;
        [SerializeField] uint _maxContactConstraints = 1024;
        
        private PhysicsSystem _system;
        
        public static bool TryGetInstance(int sceneHandle, out PredictedWorld world)
        {
            return _instances.TryGetValue(sceneHandle, out world);
        }

        private void Awake()
        {
            _instances[gameObject.scene.handle] = this;
            
            var filter = ObjectLayerPairFilterTable.Create(32); // unity has 32 layers
            var broadFilter = BroadPhaseLayerInterfaceTable.Create(32, 1);
            
            for (ushort i = 0; i < 32; ++i)
                broadFilter.MapObjectToBroadPhaseLayer(i, 0);

            for (ushort i = 0; i < 32; i++)
            {
                for (ushort j = 0; j < 32; j++)
                {
                    if (!Physics.GetIgnoreLayerCollision(i, j))
                        filter.EnableCollision(i, j);
                }
            }
            
            var table = ObjectVsBroadPhaseLayerFilterTable.Create(
                broadFilter, 1,
                filter, 32
            );
            
            var settings = new PhysicsSystemSettings
            {
                MaxBodies = _maxBodies,
                MaxBodyPairs = _maxBodyPairs,
                MaxContactConstraints = _maxContactConstraints,
                ObjectLayerPairFilter = filter,
                BroadPhaseLayerInterface = broadFilter,
                ObjectVsBroadPhaseLayerFilter = table,
            };
            
            _system = new PhysicsSystem(settings);
            _system.SetGravity(Physics.gravity);
        }

        public void Simulate(float delta)
        {
            const float ONE_STEP = 0.01666666666f;
            var steps = Mathf.CeilToInt(delta / ONE_STEP);

            if (!_system.Update(delta, steps, out var error))
                Debug.LogError($"Physics step failed: {error}");
        }
        
        private void OnDestroy()
        {
            _system.Dispose();
        }
    }
}
