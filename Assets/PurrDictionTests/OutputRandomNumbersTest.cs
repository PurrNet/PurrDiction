using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class OutputRandomNumbersTest : MonoBehaviour
    {
        public PredictedRandom random;
        public int Seed = 12345;
        public int NumberToOutput = 10;
        void Awake()
        {
            random = PredictedRandom.Create((uint)Seed);
        }
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F))
            {
                OutputRandomFloats();
            }
            if (Input.GetKeyDown(KeyCode.G))
            {
                OutputRandomSFloats();
            }
            if (Input.GetKeyDown(KeyCode.H))
            {
                OutputRandomFPs();
            }
        }
        void OutputRandomFloats()
        {
            Debug.Log($"Outputting {NumberToOutput} random floats.");
            for (int i = 0; i < NumberToOutput; i++)
            {
                Debug.Log(random.NextFloat());
            }
            Debug.Log($"Finished random float test.");
        }
        void OutputRandomSFloats()
        {
            Debug.Log($"Outputting {NumberToOutput} random sfloats.");
            for (int i = 0; i < NumberToOutput; i++)
            {
                Debug.Log(random.NextSFloat());
            }
            Debug.Log($"Finished random sfloat test.");
        }
        void OutputRandomFPs()
        {
            Debug.Log($"Outputting {NumberToOutput} random FPs.");
            for (int i = 0; i < NumberToOutput; i++)
            {
                Debug.Log(random.NextFP());
            }
            Debug.Log($"Finished random FP test.");
        }
    }
}
