using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PlayerWithObject : IDuplicate<PlayerWithObject>
    {
        public PredictedObjectID objectID;
        public PlayerID playerID;

        public PlayerWithObject Duplicate()
        {
            return new PlayerWithObject
            {
                objectID = objectID,
                playerID = playerID
            };
        }
    }

    public struct PlayerSpawnerState : IPredictedData<PlayerSpawnerState>, IDuplicate<PlayerSpawnerState>
    {
        public int spawnPointIndex;
        public DisposableList<PlayerWithObject> values;

        public PredictedObjectID this[PlayerID player]
        {
            set
            {
                for (var i = 0; i < values.Count; i++)
                {
                    var playerWithObject = values[i];
                    if (playerWithObject.playerID == player)
                    {
                        playerWithObject.objectID = value;
                        values[i] = playerWithObject;
                        return;
                    }
                }

                values.Add(new PlayerWithObject { objectID = value, playerID = player });
            }
        }

        public void Dispose()
        {
            values.Dispose();
        }

        public PlayerSpawnerState Duplicate()
        {
            return new PlayerSpawnerState
            {
                values = DisposableList<PlayerWithObject>.Create(values),
                spawnPointIndex = spawnPointIndex
            };
        }

        public bool TryGetValue(PlayerID player, out PredictedObjectID o)
        {
            for (var i = 0; i < values.Count; i++)
            {
                var playerWithObject = values[i];
                if (playerWithObject.playerID == player)
                {
                    o = playerWithObject.objectID;
                    return true;
                }
            }

            o = default;
            return false;
        }

        public bool ContainsKey(PlayerID player)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i].playerID == player)
                    return true;
            }

            return false;
        }

        public void Remove(PlayerID player)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i].playerID == player)
                {
                    values.RemoveAt(i);
                    break;
                }
            }
        }
    }

    public class PredictedPlayerSpawner : DeterministicIdentity<PlayerSpawnerState>
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

        protected override PlayerSpawnerState GetInitialState()
        {
            return new PlayerSpawnerState
            {
                spawnPointIndex = 0,
                values = DisposableList<PlayerWithObject>.Create()
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

        protected override PlayerSpawnerState Interpolate(PlayerSpawnerState from, PlayerSpawnerState to, float t)
            => to;

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

            if (currentState.TryGetValue(player, out var playerID))
            {
                hierarchy.Delete(playerID);
                currentState.Remove(player);
            }
        }

        private void OnPlayerLoadedScene(PlayerID player)
        {
            if (!enabled)
                return;

            if (currentState.ContainsKey(player))
                return;

            PredictedObjectID? newPlayer;

            CleanupSpawnPoints();

            if (spawnPoints.Count > 0)
            {
                var spawnPoint = spawnPoints[currentState.spawnPointIndex];
                currentState.spawnPointIndex = (currentState.spawnPointIndex + 1) % spawnPoints.Count;
                newPlayer = hierarchy.Create(_playerPrefab, spawnPoint.position, spawnPoint.rotation, player);
            }
            else
            {
                newPlayer = hierarchy.Create(_playerPrefab, owner: player);
            }

            if (!newPlayer.HasValue)
                return;

            currentState[player] = newPlayer.Value;
            predictionManager.SetOwnership(newPlayer, player);
        }
    }
}
