using System;
using JetBrains.Annotations;

namespace PurrNet.Prediction
{
    [Flags]
    public enum BuiltInSystems
    {
        [UsedImplicitly]
        None = 0,
        Physics3D = 1 << 0,
        Physics2D = 1 << 1,
        Time = 1 << 2,
        Hierarchy = 1 << 3,
        Players = 1 << 4
    }
}
