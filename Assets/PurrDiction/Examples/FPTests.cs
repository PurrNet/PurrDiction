using PurrNet.Prediction;
using UnityEngine;

namespace PurrDiction.Examples
{
    public class FPTests : MonoBehaviour
    {
        [SerializeField] private FP _inspectorValue;

        private void Awake()
        {
            FP test = 5f;
            test -= 69f;
            test = 5f - test + _inspectorValue;

            Debug.Log(test);
        }
    }
}
