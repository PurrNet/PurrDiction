using UnityEngine;
using UnityEditor;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(BepuColliderDefinition))]
    public class BepuColliderDefinitionDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var type = (BepuColliderType)property.FindPropertyRelative("type").enumValueIndex;
            
            float height = EditorGUIUtility.singleLineHeight * 2 + EditorGUIUtility.standardVerticalSpacing;
            
            switch (type)
            {
                case BepuColliderType.Sphere:
                    height += EditorGUIUtility.singleLineHeight + 
                             EditorGUIUtility.standardVerticalSpacing * 2;
                    break;
                case BepuColliderType.Box:
                    height += (EditorGUIUtility.singleLineHeight + 
                             EditorGUIUtility.standardVerticalSpacing) * 4;
                    break;
                case BepuColliderType.Capsule:
                    height += (EditorGUIUtility.singleLineHeight + 
                             EditorGUIUtility.standardVerticalSpacing) * 3;
                    break;
                case BepuColliderType.Mesh:
                    height += (EditorGUIUtility.singleLineHeight + 
                             EditorGUIUtility.standardVerticalSpacing) * 3;
                    break;
            }
            
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, 
            GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            position.height = EditorGUIUtility.singleLineHeight;
            property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, label);

            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                
                Rect typeRect = new Rect(position.x, position.y + 
                    EditorGUIUtility.singleLineHeight + 
                    EditorGUIUtility.standardVerticalSpacing,
                    position.width, EditorGUIUtility.singleLineHeight);

                var typeProp = property.FindPropertyRelative("type");
                EditorGUI.PropertyField(typeRect, typeProp);

                var type = (BepuColliderType)typeProp.enumValueIndex;
                float yOffset = typeRect.y + EditorGUIUtility.singleLineHeight + 
                    EditorGUIUtility.standardVerticalSpacing;

                switch (type)
                {
                    case BepuColliderType.Sphere:
                        DrawProperty(ref yOffset, position, property, "radius");
                        break;

                    case BepuColliderType.Box:
                        DrawProperty(ref yOffset, position, property, "width");
                        DrawProperty(ref yOffset, position, property, "height");
                        DrawProperty(ref yOffset, position, property, "depth");
                        break;

                    case BepuColliderType.Capsule:
                        DrawProperty(ref yOffset, position, property, "radius");
                        DrawProperty(ref yOffset, position, property, "height");
                        break;

                    case BepuColliderType.Mesh:
                        DrawProperty(ref yOffset, position, property, "mesh");
                        DrawProperty(ref yOffset, position, property, "convex");
                        break;
                }

                EditorGUI.indentLevel--;
            }

            EditorGUI.EndProperty();
        }

        private void DrawProperty(ref float yOffset, Rect position, 
            SerializedProperty property, string propertyName)
        {
            Rect propertyRect = new Rect(position.x, yOffset, position.width, 
                EditorGUIUtility.singleLineHeight);
            EditorGUI.PropertyField(propertyRect, 
                property.FindPropertyRelative(propertyName));
            yOffset += EditorGUIUtility.singleLineHeight + 
                EditorGUIUtility.standardVerticalSpacing;
        }
    }
}
