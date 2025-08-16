using JetBrains.Annotations;

namespace PurrNet.Prediction
{
    public enum UpdateViewMode : byte
    {
        [UsedImplicitly]
        None,
        Update,
        LateUpdate
    }
}