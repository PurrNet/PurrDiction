using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    using UnityEditor;
    using UnityEngine;

    [CustomPropertyDrawer(typeof(PredictedPrefabs), true)]
    class PredictedPrefabsDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect pos, SerializedProperty prop, GUIContent label)
        {
            var widthChange = prop.objectReferenceValue == null ? -55 : 0;
            
            var fieldRect = new Rect(pos.x, pos.y, pos.width + widthChange, pos.height);
            Color bg = GUI.backgroundColor;
            if (prop.objectReferenceValue == null) GUI.backgroundColor = Color.yellow;
            prop.objectReferenceValue = EditorGUI.ObjectField(fieldRect, label, prop.objectReferenceValue, typeof(PredictedPrefabs), false);
            GUI.backgroundColor = bg;

            if (prop.objectReferenceValue == null)
            {
                var btnRect = new Rect(pos.x + pos.width - 50, pos.y, 50, pos.height);
                if (GUI.Button(btnRect, "New"))
                {
                    var asset = ScriptableObject.CreateInstance<PredictedPrefabs>();
                    string path = AssetDatabase.GenerateUniqueAssetPath("Assets/PredictedPrefabs.asset");
                    AssetDatabase.CreateAsset(asset, path);
                    AssetDatabase.SaveAssets();
                    prop.objectReferenceValue = asset;
                    prop.serializedObject.ApplyModifiedProperties();
                    EditorGUIUtility.PingObject(asset);
                }
            }
        }
    }
}
