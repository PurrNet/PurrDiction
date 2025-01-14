using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public readonly struct PredictedDestroy : IPackedAuto
    {
        public readonly int prefabId;
        public readonly PredictedObjectID instanceId;
        
        public PredictedDestroy(int prefabId, PredictedObjectID instanceId)
        {
            this.instanceId = instanceId;
            this.prefabId = prefabId;
        }

        public bool Matches(PredictedDestroy otherDestroyAction)
        {
            return prefabId == otherDestroyAction.prefabId && instanceId.Equals(otherDestroyAction.instanceId);
        }

        public override string ToString()
        {
            return $"Destroy:{prefabId}:{instanceId}";
        }
    }
}