using System;

namespace FixMath.NET
{
	public class FPRandom
    {
        private readonly Random random;

        public FPRandom(int seed)
        {
            random = new Random(seed);
        }

        public FP Next()
        {
            var result = new FP
            {
                RawValue = (uint)random.Next(int.MinValue, int.MaxValue)
            };
            return result;
        }

        public FP NextInt(int maxValue)
        {
            return random.Next(maxValue);
        }
    }
}
