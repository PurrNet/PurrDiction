using System.Collections.Generic;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class GameObjectPoolCollection
    {
        private readonly Transform _parent;
        private readonly Dictionary<GameObject, GameObjectPool> _pools = new ();

        public GameObjectPoolCollection(Transform parent)
        {
            _parent = parent;
        }

        public void Register(GameObject prefab, int warmup)
        {
            _pools[prefab] = new GameObjectPool(prefab, _parent, warmup);
        }

        public bool TryGetPool(GameObject prefab, out GameObjectPool pool)
        {
            return _pools.TryGetValue(prefab, out pool);
        }
    }

    public class GameObjectPool : GenericPool<GameObject>
    {
        public GameObjectPool(GameObject prefab, Transform parent, int warmupCount) : base(
            () => UnityProxy.InstantiateDirectly(prefab),
            reset: obj =>
            {
                obj.transform.SetParent(parent, false);
            })
        {
            var toDelete = ListPool<GameObject>.Instantiate();

            for (int i = 0; i < warmupCount; i++)
            {
                var go = Allocate();
#if PURRNET_DEBUG_POOLING
                go.name += "-Warmup-" + i;
#endif
                toDelete.Add(go);
            }

            foreach (var go in toDelete)
                Delete(go);
        }
    }
}
