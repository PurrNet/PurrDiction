using System;

namespace PurrNet.Prediction
{
    [Flags]
    public enum PredictionPhysicsProvider : byte
    {
        None,
        UnityPhysics3D = 1 << 0,
        UnityPhysics2D = 1 << 1,
        UnityPhysics = UnityPhysics3D | UnityPhysics2D,
    }
}
