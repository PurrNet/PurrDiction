using System;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        /// <summary>
        /// Target rate for ordinary server frames. Zero follows the simulation rate.
        /// </summary>
        public int serverUpdateRate
        {
            get => _serverUpdateRate;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Server update rate cannot be negative.");
                _serverUpdateRate = value;
            }
        }

        private void DispatchPreparedServerFrames()
        {
            for (int i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                if (frame.preparedFrameTick != localTick)
                    continue;

                if (frame.fullFrame || (!isActiveAndEnabled &&
                    frame.frameSendSchedule.ShouldSend(frame.preparedFrameTick, tickRate, serverUpdateRate)))
                {
                    SendPreparedServerFrame(i);
                }
            }
        }

        private void FlushPendingServerFrames()
        {
            if (isSimulating || isReplaying)
                return;
            bool sent = false;
            for (int i = 0; i < _clientFrames.Count; i++)
            {
                var frame = _clientFrames[i];
                if (frame.preparedFrameTick != 0 &&
                    frame.frameSendSchedule.ShouldSend(frame.preparedFrameTick, tickRate, serverUpdateRate))
                {
                    SendPreparedServerFrame(i);
                    sent = true;
                }
            }

            // Frames ride the batched lane, which PurrNet otherwise flushes on its next tick.
            if (sent && networkManager)
            {
                networkManager.FlushBatchedRPCs();
                networkManager.RequestSendFlushThisFrame();
            }
        }
    }
}
