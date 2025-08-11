using PurrNet.Modules;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PredictedIdentitySpawnerState : IPredictedData<PredictedIdentitySpawnerState>
    {
        public NetworkID? networkId;

        public void Dispose() { }

        public override string ToString()
        {
            return $"NetworkID = {networkId?.ToString() ?? "NULL"}";
        }
    }

    [PredictionUnsafe]
    public class PredictedIdentitySpawner : PredictedIdentity<PredictedIdentitySpawnerState>
    {
        [SerializeField] private NetworkIdentity[] _identitiesToSpawn;

        private bool _areSpawned;

        private HierarchyV2 _serverHierarchy;
        private HierarchyV2 _clientHierarchy;

        private NetworkID? _networkId;

        protected override void LateAwake()
        {
            if (!enabled)
                return;

            TryToPopulateHierarchy(true, out _serverHierarchy);
            TryToPopulateHierarchy(false, out _clientHierarchy);

            if (_serverHierarchy != null)
                _networkId = SpawnAllIdentitiesOnServer();
        }

        public void ClientRequestedToBeObserver(PlayerID player)
        {
            if (!predictionManager.IsObserver(player)) return;

            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];
                if (!identity)
                    continue;

                _serverHierarchy.ManualAddObserver(identity, player);
            }
        }

        protected override void Destroyed()
        {
            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];
                if (identity && identity.isSpawned)
                {
                    _serverHierarchy?.ManualDespawn(identity);

                    if (_clientHierarchy != null && predictionManager.isVerified)
                        _clientHierarchy.ManualDespawn(identity);
                }
            }

            if (_serverHierarchy != null)
                predictionManager.onObserverRemoved -= OnObserverRemoved;
        }

        protected override PredictedIdentitySpawnerState GetInitialState()
        {
            return new PredictedIdentitySpawnerState
            {
                networkId = _networkId
            };
        }

        private void OnObserverRemoved(PlayerID player)
        {
            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];
                if (identity == null)
                    continue;

                _serverHierarchy.ManualRemoveObserver(identity, player);
            }
        }

        private void TryToPopulateHierarchy(bool asServer, out HierarchyV2 hierarchy)
        {
            var manager = predictionManager.networkManager;

            if (manager.TryGetModule(asServer, out HierarchyFactory factory) &&
                factory.TryGetHierarchy(predictionManager.sceneId, out var hierarchyResult))
            {
                hierarchy = hierarchyResult;
            }
            else
            {
                hierarchy = null;
            }
        }

        private NetworkID? SpawnAllIdentitiesOnServer()
        {
            var networkManager = predictionManager.networkManager;
            NetworkID? firstId = null;

            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];

                if (!identity)
                    continue;

                var reservedId = _serverHierarchy.ReserveNetworkID();
                firstId ??= reservedId;
                _serverHierarchy.ManualEarlySpawn(identity, reservedId);

                if (predictionManager.localPlayer.HasValue)
                    _serverHierarchy.ManualAddObserver(identity, predictionManager.localPlayer.Value);

                if (owner.HasValue && networkManager.TryGetModule(true, out GlobalOwnershipModule module))
                    module.GiveOwnership(identity, owner.Value, false, silent: true, isSpawner: true);

                _serverHierarchy.ManualFinalizeSpawn(identity);
            }

            _areSpawned = true;
            return firstId;
        }

        private int? _finalizeNextFrame;

        protected override void Simulate(ref PredictedIdentitySpawnerState state, float delta)
        {
            if (_clientHierarchy == null)
                return;

            if (_finalizeNextFrame.HasValue && Time.frameCount > _finalizeNextFrame.Value)
            {
                _finalizeNextFrame = null;

                for (int i = 0; i < _identitiesToSpawn.Length; i++)
                {
                    var identity = _identitiesToSpawn[i];
                    if (identity == null)
                        continue;
                    _clientHierarchy.ManualFinalizeSpawn(identity);
                }
            }

            if (_areSpawned || !predictionManager.isVerifiedAndReplaying)
                return;

            if (!state.networkId.HasValue)
                return;

            var firstId = state.networkId.Value;
            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];
                if (!identity)
                    continue;

                _clientHierarchy.ManualEarlySpawn(identity, new NetworkID(firstId, (ulong)i));
                _finalizeNextFrame = Time.frameCount;
            }

            predictionManager.ClientRequestedToBeObserver(id);

            _areSpawned = true;
        }
    }
}
