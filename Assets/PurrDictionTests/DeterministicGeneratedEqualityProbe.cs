using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    // Keep this fixture in the runtime test assembly: PurrNet's postprocessor skips
    // assemblies whose name contains "Editor", so Editor-only states lack codegen.
    // The string selects generated field equality instead of unmanaged MemCmp.
    // The fixture supplies no custom equality, copying or serialization.
    public struct DeterministicGeneratedEqualityState : IPredictedData<DeterministicGeneratedEqualityState>
    {
        public string label;
        public Vector3 position;
        public void Dispose() { }
    }

    public sealed class DeterministicGeneratedEqualityProbe : DeterministicIdentity<DeterministicGeneratedEqualityState>
    {
        public void Attach(PredictionManager manager, PredictedComponentID componentId)
        {
            predictionManager = manager;
            id = componentId;
        }
    }
}
