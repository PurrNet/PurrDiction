using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct SimpleWASDInput : IPackedAuto, IOptionalDispose
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
    
    public struct SimpleCCState : IPackedAuto, IOptionalDispose
    {
        public Vector3 position;
        public Vector3 velocity;
    }
}