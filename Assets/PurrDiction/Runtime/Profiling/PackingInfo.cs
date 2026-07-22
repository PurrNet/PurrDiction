using System;

namespace PurrNet.Prediction.Profiler
{
    public struct PackingInfo
    {
        public Type parent;
        public int bitCount;
        public UnityEngine.Object reference;
    }

    public readonly struct FramePackingInfo
    {
        public readonly PlayerID player;
        public readonly int bitCount;

        public FramePackingInfo(PlayerID player, int bitCount)
        {
            this.player = player;
            this.bitCount = bitCount;
        }
    }
}
