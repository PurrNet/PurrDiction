using System.Collections.Generic;
using UnityEngine;

namespace PurrNet.Prediction
{
    public readonly struct PredictedTickPool
    {
        private readonly List<PooledInstance> _pool;
        
        public PredictedTickPool(List<PooledInstance> pool)
        {
            _pool = pool;
        }
            
        public bool Put(InstanceDetails id, GameObject go)
        {
            _pool.Add(new PooledInstance(go, id.spawnPosition, id.spawnRotation));
            return true;
        }
            
        public bool TryTake(InstanceDetails id, out GameObject go)
        {
            int closestIndex = -1;
            float closestError = float.MaxValue;
            
            for (var i = 0; i < _pool.Count; i++)
            {
                var instance = _pool[i];
                
                float posError = Vector3.Distance(instance.spawnPosition, id.spawnPosition);
                
                if (posError < closestError)
                {
                    closestError = posError;
                    closestIndex = i;
                }
            }
            
            if (closestIndex == -1)
            {
                go = null;
                return false;
            }
            
            go = _pool[closestIndex].gameObject;
            _pool.RemoveAt(closestIndex);
            return true;
        }

        public void Clear(PredictionManager predictionManager)
        {
            foreach (var pair in _pool)
                predictionManager.InternalDelete(pair.gameObject);
            _pool.Clear();
        }
    }
}