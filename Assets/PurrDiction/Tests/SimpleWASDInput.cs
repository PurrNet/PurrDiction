using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public struct SimpleWASDInput : IPackedAuto, IState
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
    
    public struct SimpleCCState : IPackedAuto, IState
    {
        public Vector3 position;
        public Vector3 velocity;
        
        /*public SimpleCCState Add(SimpleCCState a, SimpleCCState b)
        {
            return new SimpleCCState
            {
                position = a.position + b.position,
                velocity = a.velocity + b.velocity
            };
        }

        public SimpleCCState Subtract(SimpleCCState a, SimpleCCState b)
        {
            return new SimpleCCState
            {
                position = a.position - b.position,
                velocity = a.velocity - b.velocity
            };
        }*/
    }
}