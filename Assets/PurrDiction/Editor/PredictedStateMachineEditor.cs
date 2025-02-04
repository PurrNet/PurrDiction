#if UNITY_EDITOR
using System.Reflection;
using PurrNet.Prediction.StateMachine;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CustomPropertyDrawer(typeof(IPredictedStateNodeBase))]
    public class PredictedStateNodeDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            Object oldValue = property.objectReferenceValue;
            Object newValue = EditorGUI.ObjectField(position, label, oldValue, typeof(Object), true);
            
            if (newValue == null || newValue is IPredictedStateNodeBase)
            {
                property.objectReferenceValue = newValue;
            }
        }
    }
    
    [CustomPropertyDrawer(typeof(SerializableInterface<>))]
    public class SerializableInterfaceDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.PropertyField(position, property.FindPropertyRelative("_object"), label);
        }
    }
    
    [CustomEditor(typeof(PredictedStateMachine))]
    public class PredictedStateMachineEditor : UnityEditor.Editor
    {
        private PredictedStateMachine _stateMachine;
        private SerializedProperty _statesProperty;

        private void OnEnable()
        {
            _stateMachine = target as PredictedStateMachine;
            _statesProperty = serializedObject.FindProperty("_wrappedStates");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();
            
            if (Application.isPlaying)
                EditorGUI.BeginDisabledGroup(true);

            EditorGUILayout.PropertyField(_statesProperty, new GUIContent("States"), true);

            if (Application.isPlaying)
                EditorGUI.EndDisabledGroup();

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(target);
                serializedObject.ApplyModifiedProperties();
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("State information available during Play Mode", MessageType.Info);
                return;
            }

            DrawStateMachineInfo();
        }

        private void DrawStateMachineInfo()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("State Machine Status", EditorStyles.boldLabel);

                if (_stateMachine._currentStateNode != null)
                {
                    EditorGUILayout.LabelField("Current State:", _stateMachine._currentStateNode.GetType().Name);

                    DrawStateData(_stateMachine._currentStateNode);
                    
                    string previousState = _stateMachine._previousStateNode != null 
                        ? _stateMachine._previousStateNode.GetType().Name 
                        : "None";
                    EditorGUILayout.LabelField("Previous State:", previousState);

                    // Next state
                    string nextState = _stateMachine._nextStateNode != null 
                        ? _stateMachine._nextStateNode.GetType().Name 
                        : "None";
                    EditorGUILayout.LabelField("Next State:", nextState);
                }
                else
                {
                    EditorGUILayout.LabelField("Current State: None");
                }
            }
        }

        private void DrawStateData(IPredictedStateNodeBase stateNode)
        {
            var nodeType = stateNode.GetType();
            var genericInterface = nodeType.GetInterfaces()[0];
            var dataTypes = genericInterface.GetGenericArguments();
            if(dataTypes.Length == 0)
                return;
            var dataType = dataTypes[0];

            var fields = dataType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var properties = dataType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            if (fields.Length > 0 || properties.Length > 0)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("State Data:", EditorStyles.boldLabel);

                    var getCurrentDataMethod = nodeType.GetMethod("GetCurrentData");
                    if (getCurrentDataMethod != null)
                    {
                        var currentData = getCurrentDataMethod.Invoke(stateNode, null);
                        if (currentData != null)
                        {
                            foreach (var field in fields)
                            {
                                var value = field.GetValue(currentData);
                                EditorGUILayout.LabelField(field.Name, value != null ? value.ToString() : "null");
                            }

                            foreach (var property in properties)
                            {
                                if (property.CanRead)
                                {
                                    var value = property.GetValue(currentData);
                                    EditorGUILayout.LabelField(property.Name, value != null ? value.ToString() : "null");
                                }
                            }
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }
        }
    }
}
#endif