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

        /// <summary>Returns the next unsigned random value.</summary>
        public uint Next()
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed *= LCG_MULTIPLIER_CONSTANT;
            seed++;
            return seed;
        }

        /// <summary>Returns a random integer in [min, max).</summary>
        public int Next(int min, int max)
        {
            return (int)(Next() % (uint)(max - min)) + min;
        }

        /// <summary>Returns a random integer in [0, max).</summary>
        public int Next(int max)
        {
            return (int)(Next() % (uint)max);
        }

        /// <summary>Returns a random float in [0, 1).</summary>
        public float NextFloat()
        {
            return BitConverter.Int32BitsToSingle((int)((Next() >> 9) | FLOAT_EXPONENT_MASK)) - 1.0f;
        }

        /// <summary>Returns a random sfloat in [0, 1).</summary>
        public sfloat NextSFloat()
        {
            return sfloat.FromRaw((Next() >> 9) | FLOAT_EXPONENT_MASK) - sfloat.one;
        }

        /// <summary>Returns a random fixed-point value in [0, 1).</summary>
        public FP NextFP()
        {
            return FP.FromRaw(Next());
        }

        /// <summary>Returns a random float in [min, max).</summary>
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
