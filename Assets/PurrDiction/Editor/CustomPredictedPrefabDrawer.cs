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
            var poolingProperty = property.FindPropertyRelative("pooling");
            var usePoolingProperty = poolingProperty.FindPropertyRelative("usePooling");
            var initialSizeProperty = poolingProperty.FindPropertyRelative("initialSize");

            EditorGUI.BeginProperty(position, label, property);

            var fieldWidth = position.width / 3f;

            const float SPACING = 2.5f;

            // same as above, but with spacing
            var prefabRect = new Rect(position.x, position.y, fieldWidth * 2f - SPACING, position.height);
            var sizeRect = new Rect(position.x + fieldWidth * 2f + SPACING, position.y, fieldWidth * 0.5f - SPACING, position.height);
            var toggleRect = new Rect(position.x + fieldWidth * 2f + SPACING * 2 + fieldWidth * 0.5f, position.y, fieldWidth * 0.5f - SPACING, position.height);

            if (!usePoolingProperty.boolValue)
            {
                /*prefabRect = new Rect(position.x, position.y, fieldWidth * 2.5f, position.height);
                sizeRect = new Rect(position.x + fieldWidth * 2.5f, position.y, fieldWidth * 0.5f, position.height);*/

                prefabRect = new Rect(position.x, position.y, fieldWidth * 2.5f - SPACING, position.height);
                sizeRect = new Rect(position.x + fieldWidth * 2.5f + SPACING, position.y, fieldWidth * 0.5f - SPACING, position.height);
            }

            EditorGUI.PropertyField(prefabRect, prefabProperty, GUIContent.none);
            usePoolingProperty.boolValue = EditorGUI.ToggleLeft(toggleRect, "Pool", usePoolingProperty.boolValue);
            if (usePoolingProperty.boolValue)
            {
                EditorGUI.PropertyField(sizeRect, initialSizeProperty, GUIContent.none);
            }
            else
            {
                initialSizeProperty.intValue = 0; // Reset size if pooling is not used
            }

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }
}
