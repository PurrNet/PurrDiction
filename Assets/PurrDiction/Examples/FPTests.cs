using PurrNet.Prediction;
using UnityEngine;

namespace PurrDiction.Examples
{
    public struct FPVec3
    {
        public FP x;
        public FP y;
        public FP z;

        public FPVec3(FP x, FP y, FP z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public class FPTests : MonoBehaviour
    {
        [SerializeField] private FP _inspectorValue;

        static FPVec3 _vec3 = new FPVec3(0f, 0f, 0f);

        FPVec3 Gravity { get; set; } = new(0, -9.81f, 0);

        private void Awake()
        {
            Gravity  = new(0, -9.81f, 0);
            FP test = 5f;
            test -= 69f;
            test = 5f - test + _inspectorValue;

            Debug.Log(test);
            CallTest((float)69.0);
        }

        void CallTest(FP test)
        {
            Debug.Log(test);
        }
    }
}
