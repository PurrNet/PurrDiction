using System.Collections.Generic;
using System.Linq;
using PurrNet.Prediction;
using UnityEngine;

public class BounceProbe : PredictedIdentity<BounceProbe.ProbeState>
{
    private static readonly SortedDictionary<ulong, int> _firesPerTick = new();

    public static int totalFires { get; private set; }
    public static int distinctTicks => _firesPerTick.Count;
    public static bool hasDuplicate { get; private set; }
    public static ulong duplicateTick { get; private set; }

    public static void ResetCounters()
    {
        _firesPerTick.Clear();
        totalFires = 0;
        hasDuplicate = false;
        duplicateTick = 0;
    }

    public static string Digest()
    {
        return "bounces=" + string.Join(",", _firesPerTick.Select(kv => $"{kv.Key}:{kv.Value}"));
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

    private void OnBounce(GameObject other, PhysicsCollision collision)
    {
        if (!predictionManager.isVerified)
            return;

        var tick = predictionManager.time.tick;
        _firesPerTick.TryGetValue(tick, out var count);
        count++;
        _firesPerTick[tick] = count;
        totalFires++;

        if (count > 1)
        {
            hasDuplicate = true;
            duplicateTick = tick;
        }
    }
}
