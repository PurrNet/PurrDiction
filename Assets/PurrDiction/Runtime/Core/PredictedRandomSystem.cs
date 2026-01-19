namespace PurrNet.Prediction
{
    public struct PredictedRandomState : IPredictedData<PredictedRandomState>
    {
        public PredictedRandom random;

        public void Dispose() { }
    }
    public class PredictedRandomSystem : PredictedIdentity<PredictedRandomState>
    {
        public uint seed
        {
            get => currentState.random.seed;
            set => currentState.random.seed = value;
        }

        protected override PredictedRandomState GetInitialState()
        {
            return new PredictedRandomState
            {
                random = PredictedRandom.Create(predictionManager.sessionSeed)
            };
        }

        // Generates a random uint in the range [0, uint.MaxValue)
        public uint Next() => currentState.random.Next();

        // Generates a random integer in the range [min, max)
        public int Next(int min, int max) => currentState.random.Next(min, max);

        // Generates a random integer in the range [0, max)
        public int Next(int max) => currentState.random.Next(max);

        // Generates a random float in the range [0, 1)
        public float NextFloat() => currentState.random.NextFloat();

        // Generates a random sfloat in the range [0, 1)
        public sfloat NextSFloat() => currentState.random.NextSFloat();

        // Generates a random sfloat in the range [0, 1)
        public FP NextFP() => currentState.random.NextFP();

        // Generates a random float in the range [min, max)
        public float NextFloat(float min, float max) => currentState.random.NextFloat(min, max);

        public sfloat NextSFloat(sfloat min, sfloat max) => currentState.random.NextSFloat(min, max);

        public FP NextFP(FP min, FP max) => currentState.random.NextFP(min, max);
    }
}
