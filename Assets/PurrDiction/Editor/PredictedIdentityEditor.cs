using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PurrNet.Prediction.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(PredictedIdentity), true)]
#if TRI_INSPECTOR_PACKAGE
    public class PredictedIdentityEditor : TriInspector.Editors.TriEditor
#elif ODIN_INSPECTOR
    public class PredictedIdentityEditor : Sirenix.OdinInspector.Editor.OdinEditor
#else
    public class PredictedIdentityEditor : UnityEditor.Editor
#endif
    {
        static GUIStyle _box;

        const string OverridesVisibleKey = "PurrDiction.PredictedIdentityEditor.OverridesVisible";
        const string ModulesVisibleKey = "PurrDiction.PredictedIdentityEditor.ModulesVisible";

        static bool overridesVisible
        {
            get => SessionState.GetBool(OverridesVisibleKey, false);
            set => SessionState.SetBool(OverridesVisibleKey, value);
        }

        static bool showPredictedModules
        {
            get => SessionState.GetBool(ModulesVisibleKey, false);
            set => SessionState.SetBool(ModulesVisibleKey, value);
        }

        static readonly string[] _defaultOverrideProps = { "m_Script", "_predictionPolicySource", "_predictionPolicy" };
        static readonly string[] _defaultOverridePropsWithDesync = { "m_Script", "_predictionPolicySource", "_predictionPolicy", "_desyncPolicy" };

        public override VisualElement CreateInspectorGUI()
        {
            var inspector = EditorAttributesIntegration.CreateInspectorGUI(
                this, "_predictionPolicySource", "_predictionPolicy", "_desyncPolicy") ??
                AlchemyIntegration.CreateInspectorGUI(
                    this, "_predictionPolicySource", "_predictionPolicy", "_desyncPolicy");
            if (inspector == null)
                return null;

            var extras = new IMGUIContainer(() =>
            {
                if (!this || !target)
                    return;

                serializedObject.UpdateIfRequiredOrScript();
                var source = serializedObject.FindProperty("_predictionPolicySource");
                var policy = serializedObject.FindProperty("_predictionPolicy");
                if (source != null && policy != null)
                    DrawOverrideDefaults(source, policy, serializedObject.FindProperty("_desyncPolicy"));

                DrawInspectorExtras();
                serializedObject.ApplyModifiedProperties();
            });
            inspector.Add(extras);
            extras.schedule.Execute(extras.MarkDirtyRepaint).Every(100);
            return inspector;
        }

#if TRI_INSPECTOR_PACKAGE || ODIN_INSPECTOR
        protected override void OnDisable()
#else
        protected virtual void OnDisable()
#endif
        {
            EditorAttributesIntegration.OnDisable(this);
            AlchemyIntegration.OnDisable(this);
#if TRI_INSPECTOR_PACKAGE || ODIN_INSPECTOR
            base.OnDisable();
#endif
        }

        protected virtual void OnSceneGUI()
        {
            EditorAttributesIntegration.OnSceneGUI(this);
        }

        public override void OnInspectorGUI()
        {
#if TRI_INSPECTOR_PACKAGE || ODIN_INSPECTOR
            base.OnInspectorGUI();
#else
            var source = serializedObject.FindProperty("_predictionPolicySource");
            var policy = serializedObject.FindProperty("_predictionPolicy");

            if (source == null || policy == null)
            {
                base.OnInspectorGUI();
            }
            else
            {
                serializedObject.Update();
                var desync = serializedObject.FindProperty("_desyncPolicy");

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

                DrawPropertiesExcluding(
                    serializedObject,
                    desync != null ? _defaultOverridePropsWithDesync : _defaultOverrideProps);
                DrawOverrideDefaults(source, policy, desync);
                serializedObject.ApplyModifiedProperties();
            }
#endif

            DrawInspectorExtras();
        }

        protected virtual void DrawInspectorExtras()
        {
            GUILayout.Space(10);
            GUILayout.Label($"Predicted State", EditorStyles.boldLabel);

            _box ??= new GUIStyle("helpbox")
            {
                wordWrap = true,
                stretchWidth = true,
                stretchHeight = true,
                alignment = TextAnchor.UpperLeft
            };

            for (int i = 0; i < targets.Length; i++)
            {
                if (!targets[i] || targets[i] is not PredictedIdentity predictedIdentity)
                    continue;

                var extraContent = predictedIdentity.GetExtraString();
                var content = predictedIdentity.ToString();

                bool bothNonEmpty = !string.IsNullOrEmpty(extraContent) && !string.IsNullOrEmpty(content);

                if (Application.isPlaying)
                {
                    EditorGUILayout.BeginHorizontal("box", GUILayout.ExpandWidth(false));
                    try
                    {
                        GUILayout.Label($"ID: {predictedIdentity.id}", GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(
                            $"Owner ID: {(predictedIdentity.owner.HasValue ? predictedIdentity.owner.Value.ToString() : "None")}",
                            GUILayout.ExpandWidth(false));
                        GUILayout.FlexibleSpace();
                        var pm = predictedIdentity.predictionManager;
                        GUILayout.Label(
                            pm
                                ? $"Local Player: {(pm.localPlayer.HasValue ? pm.localPlayer.Value.ToString() : "None")}"
                                : "Not ready",
                            GUILayout.ExpandWidth(false));

                    }
                    catch
                    {
                        GUILayout.Label($"Not Spawned", GUILayout.ExpandWidth(false));
                    }

                    EditorGUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
                if (!string.IsNullOrEmpty(extraContent))
                {
                    if (bothNonEmpty)
                        GUILayout.Box(extraContent, _box, GUILayout.MinWidth(1));
                    else GUILayout.Box(extraContent, _box);
                }

                if (!string.IsNullOrEmpty(content))
                {
                    if (bothNonEmpty)
                        GUILayout.Box(content, _box, GUILayout.MinWidth(1));
                    else GUILayout.Box(content, _box);
                }
                GUILayout.EndHorizontal();

                DrawPredictedModules(predictedIdentity);

                if (predictedIdentity.GetType().GetCustomAttributes(typeof(PredictionUnsafeAttribute), true).Length > 0)
                {
                    EditorGUILayout.HelpBox("This identity is marked as PredictionUnsafe, which means use at your own risk.\n" +
                                            "It may not behave as expected or works against prediction.",
                        MessageType.Warning);
                }
            }
        }

        private void DrawOverrideDefaults(
            SerializedProperty source,
            SerializedProperty policy,
            SerializedProperty desync)
        {
            string label = "Override Defaults";

            bool sourceMixed = source.hasMultipleDifferentValues;
            bool sourceOverridden = source.enumValueIndex == (int)PredictionPolicySource.OverrideScope;
            bool desyncMixed = desync != null && desync.hasMultipleDifferentValues;
            bool desyncOverridden = desync != null && desync.enumValueIndex != 0;

            if (sourceMixed || sourceOverridden || desyncMixed || desyncOverridden)
            {
                label += " (";
                label += sourceMixed ? "P*" : (sourceOverridden ? "P" : "");
                if ((sourceMixed || sourceOverridden) && (desyncMixed || desyncOverridden))
                    label += ",";
                label += desyncMixed ? "D*" : (desyncOverridden ? "D" : "");
                label += ")";
            }

            bool expanded = EditorGUILayout.BeginFoldoutHeaderGroup(overridesVisible, label);
            if (expanded != overridesVisible)
                overridesVisible = expanded;

            if (expanded)
            {
                EditorGUI.indentLevel++;

                EditorGUI.showMixedValue = source.hasMultipleDifferentValues;
                EditorGUILayout.PropertyField(source, new GUIContent("Prediction Policy Source"));
                EditorGUI.showMixedValue = false;

                EditorGUI.showMixedValue = policy.hasMultipleDifferentValues;
                EditorGUILayout.PropertyField(policy, new GUIContent("Prediction Policy"));
                EditorGUI.showMixedValue = false;

                if (desync != null)
                {
                    EditorGUI.showMixedValue = desync.hasMultipleDifferentValues;
                    EditorGUILayout.PropertyField(desync, new GUIContent("Desync Policy"));
                    EditorGUI.showMixedValue = false;
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawPredictedModules(PredictedIdentity predictedIdentity)
        {
            var modules = predictedIdentity.modules;
            if (modules == null || modules.Count == 0)
                return;

            GUILayout.Space(5);
            bool expanded = EditorGUILayout.Foldout(
                showPredictedModules,
                $"Predicted Modules ({modules.Count})",
                true);
            if (expanded != showPredictedModules)
                showPredictedModules = expanded;

            if (!expanded)
                return;

            for (int i = 0; i < modules.Count; i++)
            {
                var module = modules[i];
                if (module == null)
                    continue;

                var content = module.ToString();
                var moduleName = module.GetType().Name;
                if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(moduleName))
                    continue;

                EditorGUILayout.BeginVertical("box");

                var moduleDisplayName = $"{moduleName} [#{module.moduleIndex}]";
                EditorGUILayout.LabelField(moduleDisplayName, EditorStyles.boldLabel);
                GUILayout.Box(content, _box, GUILayout.ExpandWidth(true));

                EditorGUILayout.EndVertical();
            }
        }
    }
}
