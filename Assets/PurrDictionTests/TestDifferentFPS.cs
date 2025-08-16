using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class TestDifferentFPS : MonoBehaviour
    {
        [SerializeField] private int _fps = 60;

        private void Update()
        {
            if (Application.targetFrameRate == _fps)
                return;
            
            Application.targetFrameRate = _fps;
        }
    }
}
