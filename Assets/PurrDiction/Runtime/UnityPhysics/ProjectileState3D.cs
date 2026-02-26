using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct ProjectileState3D : IPredictedData<ProjectileState3D>, IDuplicate<ProjectileState3D>
    {
        public Vector3 velocity;
        public float gravity;
        public float radius;
        public bool isTrigger;
        public DisposableList<PredictedComponentID> overlappingTriggers;
        public PredictedComponentID lastSolidContact;
        public bool hasLastSolidContact;

        public void Dispose()
        {
            overlappingTriggers.Dispose();
        }

        public ProjectileState3D Duplicate()
        {
            return new ProjectileState3D
            {
                velocity = velocity,
                gravity = gravity,
                radius = radius,
                isTrigger = isTrigger,
                overlappingTriggers = overlappingTriggers.isDisposed ? DisposableList<PredictedComponentID>.Create(8) : overlappingTriggers.Duplicate(),
                lastSolidContact = lastSolidContact,
                hasLastSolidContact = hasLastSolidContact
            };
        }

        public override string ToString()
        {
            return $"Velocity: {velocity}\nGravity: {gravity}\nRadius: {radius}\nIsTrigger: {isTrigger}";
        }
    }
}