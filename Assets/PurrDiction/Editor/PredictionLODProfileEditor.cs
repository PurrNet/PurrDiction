using PurrNet.Editor;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomEditor(typeof(PredictionLODProfile))]
    [CanEditMultipleObjects]
    internal sealed class PredictionLODProfileEditor : UnityEditor.Editor
    {
        private SerializedProperty _networkProfile;
        private SerializedProperty _tiers;
        private SerializedProperty _culledPolicy;
        private bool _showInlineNetworkProfile;

        private void OnEnable()
        {
            _networkProfile = serializedObject.FindProperty("_networkProfile");
            _tiers = serializedObject.FindProperty("_tiers");
            _culledPolicy = serializedObject.FindProperty("_culledPolicy");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();
            EditorGUILayout.PropertyField(_networkProfile);

            if (!_networkProfile.hasMultipleDifferentValues &&
                _networkProfile.objectReferenceValue is NetworkLODProfile networkProfile)
            {
                NetworkLODProfileEditorGUI.DrawSummary(networkProfile);
                _showInlineNetworkProfile = EditorGUILayout.Foldout(
                    _showInlineNetworkProfile,
                    "Edit Network Profile Inline",
                    true);

                if (_showInlineNetworkProfile)
                {
                    EditorGUI.indentLevel++;
                    NetworkLODProfileEditorGUI.DrawInlineEditor(networkProfile);
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space(5f);
            EditorGUILayout.PropertyField(_tiers, true);
            EditorGUILayout.PropertyField(_culledPolicy);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
