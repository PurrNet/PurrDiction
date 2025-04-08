
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPlayersState : IPredictedData<PredictedPlayersState>
    {
        public DisposableList<PlayerID> players;

        public void Dispose()
        {
            players.Dispose();
        }
    }

    public class PredictedPlayers : PredictedIdentity<PredictedPlayersState>
    {
        protected override PredictedPlayersState GetInitialState()
        {
            return new PredictedPlayersState
            {
                players = new DisposableList<PlayerID>(16)
            };
        }

        protected override void GetUnityState(ref PredictedPlayersState state)
        {
            var actual = predictionManager.networkManager.players;
            state.players.Clear();
            state.players.AddRange(actual);
        }
    }
}
