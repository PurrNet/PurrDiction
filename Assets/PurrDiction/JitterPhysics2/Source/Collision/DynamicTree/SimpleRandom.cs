using Real = PurrNet.Prediction.FP64;
using MathR = PurrNet.Prediction.FP64Math;

namespace Jitter2.Collision
{
    public struct SimpleRandom
    {
        public ulong seed;

        public ulong Next()
        {
            seed = seed * 1664525u + 1013904223u; // LCG constants
            return seed;
        }

        // [0, 1[
        public Real NextReal()
        {
            var next = Real.FromRaw(Next());
            var range = next / Real.MaxValue;
            var res = MathR.Abs(range);
            if (res == 1.0)
                res.rawValue -= 1;
            return res;
        }
    }
}
