using System.Collections.Generic;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class GameObjectPoolCollection
    {
        private readonly bool _moveToParent;
        private readonly Transform _parent;
        private readonly Dictionary<GameObject, GameObjectPool> _pools = new ();

        public GameObjectPoolCollection(Transform parent, bool moveToParent)
        {
            _moveToParent = moveToParent;
            _parent = parent;
        }

        public void Register(GameObject prefab, int warmup)
        {
            _pools[prefab] = new GameObjectPool(prefab, _parent, warmup, _moveToParent);
        }

        public bool TryGetPool(GameObject prefab, out GameObjectPool pool)
        {
            return _pools.TryGetValue(prefab, out pool);
        }

        public GameObject Allocate(GameObject prefab)
        {
            if (!_pools.TryGetValue(prefab, out var pool))
                throw new KeyNotFoundException($"No pool registered for prefab: {prefab.name}");

            return pool.Allocate();
        }

        public void Delete(GameObject prefab, GameObject obj)
        {
            if (!_pools.TryGetValue(prefab, out var pool))
                throw new KeyNotFoundException($"No pool registered for prefab: {prefab.name}");

            pool.Delete(obj);
        }
    }

    public class GameObjectPool : GenericPool<GameObject>
    {
        public GameObjectPool(GameObject prefab, Transform parent, int warmupCount, bool moveToParent) : base(
            () => UnityProxy.InstantiateDirectly(prefab),
            reset: obj =>
            {
                if (moveToParent)
                     obj.transform.SetParent(parent, false);
                else obj.SetActive(false);
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
