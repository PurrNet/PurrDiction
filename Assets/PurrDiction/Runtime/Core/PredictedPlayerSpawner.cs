using System.Collections.Generic;
using PurrNet.Logging;
using PurrNet.Modules;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedPlayerSpawner : PurrMonoBehaviour
    {
        [SerializeField] private GameObject _playerPrefab;
        [SerializeField] private List<Transform> spawnPoints = new List<Transform>();
        private int _currentSpawnPoint;

        private void Awake() => CleanupSpawnPoints();

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

        public override void Subscribe(NetworkManager manager, bool asServer)
        {
            if (asServer && manager.TryGetModule(out ScenePlayersModule scenePlayersModule, true))
            {
                scenePlayersModule.onPlayerLoadedScene += OnPlayerLoadedScene;

                if (!manager.TryGetModule(out ScenesModule scenes, true))
                    return;
            
                if (!scenes.TryGetSceneID(gameObject.scene, out var sceneID))
                    return;
                
                if (scenePlayersModule.TryGetPlayersInScene(sceneID, out var players))
                {
                    foreach (var player in players)
                        OnPlayerLoadedScene(player, sceneID, true);
                }
            }
        }

        public override void Unsubscribe(NetworkManager manager, bool asServer)
        {
            if (asServer && manager.TryGetModule(out ScenePlayersModule scenePlayersModule, true))
                scenePlayersModule.onPlayerLoadedScene -= OnPlayerLoadedScene;
        }

        private void OnDestroy()
        {
            if(NetworkManager.main && NetworkManager.main.TryGetModule(out ScenePlayersModule scenePlayersModule, true))
                scenePlayersModule.onPlayerLoadedScene -= OnPlayerLoadedScene;
        }

        private void OnPlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            var main = NetworkManager.main;
            
            if (!main || !main.TryGetModule(out ScenesModule scenes, true))
                return;

            var unityScene = gameObject.scene;
            
            if (!scenes.TryGetSceneID(unityScene, out var sceneID))
                return;
            
            if (sceneID != scene)
                return;

            if (!asServer)
                return;
            
            if (!PredictionManager.TryGetInstance(gameObject.scene.handle, out var predictionManager))
                return;

            bool isDestroyOnDisconnectEnabled = main.networkRules.ShouldDespawnOnOwnerDisconnect();
            if (!isDestroyOnDisconnectEnabled && main.TryGetModule(out GlobalOwnershipModule ownership, true) && 
                ownership.PlayerOwnsSomething(player))
                return;
            
            PredictedObjectID? newPlayer;
            
            CleanupSpawnPoints();
            
            if (spawnPoints.Count > 0)
            {
                var spawnPoint = spawnPoints[_currentSpawnPoint];
                _currentSpawnPoint = (_currentSpawnPoint + 1) % spawnPoints.Count;
                newPlayer = predictionManager.hierarchy.Create(_playerPrefab, spawnPoint.position, spawnPoint.rotation);
            }
            else
            {
                newPlayer = predictionManager.hierarchy.Create(_playerPrefab);
            }

            predictionManager.SetOwnership(newPlayer, player);
        }
    }
}
