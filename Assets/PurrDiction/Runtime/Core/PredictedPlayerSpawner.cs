using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PlaySpawnerState : IPredictedData<PlaySpawnerState>
    {
        public int spawnPointIndex;
        public DisposableDictionary<PlayerID, PredictedObjectID> players;

        public void Dispose()
        {
            // TODO release managed resources here
        }
    }

    public class PredictedPlayerSpawner : PredictedIdentity<PlaySpawnerState>
    {
        [SerializeField] private GameObject _playerPrefab;
        [SerializeField, PurrLock] private bool _destroyOnDisconnect;
        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

        private void Awake() => CleanupSpawnPoints();

        protected override void LateAwake()
        {
            if (predictionManager.players)
            {
                var players = predictionManager.players.players;
                for (var i = 0; i < players.Count; i++)
                    OnPlayerLoadedScene(players[i]);

                predictionManager.players.onPlayerAdded += OnPlayerLoadedScene;
                predictionManager.players.onPlayerRemoved += OnPlayerUnloadedScene;
            }
        }

        protected override PlaySpawnerState GetInitialState()
        {
            return new PlaySpawnerState
            {
                spawnPointIndex = 0,
                players = DisposableDictionary<PlayerID, PredictedObjectID>.Create()
            };
        }

        protected override void Destroyed()
        {
            if (predictionManager && predictionManager.players)
            {
                predictionManager.players.onPlayerAdded -= OnPlayerLoadedScene;
                predictionManager.players.onPlayerRemoved -= OnPlayerUnloadedScene;
            }
        }

        private void CleanupSpawnPoints()
        {
            bool hadNullEntry = false;
            for (int i = 0; i < spawnPoints.Count; i++)
            {
                if (!spawnPoints[i])
                {
                    hadNullEntry = true;
                    spawnPoints.RemoveAt(i);
                    i--;
                }
            }

            if (hadNullEntry)
                PurrLogger.LogWarning($"Some spawn points were invalid and have been cleaned up.", this);
        }

        private void OnPlayerUnloadedScene(PlayerID player)
        {
            if (!_destroyOnDisconnect)
                return;

            if (currentState.players.TryGetValue(player, out var playerID))
            {
                predictionManager.hierarchy.Delete(playerID);
                currentState.players.Remove(player);
            }
        }

        private void OnPlayerLoadedScene(PlayerID player)
        {
            if (!enabled)
                return;

            if (currentState.players.ContainsKey(player))
                return;

            PredictedObjectID? newPlayer;

            CleanupSpawnPoints();

            if (spawnPoints.Count > 0)
            {
                var spawnPoint = spawnPoints[currentState.spawnPointIndex];
                currentState.spawnPointIndex = (currentState.spawnPointIndex + 1) % spawnPoints.Count;
                newPlayer = predictionManager.hierarchy.Create(_playerPrefab, spawnPoint.position, spawnPoint.rotation, player);
            }
            else
            {
                newPlayer = predictionManager.hierarchy.Create(_playerPrefab, owner: player);
            }

            if (!newPlayer.HasValue)
                return;

            currentState.players[player] = newPlayer.Value;
            predictionManager.SetOwnership(newPlayer, player);
        }
    }
}
