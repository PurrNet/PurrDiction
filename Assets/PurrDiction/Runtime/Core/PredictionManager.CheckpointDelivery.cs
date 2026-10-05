using UnityEngine;

namespace PurrNet.Prediction
{
    public partial class PredictionManager
    {
        private bool _awaitingFullFrame = true;

        internal static int? frameCountOverrideForTests;

        private static int currentFrameCount => frameCountOverrideForTests ?? Time.frameCount;

        private void EnqueueDelta(FrameDelta frame)
        {
            frame.enqueuedFrame = currentFrameCount;
            frame.trackAge = localTick > 1;
            _deltas.Enqueue(frame);
        }

        private void ClearCheckpointDelivery()
        {
            _awaitingFullFrame = true;
            _consecutiveRejectedCheckpoints = 0;
            _tolerateReplayHookFailures = false;
            _toleratedReplayHookFailures = 0;
        }

        // Takes ownership of the decoded payload. Frames arrive in send order, so a full frame
        // supersedes everything still queued before it.
        private void ReceiveFrame(FrameDelta frame)
        {
            if (frame.serverTick <= _verifiedServerTick)
            {
                frame.Dispose();
                return;
            }

            if (frame.fullFrame)
            {
                while (_deltas.Count > 0)
                    _deltas.Dequeue().Dispose();
            }

            EnqueueDelta(frame);
        }
    }
}
