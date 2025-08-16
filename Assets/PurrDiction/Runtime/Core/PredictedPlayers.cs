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

        public override string ToString()
        {
            string result = string.Empty;

            if (!handledPlayers.isDisposed)
            {
                result += $"handledPlayers: {handledPlayers.Count}\n";
                for (var i = 0; i < handledPlayers.Count; i++)
                {
                    var playerId = handledPlayers[i];
                    result += $"(playerId: {playerId})";
                    if (i < handledPlayers.Count - 1)
                        result += "\n";
                }

                result += "\n";
            }

            if (!purrNetPlayers.isDisposed)
            {
                result += $"purrNetPlayers: {purrNetPlayers.Count}\n";
                for (var i = 0; i < purrNetPlayers.Count; i++)
                {
                    var playerId = purrNetPlayers[i];
                    result += $"(playerId: {playerId})";
                    if (i < purrNetPlayers.Count - 1)
                        result += "\n";
                }
                result += "\n";
            }

            return result;
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
                handledPlayers = DisposableList<PlayerID>.Create(16),
                purrNetPlayers = DisposableList<PlayerID>.Create(16)
            };
        }

        protected override void GetUnityState(ref PredictedPlayersState state)
        {
            if (!isServer)
                return;

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

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }
    }
}
