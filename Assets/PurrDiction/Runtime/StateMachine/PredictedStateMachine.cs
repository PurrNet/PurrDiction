using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PurrNet.Prediction.StateMachine
{
    public class PredictedStateMachine : PredictedIdentity<SMState>
    {
        [SerializeField] private List<SerializableInterface<IPredictedStateNodeBase>> _wrappedStates = 
            new List<SerializableInterface<IPredictedStateNodeBase>>();
        private List<IPredictedStateNodeBase> _states;
        public IReadOnlyList<IPredictedStateNodeBase> States => _states;

#if UNITY_EDITOR
        public IPredictedStateNodeBase _previousStateNode;
        public IPredictedStateNodeBase _currentStateNode;
        public IPredictedStateNodeBase _nextStateNode;
#endif
    
        private void Awake()
        {
            _states = _wrappedStates.Select(wrapped => wrapped.Value).ToList();
    
            for (var i = 0; i < _states.Count; i++)
            {
                if (_states[i] == null)
                    continue;
                var state = _states[i];
                state.Setup(this);
            }
        }

        protected override SMState GetInitialState()
        {
            Debug.Log(_states.Count);
            if(_states.Count > 0)
                SetState(_states[0]);
            
            return base.GetInitialState();
        }

        public void Next()
        {
            if (_states.Count == 0) return;
            var nextIndex = (currentState.stateIndex + 1) % _states.Count;
            SetState(_states[nextIndex]);
        }

        public void Next<TData>(TData data) where TData : struct, IPredictedData<TData>
        {
            if (_states.Count == 0) return;
            var nextIndex = (currentState.stateIndex + 1) % _states.Count;
            SetState<TData>(_states[nextIndex] as IPredictedStateNodeBase<TData>, data);
        }

        public void Previous()
        {
            if (_states.Count == 0) return;
            var previousIndex = (currentState.stateIndex - 1 + _states.Count) % _states.Count;
            SetState(_states[previousIndex]);
        }

        public void Previous<TData>(TData data) where TData : struct, IPredictedData<TData>
        {
            if (_states.Count == 0) return;
            var previousIndex = (currentState.stateIndex - 1 + _states.Count) % _states.Count;
            SetState<TData>(_states[previousIndex] as IPredictedStateNodeBase<TData>, data);
        }

        public void SetState(IPredictedStateNodeBase state)
        {
            Debug.Log($" {state.GetType().Name}");
#if UNITY_EDITOR
            _previousStateNode = _currentStateNode;
            _currentStateNode = state;
            _nextStateNode = _states[(currentState.stateIndex + 1) % _states.Count];
#endif
            
            _states[currentState.stateIndex].Exit();
            SetStateIndex(_states.IndexOf(state));
            state.Enter();
        }

        public void SetState<TData>(IPredictedStateNodeBase<TData> state, TData data) where TData : struct, IPredictedData<TData>
        {
            _states[currentState.stateIndex].Exit();
            SetStateIndex(_states.IndexOf(state));
            state.Enter(data);
        }
        
        private void SetStateIndex(int index)
        {
            var copy = currentState;
            copy.stateIndex = index;
            currentState = copy;
        }
    }
    
    [System.Serializable]
    public class SerializableInterface<T> where T : class
    {
        [SerializeField] internal Object _object;

        public T Value
        {
            get 
            {
                if (_object is GameObject go)
                {
                    return go.GetComponent<T>();
                }
                return _object as T;
            }
            set => _object = value as Object;
        }
    }
    
    public struct SMState : IPredictedData<SMState>
    {
        public int stateIndex;
    }
}
