using PurrNet.Prediction;
using UnityEngine;

/// <summary>Tick-driven gameplay on the production predicted rigidbody/transform pair.</summary>
public sealed class FullPredictionBenchmarkBody : PredictedIdentity<FullPredictionBenchmarkBody.DriveInput, FullPredictionBenchmarkBody.DriveState>
{
    public const float ArenaX = 1000f;
    public static bool sampling;
    public static long contactCallbacks;
    public static long contactPoints;
    public static long bodyContactCallbacks;
    public static long groundingQueries;

    private Rigidbody _body;

    public struct DriveInput : IPredictedData
    {
        public int x;
        public int z;
        public void Dispose() { }
    }

    public struct DriveState : IPredictedData<DriveState>
    {
        public uint simulatedTicks;
        public bool grounded;
        public void Dispose() { }
    }

    protected override void LateAwake()
    {
        _body = GetComponent<Rigidbody>();
    }

    protected override void GetFinalInput(ref DriveInput input)
    {
        if (!owner.HasValue)
        {
            input = default;
            return;
        }

        // Inputs are generated once on the controller and travel through normal input history.
        ulong phase = predictionManager.localTick / 12 + owner.Value.id.value * 3 + id.objectId.instanceId.value;
        input.x = (phase & 2) == 0 ? 1 : -1;
        input.z = (phase & 4) == 0 ? 1 : -1;
    }

    protected override void Simulate(DriveInput input, ref DriveState state, float delta)
    {
        state.simulatedTicks++;
        if (!_body || !owner.HasValue)
            return;

        // One ordinary gameplay grounding query per controlled body per simulated tick.
        state.grounded = Physics.Raycast(_body.position, Vector3.down, 0.65f, ~0, QueryTriggerInteraction.Ignore);
        if (sampling)
            groundingQueries++;
        var p = _body.position;
        var force = new Vector3(input.x * 9f - (p.x - ArenaX) * 2f, 0f, input.z * 9f - p.z * 2f);
        _body.AddForce(force * (state.grounded ? 1f : 0.5f), ForceMode.Acceleration);
    }

    private void OnCollisionEnter(Collision collision) => RecordContact(collision);
    private void OnCollisionStay(Collision collision) => RecordContact(collision);

    private static void RecordContact(Collision collision)
    {
        if (!sampling)
            return;
        contactCallbacks++;
        contactPoints += collision.contactCount;
        if (collision.rigidbody)
            bodyContactCallbacks++;
    }

    public static void BeginContacts()
    {
        contactCallbacks = 0;
        contactPoints = 0;
        bodyContactCallbacks = 0;
        groundingQueries = 0;
        sampling = true;
    }
}
