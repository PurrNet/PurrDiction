using UnityEngine.UIElements;

namespace PurrNet.Prediction.Editor
{
    /// <summary>Uses PurrNet's optional Alchemy renderer when the installed version supports it.</summary>
    public static class AlchemyIntegration
    {
        public static VisualElement CreateInspectorGUI(UnityEditor.Editor owner, params string[] excludedProperties)
        {
#if ALCHEMY_PACKAGE && PURRNET_ALCHEMY_SUPPORT
            return global::PurrNet.Editor.AlchemyIntegration.CreateInspectorGUI(owner, excludedProperties);
#else
            return null;
#endif
        }

        public static void OnDisable(UnityEditor.Editor owner)
        {
#if ALCHEMY_PACKAGE && PURRNET_ALCHEMY_SUPPORT
            global::PurrNet.Editor.AlchemyIntegration.OnDisable(owner);
#endif
        }
    }
}
