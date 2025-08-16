using System;

namespace PurrNet.Prediction
{
    [Flags]
    public enum PredictionPhysicsProvider : byte
    {
        UnityPhysics3D = 1 << 0,
        UnityPhysics2D = 1 << 1
    }
}
