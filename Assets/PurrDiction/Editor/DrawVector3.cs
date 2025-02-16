using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(BEPUutilities.FPVector3))]
    public class DrawVector3 : PropertyDrawer
    {
        private SerializedProperty xValue;
        private SerializedProperty yValue;
        private SerializedProperty zValue;
        
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);

            xValue = property.FindPropertyRelative("x");
            yValue = property.FindPropertyRelative("y");
            zValue = property.FindPropertyRelative("z");
            
            float originalLabelWidth = EditorGUIUtility.labelWidth;
            int originalIndent = EditorGUI.indentLevel;
            
            EditorGUI.indentLevel = 0;
            
            float fieldWidth = position.width / 3f;
            float spacing = 2f;
            float labelWidth = 14f;

            Rect xRect = new Rect(position.x, position.y, fieldWidth - spacing, position.height);
            Rect yRect = new Rect(position.x + fieldWidth, position.y, fieldWidth - spacing, position.height);
            Rect zRect = new Rect(position.x + 2 * fieldWidth, position.y, fieldWidth - spacing, position.height);

            var labelStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };

            EditorGUIUtility.labelWidth = labelWidth;
            
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                EditorGUI.LabelField(new Rect(xRect.x, xRect.y, labelWidth, xRect.height), "X", labelStyle);
                EditorGUI.PropertyField(xRect, xValue, new GUIContent("X"));

                EditorGUI.LabelField(new Rect(yRect.x, yRect.y, labelWidth, yRect.height), "Y", labelStyle);
                EditorGUI.PropertyField(yRect, yValue, new GUIContent("Y"));

                EditorGUI.LabelField(new Rect(zRect.x, zRect.y, labelWidth, zRect.height), "Z", labelStyle);
                EditorGUI.PropertyField(zRect, zValue, new GUIContent("Z"));

                if (check.changed)
                {
                    property.serializedObject.ApplyModifiedProperties();
                }
            }

            EditorGUIUtility.labelWidth = originalLabelWidth;
            EditorGUI.indentLevel = originalIndent;
        }
    }
}
