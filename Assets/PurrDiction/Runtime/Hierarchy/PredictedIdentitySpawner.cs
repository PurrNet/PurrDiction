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

    public class PredictedIdentitySpawner : PredictedIdentity<PredictedIdentitySpawnerState>
    {
        [SerializeField] private NetworkIdentity[] _identitiesToSpawn;

        private bool _areSpawned;

        private HierarchyV2 _serverHierarchy;
        private HierarchyV2 _clientHierarchy;

        private NetworkID? _networkId;

        protected override void LateAwake()
        {
            TryToPopulateHierarchy(true, out _serverHierarchy);
            TryToPopulateHierarchy(false, out _clientHierarchy);

            if (_serverHierarchy != null)
            {
                _networkId = SpawnAllIdentitiesOnServer();
                predictionManager.onObserverAdded += OnObserverAdded;
            }
        }

        protected override void Destroyed()
        {
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

        private void OnObserverAdded(PlayerID player)
        {
            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];
                if (identity == null)
                    continue;

                _serverHierarchy.ManualAddObserver(identity, player);
            }
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
            NetworkID? firstId = null;
            var networkManager = predictionManager.networkManager;

            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];
                if (!identity)
                    continue;

                var reservedId = _serverHierarchy.ReserveNetworkID();
                firstId ??= reservedId;
                _serverHierarchy.ManualEarlySpawn(identity, reservedId);
                _serverHierarchy.ManualFinalizeSpawn(identity);

                foreach (var observer in predictionManager.observers)
                    _serverHierarchy.ManualAddObserver(identity, observer);

                /*if (owner.HasValue && networkManager.TryGetModule(true, out GlobalOwnershipModule module))
                {
                    module.GiveOwnership(identity, owner.Value, false, silent: true, isSpawner: true);
                }*/
            }

            _areSpawned = true;
            return firstId;
        }

        protected override void Simulate(ref PredictedIdentitySpawnerState state, float delta)
        {
            if (_areSpawned || !predictionManager.isVerifiedAndReplaying)
                return;

            if (!state.networkId.HasValue)
                return;

            var firstId = state.networkId.Value;
            for (int i = 0; i < _identitiesToSpawn.Length; i++)
            {
                var identity = _identitiesToSpawn[i];
                if (identity == null)
                    continue;

                _clientHierarchy.ManualEarlySpawn(identity, new NetworkID(firstId, (ulong)i));
                _clientHierarchy.ManualFinalizeSpawn(identity);
            }

            _areSpawned = true;
        }
    }
}
