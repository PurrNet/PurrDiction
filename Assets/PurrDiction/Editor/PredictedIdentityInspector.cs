#if PURRNET_DEBUG_PREDICTION
using PurrNet.Prediction;
using UnityEditor;

[CustomEditor(typeof(PredictedIdentity), true)]
public class PredictedIdentityInspector : Editor
{
    private bool _showDebugInfo;
    
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();
        
        _showDebugInfo = EditorGUILayout.Foldout(_showDebugInfo, "Debug Info");

        if (_showDebugInfo)
        {
            var predictedIdentity = (PredictedIdentity)target;
            string info = predictedIdentity.GetDebugInfo(0);

            if (!string.IsNullOrEmpty(info))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Debug Info", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(info, EditorStyles.textArea);
            }
        }
    }
}
#endif