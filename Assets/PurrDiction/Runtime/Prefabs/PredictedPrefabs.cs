using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using PurrNet.Logging;
using PurrNet.Utils;
using UnityEditor;
#endif

namespace PurrNet.Prediction
{
    [CreateAssetMenu(fileName = "PredictedPrefabs", menuName = "PurrNet/Purrdiction/PredictedPrefabs", order = -401)]
    public class PredictedPrefabs : ScriptableObject
    {
        [FormerlySerializedAs("_autoGenerate")]
        public bool autoGenerate = true;
        public bool poolByDefault;

        [SerializeField] private Object _folder;
        [Tooltip("When no folder is set, search all of Assets/ instead of doing nothing.")]
        public bool searchAllIfNoFolder = true;

        [Tooltip("Will also get prefabs from these linked assets. This is to allow further organization.")]
        public List<PredictedPrefabs> linkedPredictedPrefabs = new();

        public List<PredictedPrefab> prefabs = new();

        // Legacy migration fields
#pragma warning disable CS0612
        [SerializeField, HideInInspector, Obsolete, UsedImplicitly] private List<GameObject> _prefabs = new();

        [Serializable]
        private struct LegacyPredictedPrefab
        {
            public GameObject prefab;
            public PoolSettings pooling;
        }

        [SerializeField, HideInInspector, Obsolete, UsedImplicitly] private List<LegacyPredictedPrefab> _newPrefabs = new();
#pragma warning restore CS0612

        public Object folder
        {
            get => _folder;
            set => _folder = value;
        }

        private bool _generating;

#if UNITY_EDITOR
        private void OnValidate()
        {
#pragma warning disable CS0612
            // Migrate from oldest format (plain GameObject list)
            if (_prefabs.Count > 0)
            {
                for (int i = _prefabs.Count - 1; i >= 0; i--)
                {
                    prefabs.Add(new PredictedPrefab
                    {
                        prefab = _prefabs[i],
                        pooled = poolByDefault,
                        warmupCount = 5
                    });
                }
                _prefabs.Clear();
                EditorUtility.SetDirty(this);
            }

            // Migrate from intermediate format (LegacyPredictedPrefab with PoolSettings)
            if (_newPrefabs.Count > 0)
            {
                for (int i = 0; i < _newPrefabs.Count; i++)
                {
                    var legacy = _newPrefabs[i];
                    prefabs.Add(new PredictedPrefab
                    {
                        prefab = legacy.prefab,
                        pooled = legacy.pooling.usePooling,
                        warmupCount = legacy.pooling.initialSize
                    });
                }
                _newPrefabs.Clear();
                EditorUtility.SetDirty(this);
            }
#pragma warning restore CS0612

            if (autoGenerate)
            {
                EditorApplication.delayCall += Generate;
            }
        }
#endif

        /// <summary>
        /// Editor only method to generate predicted prefabs from a specified folder.
        /// </summary>
        [UsedImplicitly]
        public void Generate()
        {
#if UNITY_EDITOR
            if (ApplicationContext.isClone)
                return;

            if (!this) return;
            if (_generating) return;

            _generating = true;

            try
            {
                EditorUtility.DisplayProgressBar("Getting Predicted Prefabs", "Scanning...", 0.1f);

                string resolvedPath = AssetScannerUtility.ResolveFolderPath(_folder, searchAllIfNoFolder);

                if (string.IsNullOrEmpty(resolvedPath))
                {
                    if (autoGenerate && prefabs.Count > 0)
                    {
                        prefabs.Clear();
                        EditorUtility.SetDirty(this);
                        AssetDatabase.SaveAssets();
                    }

                    return;
                }

                // Scan for prefabs with PredictedIdentity (can't use ScanPrefabs which filters by NetworkIdentity)
                var found = new List<AssetScannerUtility.ScanResult>();
                string[] guids = AssetDatabase.FindAssets("t:prefab", new[] { resolvedPath });
                var identities = new List<PredictedIdentity>();

                for (int i = 0; i < guids.Length; i++)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (!prefab) continue;

                    EditorUtility.DisplayProgressBar("Getting Predicted Prefabs",
                        $"Looking at {prefab.name}",
                        0.1f + 0.6f * ((i + 1f) / guids.Length));

                    identities.Clear();
                    prefab.GetComponentsInChildren(true, identities);
                    if (identities.Count == 0) continue;

                    found.Add(new AssetScannerUtility.ScanResult
                    {
                        asset = prefab,
                        guid = guids[i],
                        assetPath = assetPath
                    });
                }

                found.Sort(AssetScannerUtility.CompareByGuid);

                EditorUtility.DisplayProgressBar("Getting Predicted Prefabs", "Syncing...", 0.8f);

                // Update GUIDs on existing entries
                for (int i = 0; i < prefabs.Count; i++)
                {
                    if (!prefabs[i].prefab) continue;
                    var path = AssetDatabase.GetAssetPath(prefabs[i].prefab);
                    var g = AssetDatabase.AssetPathToGUID(path);
                    if (prefabs[i].guid != g)
                    {
                        var p = prefabs[i];
                        p.guid = g;
                        prefabs[i] = p;
                        EditorUtility.SetDirty(this);
                    }
                }

                var (added, removed) = AssetScannerUtility.SyncEntries(
                    prefabs,
                    found,
                    e => e.guid,
                    e => e.prefab,
                    scan => new PredictedPrefab
                    {
                        prefab = (GameObject)scan.asset,
                        pooled = poolByDefault,
                        warmupCount = 5,
                        guid = scan.guid
                    },
                    e => e.prefab);

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
