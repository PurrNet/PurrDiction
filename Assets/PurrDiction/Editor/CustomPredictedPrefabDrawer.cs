using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(PredictedPrefab))]
    public class CustomPredictedPrefabDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var prefabProperty = property.FindPropertyRelative("prefab");
            var pooledProperty = property.FindPropertyRelative("pooled");
            var warmupCountProperty = property.FindPropertyRelative("warmupCount");

            EditorGUI.BeginProperty(position, label, property);

            var fieldWidth = position.width / 3f;

            const float SPACING = 2.5f;

            var prefabRect = new Rect(position.x, position.y, fieldWidth * 2f - SPACING, position.height);
            var sizeRect = new Rect(position.x + fieldWidth * 2f + SPACING, position.y, fieldWidth * 0.5f - SPACING, position.height);
            var toggleRect = new Rect(position.x + fieldWidth * 2f + SPACING * 2 + fieldWidth * 0.5f, position.y, fieldWidth * 0.5f - SPACING, position.height);

            if (!pooledProperty.boolValue)
            {
                prefabRect = new Rect(position.x, position.y, fieldWidth * 2.5f - SPACING, position.height);
                sizeRect = new Rect(position.x + fieldWidth * 2.5f + SPACING, position.y, fieldWidth * 0.5f - SPACING, position.height);
            }

            EditorGUI.PropertyField(prefabRect, prefabProperty, GUIContent.none);
            pooledProperty.boolValue = EditorGUI.ToggleLeft(toggleRect, "Pool", pooledProperty.boolValue);
            if (pooledProperty.boolValue)
            {
                EditorGUI.PropertyField(sizeRect, warmupCountProperty, GUIContent.none);
            }
            else
            {
                warmupCountProperty.intValue = 0;
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }
}
