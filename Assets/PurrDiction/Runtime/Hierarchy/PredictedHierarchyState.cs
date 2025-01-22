using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedHierarchyState : IPredictedData<PredictedHierarchyState>
    {
        public DisposableList<InstanceDetails> spawnedPrefabs;
        public readonly int nextInstanceId;
        
        public PredictedHierarchyState(DisposableList<InstanceDetails> spawnedPrefabs, int nextInstanceId)
        {
            this.spawnedPrefabs = spawnedPrefabs;
            this.nextInstanceId = nextInstanceId;
        }

        public void Dispose() => spawnedPrefabs.Dispose();

        public override string ToString()
        {
            if (spawnedPrefabs.isDisposed)
                return $"PredictedHierarchyState(actions=DISPOSED, nextInstanceId={nextInstanceId})";
            
            string actions = string.Empty;
            for (var i = 0; i < spawnedPrefabs.Count; i++)
            {
                var details = spawnedPrefabs[i];
                actions += $"({details.prefabId}, {details.instanceId})";
                if (i < spawnedPrefabs.Count - 1)
                    actions += ", ";
            }
            
            return $"PredictedHierarchyState(actions=[{actions}], nextInstanceId={nextInstanceId})";
        }
    }
}