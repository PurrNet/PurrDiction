#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Collections.Generic;
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
            UnityEngine.Object oldValue = property.objectReferenceValue;
            UnityEngine.Object newValue = EditorGUI.ObjectField(position, label, oldValue, typeof(UnityEngine.Object), true);
            
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
        
        private class StateCache
        {
            public string CurrentStateName;
            public string PreviousStateName;
            public string NextStateName;
            public Dictionary<string, string> StateData = new Dictionary<string, string>();
        }
        
        private StateCache _cachedState = new StateCache();
        private double _lastUpdateTime;
        private const double UPDATE_INTERVAL = 0.1;

        private void OnEnable()
        {
            _stateMachine = target as PredictedStateMachine;
            _statesProperty = serializedObject.FindProperty("_wrappedStates");
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (!Application.isPlaying || !target)
                return;

            double currentTime = EditorApplication.timeSinceStartup;
            if (currentTime - _lastUpdateTime < UPDATE_INTERVAL)
                return;

            _lastUpdateTime = currentTime;

            if (HasStateChanged())
            {
                Repaint();
            }
        }

        private bool HasStateChanged()
        {
            if (_stateMachine._currentStateNode == null)
                return _cachedState.CurrentStateName != null;

            bool hasChanged = false;

            string currentStateName = _stateMachine._currentStateNode.GetType().Name;
            string previousStateName = _stateMachine._previousStateNode?.GetType().Name ?? "None";
            string nextStateName = _stateMachine._nextStateNode?.GetType().Name ?? "None";

            if (_cachedState.CurrentStateName != currentStateName ||
                _cachedState.PreviousStateName != previousStateName ||
                _cachedState.NextStateName != nextStateName)
            {
                hasChanged = true;
            }

            var currentData = GetCurrentStateData();
            if (currentData != null)
            {
                foreach (var kvp in currentData)
                {
                    string cachedValue;
                    if (!_cachedState.StateData.TryGetValue(kvp.Key, out cachedValue) ||
                        cachedValue != kvp.Value)
                    {
                        hasChanged = true;
                        break;
                    }
                }
            }

            if (hasChanged)
            {
                _cachedState.CurrentStateName = currentStateName;
                _cachedState.PreviousStateName = previousStateName;
                _cachedState.NextStateName = nextStateName;
                _cachedState.StateData = currentData ?? new Dictionary<string, string>();
            }

            return hasChanged;
        }

        private Dictionary<string, string> GetCurrentStateData()
        {
            if (_stateMachine._currentStateNode == null)
                return null;

            var result = new Dictionary<string, string>();
            var nodeType = _stateMachine._currentStateNode.GetType();
            var genericInterface = nodeType.GetInterfaces()[0];
            var dataTypes = genericInterface.GetGenericArguments();
            
            if (dataTypes.Length == 0)
                return result;

            var dataType = dataTypes[0];
            var getCurrentDataMethod = nodeType.GetMethod("GetCurrentData");
            
            if (getCurrentDataMethod == null)
                return result;

            var currentData = getCurrentDataMethod.Invoke(_stateMachine._currentStateNode, null);
            if (currentData == null)
                return result;

            foreach (var field in dataType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var value = field.GetValue(currentData);
                result[field.Name] = value?.ToString() ?? "null";
            }

            foreach (var property in dataType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanRead)
                {
                    var value = property.GetValue(currentData);
                    result[property.Name] = value?.ToString() ?? "null";
                }
            }

            return result;
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
                    EditorGUILayout.LabelField("Current State:", _cachedState.CurrentStateName);
                    
                    DrawStateData();
                    
                    EditorGUILayout.LabelField("Previous State:", _cachedState.PreviousStateName);
                    EditorGUILayout.LabelField("Next State:", _cachedState.NextStateName);
                }
                else
                {
                    EditorGUILayout.LabelField("Current State: None");
                }
            }
        }

        private void DrawStateData()
        {
            if (_cachedState.StateData.Count == 0)
                return;

            EditorGUI.indentLevel++;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("State Data:", EditorStyles.boldLabel);

                foreach (var kvp in _cachedState.StateData)
                {
                    EditorGUILayout.LabelField(kvp.Key, kvp.Value);
                }
            }
            EditorGUI.indentLevel--;
        }
    }
}
#endif