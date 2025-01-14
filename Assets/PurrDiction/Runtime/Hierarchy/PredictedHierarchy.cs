using System;
using System.Collections.Generic;
using FixMath.NET;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictedHierarchyState : IPackedAuto, IDisposable
    {
        public DisposableList<PredictedAction> actions;
        public readonly int nextInstanceId;
        
        public PredictedHierarchyState(DisposableList<PredictedAction> actions, int nextInstanceId)
        {
            this.actions = actions;
            this.nextInstanceId = nextInstanceId;
        }

        public void Dispose() => actions.Dispose();
    }
    
    public class PredictedHierarchy : PredictedIdentity<PredictedHierarchyState>
    {
        readonly List<PredictedAction> _currentActions = new ();
        readonly Dictionary<PredictedObjectID, GameObject> _instanceMap = new ();
        
        private int _nextInstanceId;
        
        protected override PredictedHierarchyState GetCurrentState()
        {
            var copy = new DisposableList<PredictedAction>(_currentActions.Count);
            copy.AddRange(_currentActions);
            return new PredictedHierarchyState(copy, _nextInstanceId);
        }

        protected override void Simulate(Fix64 delta) { }
        
        protected override void Rollback(PredictedHierarchyState state)
        {
            var currentActions = _currentActions.Count;
            var stateActions = state.actions.Count;
            
            var min = Mathf.Min(currentActions, stateActions);
            
            int i;
            
            for (i = 0; i < min; i++)
            {
                var current = _currentActions[i];
                var target = state.actions[i];
                
                if (!current.Matches(target))
                    break;
            }
            
            // we match up to i, so we need to undo the rest of the actions
            int countToUndo = currentActions - i;

            if (countToUndo > 0)
            {
                for (var j = currentActions - 1; j >= i; j--)
                    UndoAction(_currentActions[j]);

                // clear the undone actions
                _currentActions.RemoveRange(i, countToUndo);

                // we need to redo the rest of the actions
                for (var j = i; j < stateActions; j++)
                {
                    DoAction(state.actions[j]);

                    // add the action to the current actions
                    _currentActions.Add(state.actions[j]);
                }
            }

            _nextInstanceId = state.nextInstanceId;
            
            Debug.Assert(_currentActions.Count == state.actions.Count, "Mismatched action count");
        }
        
        private void DoAction(PredictedAction action)
        {
            switch (action.type)
            {
                case PredictedActionType.Instantiate:
                {
                    if (!predictionManager.TryGetPrefab(action.instantiateAction.prefabId, out var prefab))
                        throw new InvalidOperationException($"Prefab with id '{action.instantiateAction}' not found");

                    var go = predictionManager.Create(prefab);
                    _instanceMap.Add(action.instantiateAction.instanceId, go);
                    break;
                }
                case PredictedActionType.Destroy:
                {
                    if (_instanceMap.Remove(action.destroyAction.instanceId, out var instance))
                        predictionManager.Delete(instance);
                    else throw new InvalidOperationException($"Instance with id '{action.destroyAction.instanceId}' not found");
                    break;
                }
                default: throw new NotImplementedException();
            }
        }
        
        private void UndoAction(PredictedAction action)
        {
            switch (action.type)
            {
                case PredictedActionType.Instantiate:
                {
                    if (_instanceMap.Remove(action.instantiateAction.instanceId, out var instance))
                        predictionManager.Delete(instance);
                    else throw new InvalidOperationException($"Instance with id '{action.instantiateAction}' not found");
                    break;
                }
                case PredictedActionType.Destroy:
                {
                    if (!predictionManager.TryGetPrefab(action.destroyAction.prefabId, out var prefab))
                        throw new InvalidOperationException($"Prefab with id '{action.destroyAction}' not found");
                    
                    var go = predictionManager.Create(prefab);
                    _instanceMap.Add(action.destroyAction.instanceId, go);
                    break;
                }
                default: throw new NotImplementedException();
            }
        }
        
        public PredictedObjectID? Create(int prefabId)
        {
            if (!predictionManager.TryGetPrefab(prefabId, out var prefab))
                return default;
            
            return Create(prefab);
        }

        public PredictedObjectID? Create(GameObject prefab)
        {
            if (!predictionManager.TryGetPrefab(prefab, out var prefabId))
                return default;
            
            var go = predictionManager.Create(prefab);
            var id = new PredictedObjectID(_nextInstanceId);
            var action = new PredictedAction(new PredictedInstantiate(prefabId, id));
            
            _instanceMap.Add(id, go);
            _currentActions.Add(action);
            _nextInstanceId++;
            
            return id;
        }
        
        public bool TryCreate(int prefabId, out PredictedObjectID id)
        {
            var result = Create(prefabId);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }
        
        public bool TryCreate(GameObject prefab, out PredictedObjectID id)
        {
            var result = Create(prefab);
            id = result.GetValueOrDefault();
            return result.HasValue;
        }
        
        public void Delete(PredictedObjectID id)
        {
            if (!_instanceMap.Remove(id, out var instance))
                return;
            
            var action = new PredictedAction(new PredictedDestroy(instance.GetInstanceID(), id));
            _currentActions.Add(action);
        }
        
        public void Delete(PredictedObjectID? id)
        {
            if (!id.HasValue)
                return;
            
            Delete(id.Value);
        }
    }
}
