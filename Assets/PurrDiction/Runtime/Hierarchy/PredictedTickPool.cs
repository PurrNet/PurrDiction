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

        public bool Put(InstanceDetails id, GameObject go, ulong tick)
        {
            _pool.Add(new PooledInstance(go, id.spawnPosition, tick));
            return true;
        }

        public bool TryTakePrecise(InstanceDetails id, out GameObject go)
        {
            for (var i = 0; i < _pool.Count; i++)
            {
                var instance = _pool[i];

                if (instance.spawnPosition == id.spawnPosition)
                {
                    go = instance.gameObject;
                    _pool.RemoveAt(i);
                    return true;
                }
            }

            go = null;
            return false;
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

        public void ClearOld(PredictionManager predictionManager)
        {
            for (var i = 0; i < _pool.Count; i++)
            {
                var pair = _pool[i];
                var tick = pair.addedTick;
                var currentTick = predictionManager.localTick;
                var delta = currentTick - tick;

                if (delta > (uint)predictionManager.tickRate)
                {
                    PredictionManager.InternalDelete(pair.gameObject);
                    _pool.RemoveAt(i--);
                }
            }
        }

        public void Clear(PredictionManager predictionManager)
        {
            foreach (var pair in _pool)
                PredictionManager.InternalDelete(pair.gameObject);
            _pool.Clear();
        }
    }
}
