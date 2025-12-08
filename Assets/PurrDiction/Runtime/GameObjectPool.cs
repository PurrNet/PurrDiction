using System;
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
            if (_pools.ContainsKey(prefab))
                return;
            _pools[prefab] = new GameObjectPool(prefab, _parent, warmup);
        }

        public bool TryGetPool(GameObject prefab, out GameObjectPool pool)
        {
            return _pools.TryGetValue(prefab, out pool);
        }

        public void Dispose()
        {
            foreach (var (_, val) in _pools)
                val.Dispose();
            _pools.Clear();
        }
    }

    public class GameObjectPool
    {
        private readonly Stack<GameObject> _pool = new Stack<GameObject>();
        private readonly Func<GameObject> _factory;
        private readonly Action<GameObject> _reset;

        public GameObjectPool(GameObject prefab, Transform parent, int warmupCount)
        {
            _factory = () =>
            {
                var res = UnityProxy.InstantiateDirectly(prefab);
                using var nidentities = DisposableList<NetworkIdentity>.Create();
                res.GetComponentsInChildren(nidentities.list);
                for (var i = 0; i < nidentities.Count; i++)
                {
                    var id = nidentities[i];
                    if (id) id.skipSceneAutoSpawning = true;
                }
                return res;
            };
            _reset = obj =>
            {
                obj.transform.SetParent(parent, false);
            };

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

        public GameObject Allocate()
        {
            return _pool.Count > 0 ? _pool.Pop() : _factory();
        }

        public void Delete(GameObject obj)
        {
            if (!obj)
                return;
            _reset(obj);
            _pool.Push(obj);
        }

        public void Dispose()
        {
            foreach (var go in _pool)
            {
                if (go)
                    UnityEngine.Object.Destroy(go);
            }
            _pool.Clear();
        }
    }
}
