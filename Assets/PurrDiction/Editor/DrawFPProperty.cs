using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(FP), true)]
    public class DrawFPProperty : PropertyDrawer
    {
        private SerializedProperty rawValue;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            rawValue = property.FindPropertyRelative("rawValue");

            var value = FP.FromRaw(rawValue.longValue);

            double representation = MathFP.ToDouble(value);
            var newValue = EditorGUI.DelayedDoubleField(position, label, representation);

            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUI.color = new Color(1f, 1f, 0.5f, 0.5f);
            GUI.Label(position, "FP ");
            GUI.color = Color.white;

            var newFixedValue = FP.FromRaw(MathFP.FromDouble(newValue));
            if (value != newFixedValue)
            {
                rawValue.longValue = newFixedValue.rawValue;
                property.serializedObject.ApplyModifiedProperties();
            }
        }
    }
}
