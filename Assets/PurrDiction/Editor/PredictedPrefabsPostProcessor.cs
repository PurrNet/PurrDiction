#if UNITY_EDITOR
using System.Linq;
using UnityEditor;

namespace PurrNet.Prediction.Editor
{
    class PredictedPrefabsPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (importedAssets.Length == 0 && deletedAssets.Length == 0 && movedAssets.Length == 0)
                return;

            var all = AssetDatabase.FindAssets("t:PredictedPrefabs")
                .Select(guid => AssetDatabase.LoadAssetAtPath<PredictedPrefabs>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(n => n && n.autoGenerate)
                .ToArray();

            foreach (var predictedPrefabs in all)
            {
                string folderPath = AssetScannerUtility.ResolveFolderPath(
                    predictedPrefabs.folder, predictedPrefabs.searchAllIfNoFolder);

                if (string.IsNullOrEmpty(folderPath))
                    continue;

                if (HasRelevantChange(folderPath, importedAssets, deletedAssets, movedAssets, movedFromAssetPaths))
                    predictedPrefabs.Generate();
            }
        }

        private static bool HasRelevantChange(string folderPath, string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            return importedAssets.Any(p => p.StartsWith(folderPath)) ||
                   deletedAssets.Any(p => p.StartsWith(folderPath)) ||
                   movedAssets.Any(p => p.StartsWith(folderPath)) ||
                   movedFromAssetPaths.Any(p => p.StartsWith(folderPath));
        }
    }
}
#endif
