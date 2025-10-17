using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(sfloat), true)]
    public class DrawSfloatProperty : PropertyDrawer
    {
        private SerializedProperty rawValue;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            rawValue = property.FindPropertyRelative("rawValue");

            var value = sfloat.FromRaw(rawValue.uintValue);

            float representation = value.ToFloat();
            var newValue = EditorGUI.DelayedFloatField(position, label, representation);

            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUI.color = new Color(1f, 1f, 0.5f, 0.5f);
            GUI.Label(position, "sfloat ");
            GUI.color = Color.white;

            var newFixedValue = sfloat.FromFloat(newValue);
            if (value != newFixedValue)
            {
                rawValue.uintValue = newFixedValue.rawValue;
                property.serializedObject.ApplyModifiedProperties();
            }
        }
    }
}
