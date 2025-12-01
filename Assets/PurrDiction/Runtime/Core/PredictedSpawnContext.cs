using System;
using System.Collections.Generic;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Context passed to initializers before PredictedIdentity registration.
    /// Contains all information needed for DI decisions.
    /// </summary>
    public readonly struct PredictedSpawnContext
    {
        public readonly PredictionManager PredictionManager;
        public readonly PredictedObjectID ObjectId;
        public readonly PlayerID? Owner;
        public readonly bool IsServer;
        public readonly bool IsClient;
        public readonly bool IsLocalPlayer;

        public PredictedSpawnContext(
            PredictionManager predictionManager,
            PredictedObjectID objectId,
            PlayerID? owner)
        {
            PredictionManager = predictionManager;
            ObjectId = objectId;
            Owner = owner;
            IsServer = predictionManager.cachedIsServer;
            IsClient = predictionManager.isClient;
            IsLocalPlayer = owner.HasValue &&
                            predictionManager.isSpawned &&
                            owner == predictionManager.localPlayer;
        }
    }
}