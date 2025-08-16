using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SomeTestsForPrediction : NetworkIdentity
    {
        [SerializeField] private SyncVar<int> _testVar = new SyncVar<int>(ownerAuth: true);

        [PurrButton]
        public void Inc()
        {
            _testVar.value++;
        }

        [PurrButton]
        public void Dec()
        {
            _testVar.value--;
        }
    }
}
