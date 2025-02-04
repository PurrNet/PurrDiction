using System;
using System.Collections.Generic;
using FixMath.NET;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BEPUphysics.Unity
{
    [DefaultExecutionOrder(-10000)]
    public class BEPU_PhysicsSpace : MonoBehaviour
    {
        static readonly Dictionary<int, BEPU_PhysicsSpace> _spaces = new();
        
        public Space space { get; private set; }
        
        public event Action onPreSimulate;
        public event Action onPostSimulate;

        public FP timeStep { get; set; } = 1 / 60M;

        private void Awake()
        {
            _spaces.Add(gameObject.scene.handle, this);

            space = new Space
            {
                ForceUpdater =
                {
                    Gravity = new BEPUutilities.FPVector3(0, -9.81M, 0)
                }
            };
        }

        private void OnDestroy()
        {
            _spaces.Remove(gameObject.scene.handle);
        }

        public void Simulate(FP step)
        {
            timeStep = step;

            onPreSimulate?.Invoke();
            space.Update(step);
            onPostSimulate?.Invoke();
        }
        
        public static BEPU_PhysicsSpace GetSpace(GameObject gameObject)
        {
            return GetSpace(gameObject.scene);
        }
        
        public static BEPU_PhysicsSpace GetSpace(Scene scene)
        {
            return _spaces.TryGetValue(scene.handle, out var space) ? space : null;
        }
    }
}
