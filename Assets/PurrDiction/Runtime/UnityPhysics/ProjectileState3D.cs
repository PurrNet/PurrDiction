using UnityEngine;

namespace PurrNet.Prediction
{
    public struct ProjectileState3D : IPredictedData<ProjectileState3D>
    {
        public Vector3 velocity;
        public float gravity;
        public float radius;
        public bool isTrigger;

        public override string ToString()
        {
            return $"Velocity: {velocity}\nGravity: {gravity}\nRadius: {radius}\nIsTrigger: {isTrigger}";
        }

        public void Dispose() { }
    }
}