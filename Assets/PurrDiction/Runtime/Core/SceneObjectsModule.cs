using System.Collections.Generic;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Prediction
{
    public static class SceneObjectsModule
    {
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
            {
                var copy = new List<GameObject>();
                foreach (var rootObject in sceneInfo.rootGameObjects)
                    copy.Add(rootObject);
                rootGameObjects = copy.ToArray();
            }

            foreach (var rootObject in rootGameObjects)
            {
                if (rootObject == null || rootObject.scene.handle != scene.handle) continue;

                rootObject.gameObject.GetComponentsInChildren(true, _sceneIdentities);

                if (_sceneIdentities.Count == 0) continue;

                rootObject.gameObject.MakeSureAwakeIsCalled();
                pids.AddRange(_sceneIdentities);
            }
        }
    }
}
