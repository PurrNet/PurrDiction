using PurrNet.Pooling;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Prediction.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(PredictedTransform), true)]
    public class PredictedTransformEditor : PredictedIdentityEditor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            for (int i = 0; i < targets.Length; i++)
            {
                if (!targets[i] || targets[i] is not PredictedTransform predictedTransform)
                    continue;

                var graphics = predictedTransform.graphics;

                if (!graphics)
                    continue;

                var colliderList = ListPool<Collider>.Instantiate();
                var rigidbodiesList = ListPool<Rigidbody>.Instantiate();
                var identitiesList = ListPool<PredictedIdentity>.Instantiate();
                var all = ListPool<Object>.Instantiate();

                graphics.GetComponentsInChildren(true, colliderList);
                graphics.GetComponentsInChildren(true, rigidbodiesList);
                graphics.GetComponentsInChildren(true, identitiesList);

                for (int j = 0; j < identitiesList.Count; j++)
                    all.Add(identitiesList[j]);
                for (int j = 0; j < colliderList.Count; j++)
                    all.Add(colliderList[j]);
                for (int j = 0; j < rigidbodiesList.Count; j++)
                    all.Add(rigidbodiesList[j]);

                if (all.Count > 0)
                {
                    var oldCol = GUI.backgroundColor;
                    GUI.backgroundColor = Color.red;
                    EditorGUILayout.BeginVertical("helpbox");
                    GUI.backgroundColor = oldCol;

                    EditorGUILayout.LabelField("Graphical Object Contains Potentially Invalid Components");

                    for (int j = 0; j < all.Count; j++)
                        EditorGUILayout.ObjectField(all[j], all[j].GetType(), true);

                    if (GUILayout.Button("Remove"))
                    {
                        int maxIterations = 20;
                        while (all.Count > 0 && maxIterations > 0)
                        {
                            try
                            {
                                Undo.IncrementCurrentGroup();
                                for (int j = 0; j < all.Count; j++)
                                {
                                    if (all[i])
                                    {
                                        EditorUtility.SetDirty(all[j]);
                                        Undo.DestroyObjectImmediate(all[j]);
                                        all.RemoveAt(j--);
                                    }
                                }
                            }
                            catch
                            {
                                // ignored
                            }

                            maxIterations--;
                        }

                        EditorUtility.SetDirty(graphics);
                    }

                    EditorGUILayout.EndVertical();
                }

                ListPool<Collider>.Destroy(colliderList);
                ListPool<Rigidbody>.Destroy(rigidbodiesList);
                ListPool<PredictedIdentity>.Destroy(identitiesList);
                ListPool<Object>.Destroy(all);
            }
        }
    }
}
