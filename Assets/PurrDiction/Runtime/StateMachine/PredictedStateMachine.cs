using System.Collections.Generic;
using System.Linq;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.StateMachine
{
    [AddComponentMenu("PurrDiction/Predicted State machine")]
    public class PredictedStateMachine : PredictedIdentity<PredictedStateMachine.SMState>
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

        private int _previousViewStateIndex = -1;
        private int _previousVerifiedViewStateIndex = -1;

        private void Awake()
        {
            _states = _wrappedStates.Select(wrapped => wrapped.Value).ToList();
#if UNITY_EDITOR
            _currentStateNode = _states.FirstOrDefault();
#endif

            for (var i = 0; i < _states.Count; i++)
            {
                if (_states[i] == null)
                    continue;
                var state = _states[i];
                state.Setup(this);
            }
        }

        private uint _prevViewTransition;
        private uint _prevVerifiedViewTransition;
        protected override void UpdateView(SMState viewState, SMState? verified)
        {
            if (verified.HasValue)
            {
                if (verified.Value.stateIndex != _previousVerifiedViewStateIndex || verified.Value.transition != _prevVerifiedViewTransition)
                {
                    if (_previousVerifiedViewStateIndex > -1 && _states[_previousVerifiedViewStateIndex] != null)
                        _states[_previousVerifiedViewStateIndex].ViewExit(true);
                    _previousVerifiedViewStateIndex = verified.Value.stateIndex;
                    _prevVerifiedViewTransition = verified.Value.transition;
                    if (_previousVerifiedViewStateIndex > -1 && _states[_previousVerifiedViewStateIndex] != null)
                        _states[_previousVerifiedViewStateIndex].ViewEnter(true);
                }
            }

            if (viewState.stateIndex != _previousViewStateIndex || viewState.transition != _prevViewTransition)
            {
                if (_previousViewStateIndex > -1 && _states[_previousViewStateIndex] != null)
                    _states[_previousViewStateIndex].ViewExit(false);
                _previousViewStateIndex = viewState.stateIndex;
                _prevViewTransition = viewState.transition;
                if (_previousViewStateIndex > -1 && _states[_previousViewStateIndex] != null)
                    _states[_previousViewStateIndex].ViewEnter(false);
            }
        }

        protected override void Simulate(ref SMState state, float delta)
        {
            base.Simulate(ref state, delta);
            if (_states.Count == 0) return;

            if (state.wantedState > -1)
            {
#if UNITY_EDITOR
                _previousStateNode = _currentStateNode;
                _nextStateNode = _states[(state.wantedState + 1) % _states.Count];
                _currentStateNode = _states[state.wantedState];
#endif

                var index = state.wantedState;
                state.wantedState = -1;
                
                if (state.stateIndex > -1)
                    _states[state.stateIndex].Exit();

                state.stateIndex = index;
                _states[state.stateIndex].Enter();
                state.transition++;
            }

            if (state.stateIndex > -1 && _states[state.stateIndex] != null)
                _states[state.stateIndex].StateSimulate(delta);
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
            {
                PurrLogger.LogError($"Can't switch state. Either state ({state}) is invalid, or doesn't exist in states list!", this);
                return;
            }

            SetState(index);
        }

        private void SetWantedStateIndex(int index)
        {
            var copy = currentState;
            copy.wantedState = index;
            currentState = copy;
        }

        public struct SMState : IPredictedData<SMState>
        {
            public int wantedState;
            public int stateIndex;
            public uint transition;

            public void Dispose() { }
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
}
