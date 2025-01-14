using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public readonly struct PredictedInstantiate : IPackedAuto
    {
        public readonly int prefabId;
        public readonly PredictedObjectID instanceId;
        
        public PredictedInstantiate(int prefabId, PredictedObjectID instanceId)
        {
            this.prefabId = prefabId;
            this.instanceId = instanceId;
        }

        public bool Matches(PredictedInstantiate otherInstantiateAction)
        {
            return prefabId == otherInstantiateAction.prefabId && instanceId.Equals(otherInstantiateAction.instanceId);
        }

        public override string ToString()
        {
            return $"Instantiate:{prefabId}:{instanceId}";
        }
    }
}