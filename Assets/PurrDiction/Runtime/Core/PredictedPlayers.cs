using System;
using System.Collections.Generic;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPlayersState : IPredictedData<PredictedPlayersState>
    {
        public DisposableList<PlayerID> players;

        [Obsolete("Use `players` instead")] public DisposableList<PlayerID> handledPlayers => players;
        [Obsolete("Use `players` instead")] public DisposableList<PlayerID> purrNetPlayers => players;

        public void Dispose()
        {
            players.Dispose();
        }

        public override string ToString()
        {
            string result = string.Empty;

            if (!players.isDisposed)
            {
                result += $"players: {players.Count}\n";
                for (var i = 0; i < players.Count; i++)
                {
                    var playerId = players[i];
                    result += $"(playerId: {playerId})";
                    if (i < players.Count - 1)
                        result += "\n";
                }

                result += "\n";
            }

            return result;
        }
    }

    public struct PredictedPlayersInput : IPredictedData
    {
        public DisposableList<PlayerID> addPlayers;
        public DisposableList<PlayerID> removePlayers;

        public void Dispose()
        {
            addPlayers.Dispose();
            removePlayers.Dispose();
        }
    }

    public class PredictedPlayers : PredictedIdentity<PredictedPlayersInput, PredictedPlayersState>
    {
        public event Action<PlayerID> onPlayerAdded;

        public event Action<PlayerID> onPlayerRemoved;

        public IReadOnlyList<PlayerID> players => currentState.players;

        protected override PredictedPlayersState GetInitialState()
        {
            return new PredictedPlayersState
            {
                players = DisposableList<PlayerID>.Create(16)
            };
        }

        protected override void ModifyExtrapolatedInput(ref PredictedPlayersInput input)
        {
            if (!input.addPlayers.isDisposed)
                input.addPlayers.Clear();

            if (!input.removePlayers.isDisposed)
                input.removePlayers.Clear();
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

            foreach (var current in currentState.players)
            {
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
