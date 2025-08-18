using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using System.IO;
using PurrNet.Logging;
using PurrNet.Utils;
using UnityEditor;
#endif

namespace PurrNet.Prediction
{
    [CreateAssetMenu(fileName = "PredictedPrefabs", menuName = "PurrNet/Purrdiction/PredictedPrefabs", order = -401)]
    public class PredictedPrefabs : ScriptableObject
    {
        [SerializeField] private bool _autoGenerate = true;
        [SerializeField] private Object _folder;

        [SerializeField, UsedImplicitly] private PoolSettings _defaultPooling = new PoolSettings
        {
            usePooling = false,
            initialSize = 0
        };

        [SerializeField, HideInInspector, Obsolete, UsedImplicitly] private List<GameObject> _prefabs = new ();
        [SerializeField] private List<PredictedPrefab> _newPrefabs = new ();

        private bool _generating;
        public bool autoGenerate => _autoGenerate;
        public List<PredictedPrefab> prefabs => _newPrefabs;

#if UNITY_EDITOR
        private void OnValidate()
        {
#pragma warning disable CS0612
            if (_prefabs.Count > 0)
            {
                for (int i = _prefabs.Count - 1; i >= 0; i--)
                {
                    _newPrefabs.Add(new PredictedPrefab
                    {
                        prefab = _prefabs[i],
                        pooling = _defaultPooling
                    });
                }
                _prefabs.Clear();
                EditorUtility.SetDirty(this);
            }
#pragma warning restore CS0612

            if (_autoGenerate)
            {
                // schedule for next editor update
                EditorApplication.delayCall += Generate;
            }
        }
#endif

        /// <summary>
        /// Editor only method to generate network prefabs from a specified folder.
        /// </summary>
        [UsedImplicitly]
        public void Generate()
        {
#if UNITY_EDITOR
            if (ApplicationContext.isClone)
                return;

            // if somehow this got called on a destroyed object
            if (!this) return;

            if (_generating) return;

            _generating = true;

            try
            {
                EditorUtility.DisplayProgressBar("Getting Network Prefabs", "Checking existing...", 0f);

                if (_folder == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(_folder)))
                {
                    EditorUtility.DisplayProgressBar("Getting Network Prefabs", "No folder found, defaulting to Assets/", 0f);
                    _folder = AssetDatabase.LoadAssetAtPath<Object>("Assets");
                }

                EditorUtility.DisplayProgressBar("Getting Network Prefabs", "Found folder...", 0f);
                string folderPath = AssetDatabase.GetAssetPath(_folder);

                if (string.IsNullOrEmpty(folderPath))
                {
                    EditorUtility.DisplayProgressBar("Getting Network Prefabs", "No folder path...", 0f);

                    if (_autoGenerate && _newPrefabs.Count > 0)
                    {
                        _newPrefabs.Clear();
                        EditorUtility.SetDirty(this);
                        AssetDatabase.SaveAssets();
                    }

                    EditorUtility.ClearProgressBar();
                    _generating = false;
                    PurrLogger.LogError("Exiting Generate method early due to empty folder path.");
                    return;
                }

                EditorUtility.DisplayProgressBar("Getting Network Prefabs", "Getting existing paths...", 0f);

                var existingPaths = new HashSet<string>();
                foreach (var prefabData in _newPrefabs)
                    existingPaths.Add(AssetDatabase.GetAssetPath(prefabData.prefab));

                EditorUtility.DisplayProgressBar("Getting Network Prefabs", "Finding paths...", 0.1f);

                var foundPrefabs = new List<GameObject>();
                var identities = new List<PredictedIdentity>();
                string[] guids = AssetDatabase.FindAssets("t:prefab", new[] { folderPath });
                for (var i = 0; i < guids.Length; i++)
                {
                    var guid = guids[i];
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);

                    if (prefab)
                    {
                        EditorUtility.DisplayProgressBar("Getting Network Prefabs", $"Looking at {prefab.name}",
                            0.1f + 0.7f * ((i + 1f) / guids.Length));

                        prefab.GetComponentsInChildren(true, identities);

                        if (identities.Count > 0)
                            foundPrefabs.Add(prefab);
                    }
                }

                EditorUtility.DisplayProgressBar("Getting Network Prefabs", "Sorting...", 0.9f);

                foundPrefabs.Sort((a, b) =>
                {
                    string pathA = AssetDatabase.GetAssetPath(a);
                    string pathB = AssetDatabase.GetAssetPath(b);
                    var guidA = AssetDatabase.GUIDFromAssetPath(pathA);
                    var guidB = AssetDatabase.GUIDFromAssetPath(pathB);
                    return string.Compare(guidA.ToString(), guidB.ToString(), StringComparison.Ordinal);
                });

                EditorUtility.DisplayProgressBar("Getting Network Prefabs", "Removing invalid prefabs...", 0.95f);

                int removed = _newPrefabs.RemoveAll(prefabData =>
                    !prefabData.prefab || !File.Exists(AssetDatabase.GetAssetPath(prefabData.prefab)));

                for (int i = 0; i < _newPrefabs.Count; i++)
                {
                    if (!foundPrefabs.Contains(_newPrefabs[i].prefab))
                    {
                        _newPrefabs.RemoveAt(i);
                        removed++;
                        i--;
                    }
                }

                int added = 0;
                foreach (var foundPrefab in foundPrefabs)
                {
                    var foundPath = AssetDatabase.GetAssetPath(foundPrefab);
                    if (!existingPaths.Contains(foundPath))
                    {
                        _newPrefabs.Add(new PredictedPrefab
                        {
                            prefab = foundPrefab,
                            pooling = _defaultPooling
                        });
                        added++;
                    }
                }

                if (removed > 0 || added > 0)
                {
                    EditorUtility.SetDirty(this);
                    AssetDatabase.SaveAssets();
                }
            }
            catch (Exception e)
            {
                PurrLogger.LogError($"An error occurred during prefab generation: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _generating = false;
            }
#endif
        }
    }
}
