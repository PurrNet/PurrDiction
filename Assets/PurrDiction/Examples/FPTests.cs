using PurrNet.Prediction;
using UnityEngine;

namespace PurrDiction.Examples
{
    public class FPTests : MonoBehaviour
    {
        private void Awake()
        {
            FP test = 5f;
            test -= 69f;
            test = 5f - test;

            test %= 69f;
            test = 5f % test;

            Debug.Log(test);
        }
    }
}
