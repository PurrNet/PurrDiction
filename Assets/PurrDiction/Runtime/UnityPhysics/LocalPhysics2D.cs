using UnityEngine;

namespace PurrNet.Prediction
{
    public class LocalPhysics2D : MonoBehaviour
    {
#if UNITY_PHYSICS_2D
        private PredictionManager _manager;
        private Rigidbody2D[] _rigidbodies;
        private UnityRigidbody2DState[] _state;

        private void Awake()
        {
            _rigidbodies = GetComponentsInChildren<Rigidbody2D>();
            _state = new UnityRigidbody2DState[_rigidbodies.Length];
        }

        private void Start()
        {
            if (PredictionManager.TryGetInstance(gameObject.scene.handle, out var manager))
            {
                _manager = manager;
                _manager.onStartingToRollback += OnStartingToRollback;
                _manager.onRollbackFinished += OnRollbackFinished;
            }
        }

        private void OnDestroy()
        {
            if (!_manager)
                return;

            _manager.onStartingToRollback -= OnStartingToRollback;
            _manager.onRollbackFinished -= OnRollbackFinished;
        }

        private void OnStartingToRollback()
        {
            for(int i = 0; i < _rigidbodies.Length; i++)
            {
                var rb = _rigidbodies[i];

                _state[i] = new UnityRigidbody2DState(rb);

                rb.bodyType = RigidbodyType2D.Kinematic;

                //reset velocities as setting bodyType to Kinematic does not reset them like on 3d rigidbodies
#if UNITY_6000
                rb.linearVelocity = default;
#else
                rb.velocity = default;
#endif
                rb.angularVelocity = default;
            }
        }

        private void OnRollbackFinished()
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                var state = _state[i];
                var rb = _rigidbodies[i];
                rb.bodyType = (RigidbodyType2D) state.bodyType;
                //Rigidbody2D can have velocity even when Kinematic. We only skip when bodyType is Static
                if (rb.bodyType == RigidbodyType2D.Static)
                    continue;

#if UNITY_6000
                rb.linearVelocity = state.linearVelocity;
#else
                rb.velocity = state.linearVelocity;
#endif
                rb.angularVelocity = state.angularVelocity;
                if (state.isSleeping)
                    rb.Sleep();
                else rb.WakeUp();
            }
        }
#endif
    }
}