using System.Collections.Generic;
using PurrNet.Modules;
using UnityEngine.SceneManagement;

namespace PurrNet.Prediction
{
    public static class SceneObjectsModule
    {
#if PURRSCENE_OBJECT_FILTERS
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
		{
            PurrNet.Modules.SceneObjectsModule.onFilterSceneObjects -= Filter;
            PurrNet.Modules.SceneObjectsModule.onFilterSceneObjects += Filter;
		}

		static bool Filter(UnityEngine.GameObject root)
		{
            bool hasAPredictedIdentity = root.GetComponentInChildren<PredictedIdentity>(true);
            return !hasAPredictedIdentity;
		}
#endif

        private static readonly List<PredictedIdentity> _sceneIdentities = new List<PredictedIdentity>();

        public static void GetScenePredictedIdentities(Scene scene, List<PredictedIdentity> pids)
        {
            var rootGameObjects = scene.GetRootGameObjects();

            PurrSceneInfo sceneInfo = null;

            foreach (var rootObject in rootGameObjects)
            {
                if (rootObject.TryGetComponent<PurrSceneInfo>(out var si))
                {
                    sceneInfo = si;
                    break;
                }
            }

            if (sceneInfo)
                rootGameObjects = sceneInfo.rootGameObjects.ToArray();

            foreach (var rootObject in rootGameObjects)
            {
                if (!rootObject || rootObject.scene.handle != scene.handle) continue;

                rootObject.gameObject.GetComponentsInChildren(true, _sceneIdentities);

                if (_sceneIdentities.Count == 0) continue;

                rootObject.gameObject.MakeSureAwakeIsCalled();
                pids.AddRange(_sceneIdentities);
            }
        }
    }
}
