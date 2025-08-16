using JetBrains.Annotations;
using UnityEngine;

namespace PurrNet.Prediction
{
    [UsedImplicitly]
    public static class PredictedObjectIDExtensions
    {
        public static GameObject GetGameObject(this PredictedObjectID? id, PredictionManager manager)
        {
            return id?.GetGameObject(manager);
        }

        public static bool TryGetGameObject(this PredictedObjectID? id, PredictionManager manager, out GameObject gameObject)
        {
            gameObject = null;
            return id?.TryGetGameObject(manager, out gameObject) ?? false;
        }

        public static T GetComponent<T>(this PredictedObjectID? id, PredictionManager manager) where T : Component
        {
            return id?.GetComponent<T>(manager);
        }

        public static bool TryGetComponent<T>(this PredictedObjectID? id, PredictionManager manager, out T component) where T : Component
        {
            component = null;
            return id?.TryGetComponent(manager, out component) ?? false;
        }
    }
}