using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPlayersInput : IPredictedData
    {
        public DisposableList<PlayerID> addPlayers;
        public DisposableList<PlayerID> removePlayers;

        public void Dispose()
        {
            addPlayers.Dispose();
            removePlayers.Dispose();
        }

        public override string ToString()
        {
            return $"Add Players: {addPlayers}, Remove Players: {removePlayers}";
        }
    }
}
