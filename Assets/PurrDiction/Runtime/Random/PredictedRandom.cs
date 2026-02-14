using PurrNet.Packing;
using System;

namespace PurrNet.Prediction
{
    public struct PredictedRandom : IPackedAuto
    {
        public uint seed;
        const uint LCG_MULTIPLIER_CONSTANT = 0x915f77f5;
        const uint FLOAT_EXPONENT_MASK = 0x3F800000;
        public override string ToString()
        {
            return $"PredictedRandom(seed: {seed})";
        }

        public static PredictedRandom Create(uint seed)
        {
            return new PredictedRandom { seed = seed };
        }

        // Generates a random uint in the range [0, uint.MaxValue)
        public uint Next()
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed *= LCG_MULTIPLIER_CONSTANT;
            seed++;
            return seed;
        }

        // Generates a random integer in the range [min, max)
        public int Next(int min, int max)
        {
            return (int)(Next() % (uint)(max - min)) + min;
        }

        // Generates a random integer in the range [0, max)
        public int Next(int max)
        {
            return (int)(Next() % (uint)max);
        }

        // Generates a random float in the range [0, 1)
        public float NextFloat()
        {
            return BitConverter.Int32BitsToSingle((int)((Next() >> 9) | FLOAT_EXPONENT_MASK)) - 1.0f;
        }

        // Generates a random sfloat in the range [0, 1)
        public sfloat NextSFloat()
        {
            return sfloat.FromRaw((Next() >> 9) | FLOAT_EXPONENT_MASK) - sfloat.one;
        }

        // Generates a random sfloat in the range [0, 1)
        public FP NextFP()
        {
            return FP.FromRaw(Next());
        }

        // Generates a random float in the range [min, max)
        public float NextFloat(float min, float max)
        {
            return min + (max - min) * NextFloat();
        }

        public sfloat NextSFloat(sfloat min, sfloat max)
        {
            return min + (max - min) * NextSFloat();
        }

        public FP NextFP(FP min, FP max)
        {
            return min + (max - min) * NextFP();
        }
    }
}
