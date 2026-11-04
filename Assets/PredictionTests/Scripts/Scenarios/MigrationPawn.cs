using PurrNet.Prediction;

/// <summary>
/// Player pawn for the host migration scenario: the owner adds one per tick, so the sum only keeps growing on
/// the server while the owner's input reaches it.
/// </summary>
public class MigrationPawn : PredictedIdentity<MigrationPawn.PawnInput, MigrationPawn.PawnState>
{
    public struct PawnInput : IPredictedData
    {
        public int step;

        public void Dispose() { }
    }

    public struct PawnState : IPredictedData<PawnState>
    {
        public long sum;

        public void Dispose() { }
    }

    private void Awake()
    {
        extrapolateInput = false;
    }

    protected override void GetFinalInput(ref PawnInput input)
    {
        input.step = 1;
    }

    protected override void Simulate(PawnInput input, ref PawnState state, float delta)
    {
        state.sum += input.step;
    }
}
