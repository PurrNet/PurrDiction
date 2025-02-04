using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(BEPUutilities.FPVector2), true)]
    public class DrawVector2 : PropertyDrawer
    {
        private SerializedProperty xValue;
        private SerializedProperty yValue;
        
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Save the original label width
            float originalLabelWidth = EditorGUIUtility.labelWidth;
    
            // Set a smaller label width for the axis labels
            xValue = property.FindPropertyRelative("X");
            yValue = property.FindPropertyRelative("Y");
    
            // Draw the main label
            position = EditorGUI.PrefixLabel(position, label);
    
            // Calculate the width for each field
            float fieldWidth = position.width / 3;
    
            // Draw the X, Y, and Z fields with their labels
            var xRect = new Rect(position.x, position.y, fieldWidth - 3, position.height);
            var yRect = new Rect(position.x + fieldWidth + 2, position.y, fieldWidth - 3, position.height);
    
            EditorGUIUtility.labelWidth = 12f; // Adjust this value as needed
            EditorGUI.PropertyField(xRect, xValue, new GUIContent("X"));
            EditorGUI.PropertyField(yRect, yValue, new GUIContent("Y"));
    
            // Restore the original label width
            EditorGUIUtility.labelWidth = originalLabelWidth;
    
            // If any of the values change, apply the changes
            if (GUI.changed)
            {
                property.serializedObject.ApplyModifiedProperties();
            }
        }
    }
}
