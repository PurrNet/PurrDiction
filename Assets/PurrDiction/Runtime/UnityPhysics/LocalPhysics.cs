using UnityEngine;

namespace PurrNet.Prediction
{
    public class LocalPhysics : MonoBehaviour
    {
#if UNITY_PHYSICS_3D
        private PredictionManager _manager;
        private Rigidbody[] _rigidbodies;
        private UnityRigidbodyState[] _state;

        private void Awake()
        {
            _rigidbodies = GetComponentsInChildren<Rigidbody>();
            _state = new UnityRigidbodyState[_rigidbodies.Length];
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

        public void CacheCurrentState(bool overrideVelocities = false)
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                if (overrideVelocities)
                     _state[i] = new UnityRigidbodyState(_rigidbodies[i]);
                else _state[i].isKinematic = _rigidbodies[i].isKinematic;
            }
        }

        private void OnStartingToRollback()
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                _state[i] = new UnityRigidbodyState(_rigidbodies[i]);
                _rigidbodies[i].isKinematic = true;
            }
        }

        private void OnRollbackFinished()
        {
            for (int i = 0; i < _rigidbodies.Length; i++)
            {
                var state = _state[i];
                var rb = _rigidbodies[i];
                rb.isKinematic = state.isKinematic;
                if (state.isKinematic)
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
