using System.Collections.Generic;
using System.Linq;
using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.StateMachine
{
    [AddComponentMenu("PurrDiction/State machine")]
    public class PredictedStateMachine : PredictedIdentity<SMState>
    {
        [SerializeField] private List<SerializableInterface<IPredictedStateNodeBase>> _wrappedStates = 
            new List<SerializableInterface<IPredictedStateNodeBase>>();
        private List<IPredictedStateNodeBase> _states;
        public IReadOnlyList<IPredictedStateNodeBase> states => _states;

        public IPredictedStateNodeBase currentStateNode
        {
            get
            {
                if(currentState.stateIndex < 0 || currentState.stateIndex >= _states.Count)
                    return null;
                
                return _states[currentState.stateIndex];
            }
        }

        
        
#if UNITY_EDITOR
        public IPredictedStateNodeBase _previousStateNode;
        public IPredictedStateNodeBase _nextStateNode;
        public IPredictedStateNodeBase _currentStateNode;
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

        protected override void Simulate(ref SMState state, FP delta)
        {
            base.Simulate(ref state, delta);
            
            if (_states.Count <= 0 || state.wantedState <= -1)
                return;
            
            if(state.stateIndex > -1 && _states[state.stateIndex] != null)
                _states[state.stateIndex].StateSimulate(delta);
            
            if(state.wantedState != state.stateIndex)
            {
#if UNITY_EDITOR
                _previousStateNode = _currentStateNode;
                _nextStateNode = _states[(state.wantedState + 1) % _states.Count];
                _currentStateNode = _states[state.wantedState];
#endif
                if(state.stateIndex > -1)
                    _states[state.stateIndex].Exit();
                state.stateIndex = state.wantedState;
                _states[state.stateIndex].Enter();
            }
        }

        protected override SMState GetInitialState()
        {
            var state = new SMState()
            {
                wantedState = 0,
                stateIndex = -1
            };
            
            return state;
        }

        public void Next()
        {
            if (_states.Count == 0) return;
            var nextIndex = (currentState.stateIndex + 1) % _states.Count;
            Debug.Log($"Wanted: {currentState.wantedState} | Current: {currentState.stateIndex} | Next: {nextIndex}");
            SetState(nextIndex);
        }

        public void Previous()
        {
            if (_states.Count == 0) return;
            var previousIndex = (currentState.stateIndex - 1 + _states.Count) % _states.Count;
            SetState(previousIndex);
        }

        public void SetState(int stateIndex)
        {
#if UNITY_EDITOR
            _previousStateNode = _currentStateNode;
            _nextStateNode = _states[(currentState.stateIndex + 1) % _states.Count];
#endif
            SetWantedStateIndex(stateIndex);
        }

        public void SetState(IPredictedStateNodeBase state)
        {
            var index = _states.IndexOf(state);
            if (index == -1)
                return;
            
            SetState(index);
        }
        
        private void SetWantedStateIndex(int index)
        {
            var copy = currentState;
            copy.wantedState = index;
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
        public int wantedState;
        public int stateIndex;
    }
}
