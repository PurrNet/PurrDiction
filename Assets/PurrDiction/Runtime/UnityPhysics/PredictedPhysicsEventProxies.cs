using UnityEngine;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Unity generates contact reports and a managed callback per touching pair per physics step
    /// for every script class that declares a collision or trigger message, whether or not the
    /// message does anything. Predicted rigidbodies therefore keep those messages on small proxy
    /// components that are attached only while the matching event kinds are enabled, with the
    /// per-step Stay messages on their own proxy so Enter/Exit-only bodies never pay for them.
    /// </summary>
    internal static class PredictedPhysicsEventProxies
    {
        /// <summary>
        /// Attaches or detaches one proxy type on <paramref name="gameObject"/> to match
        /// <paramref name="wanted"/>. Returns true with the live proxy when it is attached, so
        /// the caller can (re)bind it; the proxy is hidden and never serialized.
        /// </summary>
        public static bool Sync<TProxy>(GameObject gameObject, bool wanted, out TProxy proxy) where TProxy : MonoBehaviour
        {
            bool present = gameObject.TryGetComponent(out proxy);
            if (wanted)
            {
                if (!present)
                {
                    proxy = gameObject.AddComponent<TProxy>();
                    proxy.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
                }
                return true;
            }

            if (present)
            {
                if (Application.isPlaying)
                    Object.Destroy(proxy);
                else
                    Object.DestroyImmediate(proxy);
                proxy = null;
            }
            return false;
        }
    }
}
