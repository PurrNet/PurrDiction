using System;
using PurrNet.Packing;
using PurrNet.Pooling;

namespace PurrNet.Prediction
{
    public struct PredictedPlayersState : IPredictedData<PredictedPlayersState>, IDuplicate<PredictedPlayersState>
    {
        public DisposableList<PlayerID> players;

        [Obsolete("Use `players` instead")] public DisposableList<PlayerID> handledPlayers => players;
        [Obsolete("Use `players` instead")] public DisposableList<PlayerID> purrNetPlayers => players;

        public void Dispose()
        {
            players.Dispose();
        }

        public PredictedPlayersState Duplicate()
        {
            return new PredictedPlayersState
            {
                players = players.Duplicate()
            };
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
}
