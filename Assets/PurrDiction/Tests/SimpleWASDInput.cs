using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct SimpleWASDInput : IPredictedData
    {
        public float horizontal;
        public float vertical;
        public bool jump;
        public bool dash;

        public override string ToString()
        {
            return $"(horizontal: {horizontal}, vertical: {vertical}, jump: {jump}, dash: {dash})";
        }
    }
    
    public struct SimpleCCState : IPredictedData<SimpleCCState>
    {
        public Vector3 linearVelocity;
        public Vector3 angularVelocity;
        public bool wasShooting;
    }
}