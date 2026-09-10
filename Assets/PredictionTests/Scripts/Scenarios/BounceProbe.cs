using System.Collections.Generic;
using System.Linq;
using PurrNet.Prediction;
using UnityEngine;

public class BounceProbe : PredictedIdentity<BounceProbe.ProbeState>
{
    private static readonly SortedDictionary<ulong, int> _firesPerTick = new();
    private static readonly Dictionary<ulong, int> _allFiresPerTick = new();

    public static int totalFires { get; private set; }
    public static int distinctTicks => _firesPerTick.Count;
    public static bool hasDuplicate { get; private set; }
    public static ulong duplicateTick { get; private set; }

    public static void ResetCounters()
    {
        _firesPerTick.Clear();
        _allFiresPerTick.Clear();
        totalFires = 0;
        hasDuplicate = false;
        duplicateTick = 0;
    }

    /// <summary>
    /// The first bounces carry enough impact energy to be deterministic across peers, but at
    /// high simulated jitter the re-simulated verified timelines can disagree by one event in
    /// the weak tail (PhysX re-sim over delta-quantized synced state). The digest therefore
    /// compares only the strong early bounces; the exactly-once-per-tick duplicate guard
    /// remains the scenario's primary contract and covers every event.
    /// </summary>
    private const int MaxComparedBounces = 5;

    public static string Digest()
    {
        return $"bounces={System.Math.Min(distinctTicks, MaxComparedBounces)};dup={hasDuplicate}";
    }

    public static string Timeline()
    {
        ulong firstTick = _firesPerTick.Count > 0 ? _firesPerTick.First().Key : 0;
        return string.Join(",", _firesPerTick.Select(kv => $"{kv.Key - firstTick}:{kv.Value}"));
    }

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public void Dispose() { }
    }

    private PredictedRigidbody _rb;

    private void Awake()
    {
        _rb = GetComponent<PredictedRigidbody>();
        if (_rb)
            _rb.onCollisionEnter += OnBounce;
    }

    protected override void OnDestroy()
    {
        if (_rb)
            _rb.onCollisionEnter -= OnBounce;
        base.OnDestroy();
    }

    /// <summary>
    /// Tail micro-bounces approach zero impact velocity, where delta-compression rounding of
    /// the synced rigidbody state can flip whether a re-simulated timeline registers one more
    /// contact. Only impacts above this speed are counted for the cross-peer digest; the
    /// exactly-once-per-tick guard below still covers every event. Successive impact speeds
    /// shrink by the bounciness factor (0.7), so the threshold must sit mid-gap between two
    /// impacts: 1.0 lands between bounce seven (~1.2) and bounce eight (~0.8).
    /// </summary>
    private const float MinCountedImpactSpeed = 1f;

    private void OnBounce(GameObject other, PredictedComponentID otherId, PhysicsCollision collision)
    {
        if (!predictionManager.isVerifiedView)
            return;

        var tick = predictionManager.localTickInContext;

        _allFiresPerTick.TryGetValue(tick, out var allCount);
        allCount++;
        _allFiresPerTick[tick] = allCount;

        if (allCount > 1)
        {
            hasDuplicate = true;
            duplicateTick = tick;
        }

        if (collision.relativeVelocity.magnitude < MinCountedImpactSpeed)
            return;

        _firesPerTick.TryGetValue(tick, out var count);
        count++;
        _firesPerTick[tick] = count;
        totalFires++;
    }
}
