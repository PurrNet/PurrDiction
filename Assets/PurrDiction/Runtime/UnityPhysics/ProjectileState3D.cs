using UnityEngine;

namespace PurrNet.Prediction
{
    public struct ProjectileState3D : IPredictedData<ProjectileState3D>
    {
        public Vector3 velocity;
        public float gravity;
        public float radius;
        public bool isTrigger;
        
        public void Dispose()
        {
            
        }
    }
}