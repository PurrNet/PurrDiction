using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public struct PredictedRandom : IPackedAuto
    {
        public uint seed;

        public static PredictedRandom Create(uint seed)
        {
            return new PredictedRandom { seed = seed };
        }

        // Generates a random uint in the range [0, uint.MaxValue)
        public uint Next()
        {
            seed = seed * 1664525u + 1013904223u; // LCG constants
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
            return Next() / (float)uint.MaxValue;
        }

        // Generates a random sfloat in the range [0, 1)
        public sfloat NextSFloat()
        {
            return sfloat.Abs((int)Next() / (sfloat)int.MaxValue);
        }

        // Generates a random sfloat in the range [0, 1)
        public FP NextFP()
        {
            return (FP)Next() / 4294967296.0;
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
