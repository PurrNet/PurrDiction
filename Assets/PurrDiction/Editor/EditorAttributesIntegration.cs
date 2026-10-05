using UnityEngine.UIElements;

namespace PurrNet.Prediction.Editor
{
    /// <summary>Uses PurrNet's optional EditorAttributes renderer when the installed version supports it.</summary>
    public static class EditorAttributesIntegration
    {
        public static VisualElement CreateInspectorGUI(UnityEditor.Editor owner, params string[] excludedProperties)
        {
#if EDITOR_ATTRIBUTES_PACKAGE && PURRNET_EDITOR_ATTRIBUTES_SUPPORT
            return global::PurrNet.Editor.EditorAttributesIntegration.CreateInspectorGUI(owner, excludedProperties);
#else
            return null;
#endif
        }

        public static void OnDisable(UnityEditor.Editor owner)
        {
#if EDITOR_ATTRIBUTES_PACKAGE && PURRNET_EDITOR_ATTRIBUTES_SUPPORT
            global::PurrNet.Editor.EditorAttributesIntegration.OnDisable(owner);
#endif
        }

        public static void OnSceneGUI(UnityEditor.Editor owner)
        {
#if EDITOR_ATTRIBUTES_PACKAGE && PURRNET_EDITOR_ATTRIBUTES_SUPPORT
            global::PurrNet.Editor.EditorAttributesIntegration.OnSceneGUI(owner);
#endif
        }
    }
}
