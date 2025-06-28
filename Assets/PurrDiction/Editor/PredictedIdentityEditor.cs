using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(PredictedIdentity), true)]
#if TRI_INSPECTOR_PACKAGE
    public class PredictedIdentityEditor : TriInspector.Editors.TriEditor
#elif ODIN_INSPECTOR
    public class PredictedIdentityEditor : Sirenix.OdinInspector.Editor.OdinEditor
#else
#endif
    public class PredictedIdentityEditor : UnityEditor.Editor
    {
        static GUIStyle _box;

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            GUILayout.Space(10);
            GUILayout.Label($"Predicted State", EditorStyles.boldLabel);

            _box ??= new GUIStyle("helpbox")
            {
                wordWrap = true,
                stretchWidth = true,
                stretchHeight = true,
                alignment = TextAnchor.UpperLeft
            };

            for (int i = 0; i < targets.Length; i++)
            {
                if (!targets[i] || targets[i] is not PredictedIdentity predictedIdentity)
                    continue;

                var extraContent = predictedIdentity.GetExtraString();
                var content = predictedIdentity.ToString();

                bool bothNonEmpty = !string.IsNullOrEmpty(extraContent) && !string.IsNullOrEmpty(content);

                if (Application.isPlaying)
                {
                    EditorGUILayout.BeginHorizontal("box", GUILayout.ExpandWidth(false));
                    try
                    {
                        GUILayout.Label($"ID: {predictedIdentity.id}", GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(
                            $"Owner ID: {(predictedIdentity.owner.HasValue ? predictedIdentity.owner.Value.ToString() : "None")}",
                            GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        var pm = predictedIdentity.predictionManager;
                        GUILayout.Label(
                            pm
                                ? $"Local Player: {(pm.localPlayer.HasValue ? pm.localPlayer.Value.ToString() : "None")}"
                                : "Not ready",
                            GUILayout.ExpandWidth(false));

                    }
                    catch
                    {
                        GUILayout.Label($"Not Spawned", GUILayout.ExpandWidth(false));
                    }

                    EditorGUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
                if (!string.IsNullOrEmpty(extraContent))
                {
                    if (bothNonEmpty)
                        GUILayout.Box(extraContent, _box, GUILayout.MinWidth(1));
                    else GUILayout.Box(extraContent, _box);
                }

                if (!string.IsNullOrEmpty(content))
                {
                    if (bothNonEmpty)
                        GUILayout.Box(content, _box, GUILayout.MinWidth(1));
                    else GUILayout.Box(content, _box);
                }
                GUILayout.EndHorizontal();
            }
        }
    }
}
