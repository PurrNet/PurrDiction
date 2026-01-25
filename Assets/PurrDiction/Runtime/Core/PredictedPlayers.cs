using System;
using System.Collections.Generic;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public class PredictedPlayers : PredictedIdentity<PredictedPlayersInput, PredictedPlayersState>
    {
        public event Action<PlayerID> onPlayerAdded;

        public event Action<PlayerID> onPlayerRemoved;

        public IReadOnlyList<PlayerID> players => currentState.players;

        private void Awake()
        {
            extrapolateInput = false;
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
            var toAdd = DisposableList<PlayerID>.Create(16);
            var toRemove = DisposableList<PlayerID>.Create(16);

            var observers = predictionManager.observers;

            for (var i = 0; i < observers.Count; i++)
            {
                var player = observers[i];
                if (!currentState.players.Contains(player))
                    toAdd.Add(player);
            }

            var playersCount = currentState.players.Count;
            var currentPlayers = currentState.players;
            for (var i = 0; i < playersCount; i++)
            {
                var current = currentPlayers[i];
                if (!predictionManager.IsObserver(current))
                    toRemove.Add(current);
            }

            input.addPlayers = toAdd;
            input.removePlayers = toRemove;
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
