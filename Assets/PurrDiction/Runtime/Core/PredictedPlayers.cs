
using System;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPlayersState : IPredictedData<PredictedPlayersState>
    {
        public DisposableList<PlayerID> handledPlayers;
        public DisposableList<PlayerID> purrNetPlayers;

        public void Dispose()
        {
            handledPlayers.Dispose();
            purrNetPlayers.Dispose();
        }
    }

    public class PredictedPlayers : PredictedIdentity<PredictedPlayersState>
    {
        public event Action<PlayerID> onPlayerAdded;

        public event Action<PlayerID> onPlayerRemoved;

        protected override PredictedPlayersState GetInitialState()
        {
            return new PredictedPlayersState
            {
                handledPlayers = new DisposableList<PlayerID>(16),
                purrNetPlayers = new DisposableList<PlayerID>(16)
            };
        }

        protected override void GetUnityState(ref PredictedPlayersState state)
        {
            var actual = predictionManager.observers;
            state.purrNetPlayers.Clear();
            state.purrNetPlayers.AddRange(actual);
        }

        protected override void Simulate(ref PredictedPlayersState state, float delta)
        {
            for (var i = 0; i < state.purrNetPlayers.Count; i++)
            {
                var playerId = state.purrNetPlayers[i];
                if (state.handledPlayers.Contains(playerId))
                    continue;

                state.handledPlayers.Add(playerId);
                onPlayerAdded?.Invoke(playerId);
            }

            for (var i = 0; i < state.handledPlayers.Count; i++)
            {
                var playerId = state.handledPlayers[i];
                if (state.purrNetPlayers.Contains(playerId))
                    continue;

                state.handledPlayers.RemoveAt(i);
                onPlayerRemoved?.Invoke(playerId);
            }
        }
    }
}
