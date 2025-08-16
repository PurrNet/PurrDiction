using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedHierarchyState : IPredictedData<PredictedHierarchyState>
    {
        public DisposableList<InstanceDetails> spawnedPrefabs;
        public DisposableList<PredictedObjectID> toDelete;
        public uint nextInstanceId;

        public PredictedHierarchyState(DisposableList<InstanceDetails> spawnedPrefabs, DisposableList<PredictedObjectID> toDelete, uint nextInstanceId)
        {
            this.spawnedPrefabs = spawnedPrefabs;
            this.nextInstanceId = nextInstanceId;
            this.toDelete = toDelete;
        }

        public void Dispose()
        {
            spawnedPrefabs.Dispose();
            toDelete.Dispose();
        }

        public override string ToString()
        {
            if (spawnedPrefabs.isDisposed)
                return $"nextInstanceId={nextInstanceId}";

            string actions = string.Empty;
            for (var i = 0; i < spawnedPrefabs.Count; i++)
            {
                var details = spawnedPrefabs[i];
                actions += $"(prefab: {details.prefabId}, id: {details.instanceId.instanceId})";
                if (i < spawnedPrefabs.Count - 1)
                    actions += "\n";
            }

            return $"nextInstanceId={nextInstanceId}\n{actions}";
        }
    }
}
