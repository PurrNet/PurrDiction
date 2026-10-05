using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Injects a known client-only pose displacement and velocity impulse. The pose displacement
/// makes the disturbance independent of how much a velocity kick integrates before correction.
/// Actual rigidbody poses are sampled after physics; replay simulation remains forbidden.
/// </summary>
public class SoftProbe : PredictedIdentity<SoftProbe.ProbeState>
{
    public override bool supportsSoftCorrection => true;

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public void Dispose() { }
    }

    public static readonly List<SoftProbe> instances = new();
    public static int replayViolations { get; private set; }
    public static bool impulseApplied { get; private set; }
    public static float initialDivergence { get; private set; }
    public static float injectedDisplacement { get; private set; }
    public static float injectedVelocityChange { get; private set; }
    public static float maxObservedDivergence { get; private set; }
    public static int postPhysicsSamples { get; private set; }
    public static ulong injectionTick { get; private set; }

    public static void ResetCounters()
    {
        instances.Clear();
        replayViolations = 0;
        impulseApplied = false;
        initialDivergence = 0f;
        injectedDisplacement = 0f;
        injectedVelocityChange = 0f;
        maxObservedDivergence = 0f;
        postPhysicsSamples = 0;
        injectionTick = 0;
    }

    [SerializeField] private int _impulseAfterTicks = 90;
    [SerializeField] private Vector3 _impulse = new(2f, 4f, 1f);
    [SerializeField] private Vector3 _positionOffset = new(0.75f, 0f, 0f);

    private PredictedTransform _predictedTransform;
    private PredictedRigidbody _predictedRigidbody;
    private int _liveTicks;

    public bool hasVerifiedPose => _predictedTransform.verifiedState.HasValue;
    public float expectedDisplacement => _positionOffset.magnitude;
    public float expectedVelocityChange => _impulse.magnitude;

    public float divergence
    {
        get
        {
            var verified = _predictedTransform.verifiedState;
            if (!verified.HasValue)
                return float.PositiveInfinity;
            return Vector3.Distance(_predictedRigidbody.position, verified.Value.unityPosition);
        }
    }

    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.SoftCorrection;

    private void Awake()
    {
        _predictedTransform = GetComponent<PredictedTransform>();
        _predictedRigidbody = GetComponent<PredictedRigidbody>();
    }

    protected override void LateAwake() => instances.Add(this);
    protected override void Destroyed() => instances.Remove(this);

    protected override void Simulate(ref ProbeState state, float delta)
    {
        if (predictionManager.cachedIsServer)
            return;

        if (predictionManager.isReplaying || predictionManager.isVerified)
        {
            if (_liveTicks > 0)
                replayViolations++;
            return;
        }

        if (impulseApplied || ++_liveTicks < _impulseAfterTicks || !hasVerifiedPose)
            return;

        var beforePosition = _predictedRigidbody.position;
        var beforeVelocity = _predictedRigidbody.linearVelocity;
        _predictedRigidbody.position = beforePosition + _positionOffset;
        // Keep both Unity poses aligned, as PredictedTransform.SetUnityState does.
        // The transform may also have received a correction earlier in this tick.
        transform.position = _predictedRigidbody.position;
        _predictedRigidbody.linearVelocity = beforeVelocity + _impulse;

        injectedDisplacement = Vector3.Distance(beforePosition, _predictedRigidbody.position);
        injectedVelocityChange = Vector3.Distance(beforeVelocity, _predictedRigidbody.linearVelocity);
        initialDivergence = divergence;
        injectionTick = predictionManager.localTickInContext;
        impulseApplied = true;
    }

    protected override void LateSimulate(ref ProbeState state, float delta)
    {
        if (predictionManager.cachedIsServer || predictionManager.isReplaying ||
            predictionManager.isVerified || !impulseApplied || !hasVerifiedPose)
            return;

        postPhysicsSamples++;
        maxObservedDivergence = Mathf.Max(maxObservedDivergence, divergence);
    }
}
