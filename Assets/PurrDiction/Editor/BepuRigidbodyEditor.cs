using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomEditor(typeof(BepuRigidbody))]
    public class BepuRigidbodyEditor : UnityEditor.Editor
    {
        private SerializedProperty _colliders;
        private SerializedProperty _isTrigger;
        private SerializedProperty _isKinematic;
        private SerializedProperty _mass;
        private SerializedProperty _drag;
        private SerializedProperty _angularDrag;
        
        private SerializedProperty _freezePositionX;
        private SerializedProperty _freezePositionY;
        private SerializedProperty _freezePositionZ;
        private SerializedProperty _freezeRotationX;
        private SerializedProperty _freezeRotationY;
        private SerializedProperty _freezeRotationZ;

        private void OnEnable()
        {
            _colliders = serializedObject.FindProperty("_colliders");
            _isTrigger = serializedObject.FindProperty("_isTrigger");
            _isKinematic = serializedObject.FindProperty("_isKinematic");
            _mass = serializedObject.FindProperty("_mass");
            _drag = serializedObject.FindProperty("_linearDrag");
            _angularDrag = serializedObject.FindProperty("_angularDrag");
            
            _freezePositionX = serializedObject.FindProperty("_freezePositionX");
            _freezePositionY = serializedObject.FindProperty("_freezePositionY");
            _freezePositionZ = serializedObject.FindProperty("_freezePositionZ");
            _freezeRotationX = serializedObject.FindProperty("_freezeRotationX");
            _freezeRotationY = serializedObject.FindProperty("_freezeRotationY");
            _freezeRotationZ = serializedObject.FindProperty("_freezeRotationZ");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Bepu Rigidbody", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_colliders);
            EditorGUILayout.PropertyField(_isTrigger);
            EditorGUILayout.PropertyField(_isKinematic);
            EditorGUILayout.PropertyField(_mass);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Drag", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_drag);
            EditorGUILayout.PropertyField(_angularDrag);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Constraints", EditorStyles.boldLabel);
            
            float labelWidth = EditorGUIUtility.labelWidth;
            
            Rect rect = EditorGUILayout.GetControlRect();
            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelWidth, rect.height), "Freeze Position");
            rect.x += labelWidth;
            rect.width = 40;
            _freezePositionX.boolValue = EditorGUI.ToggleLeft(rect, "X", _freezePositionX.boolValue);
            rect.x += 45;
            _freezePositionY.boolValue = EditorGUI.ToggleLeft(rect, "Y", _freezePositionY.boolValue);
            rect.x += 45;
            _freezePositionZ.boolValue = EditorGUI.ToggleLeft(rect, "Z", _freezePositionZ.boolValue);
            
            rect = EditorGUILayout.GetControlRect();
            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelWidth, rect.height), "Freeze Rotation");
            rect.x += labelWidth;
            rect.width = 40;
            _freezeRotationX.boolValue = EditorGUI.ToggleLeft(rect, "X", _freezeRotationX.boolValue);
            rect.x += 45;
            _freezeRotationY.boolValue = EditorGUI.ToggleLeft(rect, "Y", _freezeRotationY.boolValue);
            rect.x += 45;
            _freezeRotationZ.boolValue = EditorGUI.ToggleLeft(rect, "Z", _freezeRotationZ.boolValue);

            serializedObject.ApplyModifiedProperties();
        }
    }
}