#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace PurrNet.Prediction.Editor
{
    [CustomEditor(typeof(PredictedPrefabs))]
    public class PredictedPrefabsEditor : UnityEditor.Editor
    {
        private PredictedPrefabs _target;
        private SerializedProperty _linkedPredictedPrefabs;
        private SerializedProperty _prefabs;
        private SerializedProperty _folderProp;
        private ReorderableList _reorderableList;

        private const float SPACING = 8f;
        private const float REORDERABLE_LIST_BUTTON_WIDTH = 25f;

        private void OnEnable()
        {
            _target = (PredictedPrefabs)target;
            _linkedPredictedPrefabs = serializedObject.FindProperty("linkedPredictedPrefabs");
            _prefabs = serializedObject.FindProperty("prefabs");
            _folderProp = serializedObject.FindProperty("_folder");

            if (_target.autoGenerate)
                _target.Generate();

            SetupReorderableList();
        }

        private void SetupReorderableList()
        {
            _reorderableList = new ReorderableList(serializedObject, _prefabs, true, true, true, true);
            _reorderableList.elementHeight = EditorGUIUtility.singleLineHeight;

            _reorderableList.drawHeaderCallback = (Rect rect) =>
            {
                float fullWidth = rect.width - REORDERABLE_LIST_BUTTON_WIDTH;
                CalculateWidths(fullWidth, out float prefabWidth, out float poolWidth, out float warmupWidth);

                EditorGUI.LabelField(new Rect(rect.x, rect.y, prefabWidth, rect.height), "Prefab");
                EditorGUI.LabelField(
                    new Rect(rect.x + prefabWidth + SPACING, rect.y, poolWidth + warmupWidth, rect.height), "Pool");
            };

            _reorderableList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                SerializedProperty element = _prefabs.GetArrayElementAtIndex(index);
                SerializedProperty prefabProp = element.FindPropertyRelative("prefab");
                SerializedProperty poolProp = element.FindPropertyRelative("pooled");
                SerializedProperty warmupCountProp = element.FindPropertyRelative("warmupCount");

                float fullWidth = rect.width - REORDERABLE_LIST_BUTTON_WIDTH;
                CalculateWidths(fullWidth, out float prefabWidth, out float poolWidth, out float warmupWidth);

                EditorGUI.BeginDisabledGroup(_target.autoGenerate);
                EditorGUI.PropertyField(new Rect(rect.x, rect.y, prefabWidth, rect.height), prefabProp,
                    GUIContent.none);
                EditorGUI.EndDisabledGroup();

                poolProp.boolValue =
                    EditorGUI.Toggle(new Rect(rect.x + prefabWidth + SPACING, rect.y, poolWidth, rect.height),
                        poolProp.boolValue);

                if (poolProp.boolValue)
                {
                    EditorGUI.PropertyField(
                        new Rect(rect.x + prefabWidth + poolWidth + (SPACING * 2), rect.y, warmupWidth, rect.height),
                        warmupCountProp, GUIContent.none);
                }
            };

            _reorderableList.onAddDropdownCallback = (Rect buttonRect, ReorderableList list) =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Add Empty Entry"), false, () =>
                {
                    int index = list.count;
                    list.serializedProperty.arraySize++;
                    var element = list.serializedProperty.GetArrayElementAtIndex(index);
                    element.FindPropertyRelative("prefab").objectReferenceValue = null;
                    element.FindPropertyRelative("pooled").boolValue = _target.poolByDefault;
                    element.FindPropertyRelative("warmupCount").intValue = 5;
                    element.FindPropertyRelative("guid").stringValue = string.Empty;
                    serializedObject.ApplyModifiedProperties();
                });

                menu.AddItem(new GUIContent("Add Selected Prefabs"), false, () =>
                {
                    bool addedAny = false;
                    foreach (var obj in Selection.gameObjects)
                    {
                        if (PrefabUtility.IsPartOfPrefabAsset(obj))
                        {
                            addedAny = true;
                            int index = list.count;
                            list.serializedProperty.arraySize++;
                            var element = list.serializedProperty.GetArrayElementAtIndex(index);
                            element.FindPropertyRelative("prefab").objectReferenceValue = obj;
                            element.FindPropertyRelative("pooled").boolValue = _target.poolByDefault;
                            element.FindPropertyRelative("warmupCount").intValue = 5;
                            element.FindPropertyRelative("guid").stringValue =
                                AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(obj));
                        }
                    }

                    if (addedAny)
                    {
                        serializedObject.ApplyModifiedProperties();
                    }
                });

                menu.ShowAsContext();
            };
        }

        private void CalculateWidths(float fullWidth, out float prefabWidth, out float poolWidth, out float warmupWidth)
        {
            poolWidth = 20f;
            warmupWidth = 60f;
            prefabWidth = fullWidth - poolWidth - warmupWidth - (SPACING * 2);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SharedAssetEditorUI.DrawHeader(
                "Predicted Prefabs",
                "This asset stores prefabs containing a Predicted Identity. " +
                "You can add prefabs manually or auto generate the references. " +
                "This list is used by the Prediction Manager to spawn predicted prefabs.");

            SharedAssetEditorUI.DrawGenerationSettingsTop(_folderProp, _target);

            GUILayout.BeginHorizontal();
            DrawToggleButton("Auto generate", ref _target.autoGenerate);
            DrawToggleButton("Default pooling", ref _target.poolByDefault);
            GUILayout.EndHorizontal();

            SharedAssetEditorUI.DrawGenerateButton(() =>
            {
                _target.Generate();
                serializedObject.ApplyModifiedProperties();
                _prefabs = serializedObject.FindProperty("prefabs");
            });

            SharedAssetEditorUI.DrawLinkedField(_linkedPredictedPrefabs);

            SharedAssetEditorUI.DrawEntryList(_reorderableList, _target.autoGenerate);

            serializedObject.ApplyModifiedProperties();

            if (GUI.changed)
            {
                EditorUtility.SetDirty(_target);
            }
        }

        private void DrawToggleButton(string label, ref bool value)
        {
            value = SharedAssetEditorUI.DrawToggleButton(label, value, _target, () =>
            {
                if (_target.autoGenerate)
                {
                    _target.Generate();
                    serializedObject.ApplyModifiedProperties();
                    _prefabs = serializedObject.FindProperty("prefabs");
                }
            });
        }
    }
}
#endif
