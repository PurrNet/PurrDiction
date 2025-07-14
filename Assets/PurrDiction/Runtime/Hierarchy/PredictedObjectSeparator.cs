using UnityEngine;

namespace PurrNet.Prediction
{
    public sealed class PredictedObjectSeparator : MonoBehaviour
    {

    }
}

#if UNITY_EDITOR
namespace PurrNet.Prediction.Editor
{
    [UnityEditor.CustomEditor(typeof(PredictedObjectSeparator))]
    public class PredictedObjectSeparatorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            UnityEditor.EditorGUILayout.HelpBox("This will assign this Gameobject and it's children a new `PredictedObjectID` such that you can" +
                                                " delete this sub-part individually of the parent object.", UnityEditor.MessageType.Info);
        }
    }
}
#endif
