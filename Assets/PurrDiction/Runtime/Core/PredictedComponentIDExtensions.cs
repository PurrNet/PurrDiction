using JetBrains.Annotations;
using UnityEngine;

namespace PurrNet.Prediction
{
    [UsedImplicitly]
    public static class PredictedComponentIDExtensions
    {
        public static GameObject GetGameObject(this PredictedComponentID? id, PredictionManager manager)
        {
            return id?.GetGameObject(manager);
        }

        public static T GetIdentity<T>(this PredictedComponentID? id, PredictionManager manager) where T : PredictedIdentity
        {
            return id?.GetIdentity<T>(manager);
        }

        public static bool TryGetIdentity<T>(this PredictedComponentID? id, PredictionManager manager, out T identity) where T : PredictedIdentity
        {
            identity = null;
            return id?.TryGetIdentity<T>(manager, out identity) ?? false;
        }

        public static bool TryGetGameObject(this PredictedComponentID? id, PredictionManager manager, out GameObject gameObject)
        {
            gameObject = null;
            return id?.TryGetGameObject(manager, out gameObject) ?? false;
        }

        public static bool TryGetIdentity(this PredictedComponentID? id, PredictionManager manager, out PredictedIdentity identity)
        {
            identity = null;
            return id?.TryGetIdentity(manager, out identity) ?? false;
        }
    }
}