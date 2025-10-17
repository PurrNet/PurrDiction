using System.Threading;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class FreezeGame : MonoBehaviour
    {
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F))
            {
                Thread.Sleep(2000); // Freeze the game for 1 second
            }
        }
    }
}
