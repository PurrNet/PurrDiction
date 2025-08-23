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

#if PURRSCENE_OBJECT_FILTERS
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            PurrNet.Modules.SceneObjectsModule.onPreSceneLoad -= FilterNetworkIdentities;
            PurrNet.Modules.SceneObjectsModule.onPreSceneLoad += FilterNetworkIdentities;
        }

        static void FilterNetworkIdentities(Scene scene)
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();
            GetScenePredictedIdentities(scene, identities);

            using var roots = DisposableHashSet<GameObject>.Create();

            for (var i = 0; i < identities.Count; i++)
                roots.Add(identities[i].GetRoot());

            ListPool<PredictedIdentity>.Destroy(identities);

            foreach (var root in roots)
            {
                using var children = DisposableList<NetworkIdentity>.Create();
                root.GetComponentsInChildren(true, children.list);

                for (var i = 0; i < children.Count; i++)
                    children[i].skipSceneAutoSpawning = true;
            }
        }
#endif

        public static void GetScenePredictedIdentities(Scene scene, List<PredictedIdentity> pids)
        {
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
                pids.AddRange(_sceneIdentities);
            }
        }
    }
}
