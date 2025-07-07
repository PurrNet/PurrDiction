#if UNITY_EDITOR
using UnityEditor;

namespace PurrNet.Prediction.Editor
{
    class PredictedPrefabsPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported,string[] deleted,string[] moved,string[] movedFrom)
        {
            if(imported.Length==0 && deleted.Length==0 && moved.Length==0) return;
            foreach(var guid in AssetDatabase.FindAssets("t:PredictedPrefabs"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<PredictedPrefabs>(path);
                if(asset && asset.autoGenerate) asset.Generate();
            }
        }
    }
}
#endif
