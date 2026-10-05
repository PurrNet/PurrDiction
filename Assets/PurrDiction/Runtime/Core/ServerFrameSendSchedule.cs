using System;

namespace PurrNet.Prediction
{
    internal struct ServerFrameSendSchedule
    {
        private ulong _anchorTick;
        public ulong lastSentTick { get; private set; }

        public bool ShouldSend(ulong tick, int simulationRate, int requestedRate)
        {
            if (tick == 0 || tick <= lastSentTick)
                return false;
            if (lastSentTick == 0)
                return true;

            ulong hz = (ulong)Math.Max(1, simulationRate);
            ulong rate = requestedRate <= 0 ? hz : Math.Min(hz, (ulong)requestedRate);
            ulong now = tick - _anchorTick;
            ulong previous = lastSentTick - _anchorTick;
            return now / hz != previous / hz ||
                   now % hz * rate / hz != previous % hz * rate / hz;
        }

        public void MarkSent(ulong tick, bool fullFrame)
        {
            if (fullFrame || lastSentTick == 0)
                _anchorTick = tick;
            lastSentTick = tick;
        }
    }
}
