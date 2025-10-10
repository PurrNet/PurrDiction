using System.Collections.Generic;
using UnityEngine.SceneManagement;
using PurrNet.Modules;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public static class SceneObjectsModule
    {
        private static readonly List<PredictedIdentity> _sceneIdentities = new List<PredictedIdentity>();

        private static readonly Dictionary<Scene, List<PredictedIdentity>> _cache = new ();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void CleanupCache() => _cache.Clear();

#if PURRSCENE_OBJECT_FILTERS
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            PurrNet.Modules.SceneObjectsModule.onPreSceneLoad -= CacheScene;
            PurrNet.Modules.SceneObjectsModule.onPreSceneLoad += CacheScene;
        }

        static void CacheScene(Scene scene)
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            GetScenePredictedIdentities(scene, identities);
            ListPool<PredictedIdentity>.Destroy(identities);
        }
#endif

        public static void GetScenePredictedIdentities(Scene scene, List<PredictedIdentity> pids)
        {
            if (_cache.TryGetValue(scene, out var cached))
            {
                for (var i = 0; i < cached.Count; i++)
                {
                    var p = cached[i];
                    if (p)
                        pids.Add(p);
                }

                return;
            }

            var result = new List<PredictedIdentity>();
            var rootGameObjects = scene.GetRootGameObjects();

            PurrSceneInfo sceneInfo = null;

            for (var i = 0; i < rootGameObjects.Length; i++)
            {
                var rootObject = rootGameObjects[i];
                if (rootObject.TryGetComponent<PurrSceneInfo>(out var si))
                {
                    sceneInfo = si;
                    break;
                }
            }

            if (sceneInfo)
                rootGameObjects = sceneInfo.rootGameObjects.ToArray();

            for (var i = 0; i < rootGameObjects.Length; i++)
            {
                var rootObject = rootGameObjects[i];
                if (!rootObject || rootObject.scene.handle != scene.handle) continue;

                rootObject.gameObject.GetComponentsInChildren(true, _sceneIdentities);

                if (_sceneIdentities.Count == 0) continue;

                rootObject.gameObject.MakeSureAwakeIsCalled();
                result.AddRange(_sceneIdentities);
            }

            pids.AddRange(result);
            _cache[scene] = result;
        }
    }
}
