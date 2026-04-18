using System;
using System.Collections.Generic;
using PurrNet.Modules;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public class PredictedPlayers : PredictedIdentity<PredictedPlayersInput, PredictedPlayersState>
    {
        public event Action<PlayerID> onPlayerAdded;

        public event Action<PlayerID> onPlayerRemoved;

        public IReadOnlyList<PlayerID> players => currentState.players;

        private ScenePlayersModule _scenePlayersModule;

        private void Awake()
        {
            extrapolateInput = false;
        }

        protected override void LateAwake()
        {
            _scenePlayersModule = predictionManager.networkManager.scenePlayersModule;
        }

        protected override PredictedPlayersState GetInitialState()
        {
            return new PredictedPlayersState
            {
                players = DisposableList<PlayerID>.Create(16)
            };
        }

        protected override void GetFinalInput(ref PredictedPlayersInput input)
        {
            var observers = predictionManager.observers;
            var currentPlayers = currentState.players;
        
            bool needsAdd = false;
            for (var i = 0; i < observers.Count; i++)
            {
                if (!currentPlayers.Contains(observers[i])) { needsAdd = true; break; }
            }
        
            bool needsRemove = false;
            for (var i = 0; i < currentPlayers.Count; i++)
            {
                if (!predictionManager.IsObserver(currentPlayers[i])) { needsRemove = true; break; }
            }
        
            if (needsAdd)
            {
                var toAdd = DisposableList<PlayerID>.Create(16);
                for (var i = 0; i < observers.Count; i++)
                {
                    if (!currentPlayers.Contains(observers[i]))
                        toAdd.Add(observers[i]);
                }
                input.addPlayers = toAdd;
            }
        
            if (needsRemove)
            {
                var toRemove = DisposableList<PlayerID>.Create(16);
                for (var i = 0; i < currentPlayers.Count; i++)
                {
                    var current = currentPlayers[i];
                    if (!predictionManager.IsObserver(current))
                        toRemove.Add(current);
                }
                input.removePlayers = toRemove;
            }
        }

        protected override void Simulate(PredictedPlayersInput input, ref PredictedPlayersState state, float delta)
        {
            int added = input.addPlayers.isDisposed ? 0 : input.addPlayers.Count;
            for (var i = 0; i < added; i++)
            {
                var playerId = input.addPlayers[i];
                state.players.Add(playerId);
                onPlayerAdded?.Invoke(playerId);
            }

            int removed = input.removePlayers.isDisposed ? 0 : input.removePlayers.Count;
            for (var i = 0; i < removed; i++)
            {
                var playerId = input.removePlayers[i];
                if (state.players.Remove(playerId))
                    onPlayerRemoved?.Invoke(playerId);
            }
        }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
