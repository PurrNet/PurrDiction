using System.Collections.Generic;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedPlayerSpawner : StatelessPredictedIdentity
    {
        [SerializeField] private GameObject _playerPrefab;
        [SerializeField] private bool _destroyOnDisconnect;
        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

        private int _currentSpawnPoint;
        readonly Dictionary<PlayerID, PredictedObjectID> _players = new ();

        private void Awake() => CleanupSpawnPoints();

        protected override void LateAwake()
        {
            _currentSpawnPoint = 0;
            _players.Clear();

            if (predictionManager.players != null)
            {
                predictionManager.players.onPlayerAdded += OnPlayerLoadedScene;
                predictionManager.players.onPlayerRemoved += OnPlayerUnloadedScene;
            }
        }

        protected override void Destroyed()
        {
            if (predictionManager && predictionManager.players != null)
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

            if (_players.TryGetValue(player, out var playerID))
            {
                predictionManager.hierarchy.Delete(playerID);
                _players.Remove(player);
            }
        }

        private void OnPlayerLoadedScene(PlayerID player)
        {
            if (!enabled)
                return;

            if (_players.ContainsKey(player))
                return;

            PredictedObjectID? newPlayer;

            CleanupSpawnPoints();

            if (spawnPoints.Count > 0)
            {
                var spawnPoint = spawnPoints[_currentSpawnPoint];
                _currentSpawnPoint = (_currentSpawnPoint + 1) % spawnPoints.Count;
                newPlayer = predictionManager.hierarchy.Create(_playerPrefab, spawnPoint.position, spawnPoint.rotation, player);
            }
            else
            {
                newPlayer = predictionManager.hierarchy.Create(_playerPrefab, owner: player);
            }

            if (!newPlayer.HasValue)
                return;

            _players[player] = newPlayer.Value;
            predictionManager.SetOwnership(newPlayer, player);
        }
    }
}
