using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Reproduces soft-correction accumulators leaking from one pooled lifetime into the next.
/// The probe continuously creates a local-only pose error, is despawned, and is eventually
/// reused after the rollback pool hands it back to the prefab pool.
/// </summary>
public sealed class SoftCorrectionPoolReuseScenario : Scenario
{
    private const float TimeoutSeconds = 90f;

    private GameObject _driverPrefab;
    private GameObject _probePrefab;
    private int _driverPrefabId;
    private ulong _startTick;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = new GameObject(nameof(SoftCorrectionPoolProbe));
        _probePrefab.SetActive(false);
        var scope = _probePrefab.AddComponent<PredictionPolicyScope>();
        scope.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
        _probePrefab.AddComponent<SoftCorrectionPoolProbe>();
        UnityEngine.Object.DontDestroyOnLoad(_probePrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab, pooled: true, warmupCount: 1);

        _driverPrefab = PredictionTestUtils.CreatePrefab<SoftCorrectionPoolDriver>(
            nameof(SoftCorrectionPoolDriver));
        _driverPrefab.GetComponent<SoftCorrectionPoolDriver>().probePrefab = _probePrefab;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _startTick = startTick;
        SoftCorrectionPoolProbe.ResetStats();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ctx.predictionManager.time.tick >= _startTick,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"never reached scheduled start tick {_startTick}");
        }

        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_driverPrefab, out _driverPrefabId);
        if (!pm.hierarchy.Create(_driverPrefab).HasValue)
            return ScenarioResult.Fail("failed to create soft-correction pool driver");

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => DriverFinished(pm),
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"pool driver timed out: priorLifetimeReuses={SoftCorrectionPoolProbe.priorLifetimeReuses}, " +
                $"verifiedCorrections={SoftCorrectionPoolProbe.verifiedCorrections}, " +
                $"staleCorrections={SoftCorrectionPoolProbe.staleCorrectionReuses}");
        }

        var report = $"tickRate={pm.tickRate}; lifetimeSeconds={SoftCorrectionPoolDriver.LifetimeSeconds}; " +
                     $"gapSeconds={SoftCorrectionPoolDriver.GapSeconds}; liveTicks={SoftCorrectionPoolProbe.liveTicks}; " +
                     $"verifiedCorrections={SoftCorrectionPoolProbe.verifiedCorrections}; " +
                     $"priorLifetimeReuses={SoftCorrectionPoolProbe.priorLifetimeReuses}; " +
                     $"correctedLifetimeReuses={SoftCorrectionPoolProbe.correctedLifetimeReuses}; " +
                     $"staleCorrections={SoftCorrectionPoolProbe.staleCorrectionReuses}";

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok(report);

        if (SoftCorrectionPoolProbe.verifiedCorrections == 0)
            return ScenarioResult.Fail($"the client never received a soft-correction target: {report}");

        if (SoftCorrectionPoolProbe.priorLifetimeReuses == 0)
            return ScenarioResult.Fail($"the probe was never reused after a completed pooled lifetime: {report}");

        if (SoftCorrectionPoolProbe.correctedLifetimeReuses == 0 || SoftCorrectionPoolProbe.liveTicks == 0)
            return ScenarioResult.Fail($"no live reuse followed a lifetime with a verified correction: {report}");

        if (SoftCorrectionPoolProbe.staleCorrectionReuses != 0)
        {
            return ScenarioResult.Fail(
                $"{SoftCorrectionPoolProbe.staleCorrectionReuses}/" +
                $"{SoftCorrectionPoolProbe.priorLifetimeReuses} pooled lifetimes started with a stale correction: {report}");
        }

        return ScenarioResult.Ok(report);
    }

    private bool DriverFinished(PredictionManager pm)
    {
        ref var hierarchyState = ref pm.hierarchy.currentState;
        for (var i = 0; i < hierarchyState.spawnedPrefabs.Count; i++)
        {
            var details = hierarchyState.spawnedPrefabs[i];
            if (details.prefabId != _driverPrefabId)
                continue;

            if (details.instanceId.TryGetComponent<SoftCorrectionPoolDriver>(pm, out var driver))
                return driver.currentState.completed >= SoftCorrectionPoolDriver.TotalLifetimes;
        }

        return false;
    }
}

public sealed class SoftCorrectionPoolDriver : PredictedIdentity<SoftCorrectionPoolDriver.DriverState>
{
    public const int TotalLifetimes = 18;
    // Preserve the original 20 Hz fixture's durations. At 60 Hz, 18 ticks was only
    // 0.3 seconds: a lifetime could end before a high-latency client received a target.
    public const float LifetimeSeconds = 18f / 20f;
    public const float GapSeconds = 3f / 20f;

    public GameObject probePrefab;

    public struct DriverState : IPredictedData<DriverState>
    {
        public uint phaseTicks;
        public uint activeId;
        public int spawned;
        public int completed;
        public bool active;

        public void Dispose() { }
    }

    protected override void Simulate(ref DriverState state, float delta)
    {
        if (!probePrefab || state.completed >= TotalLifetimes)
            return;

        state.phaseTicks++;

        if (state.active)
        {
            if (state.phaseTicks < Mathf.CeilToInt(LifetimeSeconds * predictionManager.tickRate))
                return;

            hierarchy.Delete((PredictedObjectID)state.activeId);
            state.active = false;
            state.phaseTicks = 0;
            state.completed++;
            return;
        }

        if (state.phaseTicks < Mathf.CeilToInt(GapSeconds * predictionManager.tickRate) || state.spawned >= TotalLifetimes)
            return;

        var created = hierarchy.Create(probePrefab, new Vector3(60f, 4f, 0f), Quaternion.identity, owner);
        if (!created.HasValue)
            return;

        state.activeId = created.Value.instanceId.value;
        state.active = true;
        state.phaseTicks = 0;
        state.spawned++;
    }
}

public sealed class SoftCorrectionPoolProbe : PredictedTransform
{
    private const float InjectedOffset = 1f;
    private const float StaleMovementThreshold = 0.0001f;

    public static int priorLifetimeReuses { get; private set; }
    public static int staleCorrectionReuses { get; private set; }
    public static int verifiedCorrections { get; private set; }
    public static int correctedLifetimeReuses { get; private set; }
    public static int liveTicks { get; private set; }

    private bool _completedPooledLifetime;
    private bool _reusedAfterCompletedLifetime;
    private bool _checkedFirstLiveTick;
    private bool _lifetimeReceivedCorrection;
    private bool _completedLifetimeHadCorrection;
    private bool _reusedAfterCorrection;
    private Vector3 _spawnPosition;

    public static void ResetStats()
    {
        priorLifetimeReuses = 0;
        staleCorrectionReuses = 0;
        verifiedCorrections = 0;
        correctedLifetimeReuses = 0;
        liveTicks = 0;
    }

    public override void ResetState()
    {
        _reusedAfterCompletedLifetime = false;
        _checkedFirstLiveTick = false;
        _lifetimeReceivedCorrection = false;
        _spawnPosition = default;
        // The base reset invokes OnRemovedFromPool. Keep the lifetime provenance
        // established by that callback for the first subsequent live-tick check.
        base.ResetState();
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        // Setup has positioned the object, but its initial predicted state is captured later.
        _spawnPosition = transform.position;
        _checkedFirstLiveTick = false;
    }

    protected override void OnAddedToPool()
    {
        base.OnAddedToPool();
        _completedPooledLifetime = true;
        _completedLifetimeHadCorrection = _lifetimeReceivedCorrection;
    }

    protected override void OnRemovedFromPool()
    {
        base.OnRemovedFromPool();
        _reusedAfterCompletedLifetime = _completedPooledLifetime;
        _reusedAfterCorrection = _completedLifetimeHadCorrection;
        if (_reusedAfterCompletedLifetime)
            priorLifetimeReuses++;
    }

    protected override void OnVerifiedStateReceived(
        ulong tick,
        in PredictedTransformState predicted,
        in PredictedTransformState verified)
    {
        base.OnVerifiedStateReceived(tick, in predicted, in verified);
        if (predictionManager && !predictionManager.cachedIsServer)
        {
            verifiedCorrections++;
            _lifetimeReceivedCorrection = true;
        }
    }

    protected override void Simulate(ref PredictedTransformState state, float delta)
    {
        bool liveClient = predictionManager &&
                          !predictionManager.cachedIsServer &&
                          !predictionManager.isReplaying &&
                          !predictionManager.isVerified;

        var beforeCorrection = state.unityPosition;
        base.Simulate(ref state, delta);

        if (liveClient && !_checkedFirstLiveTick)
        {
            if (_reusedAfterCorrection)
                correctedLifetimeReuses++;
            if (_reusedAfterCompletedLifetime &&
                (state.unityPosition - beforeCorrection).sqrMagnitude > StaleMovementThreshold * StaleMovementThreshold)
            {
                staleCorrectionReuses++;
            }

            _checkedFirstLiveTick = true;
        }

        if (!liveClient)
            return;

        liveTicks++;
        state.unityPosition = _spawnPosition + Vector3.right * InjectedOffset;
        transform.SetPositionAndRotation(state.unityPosition, state.unityRotation);
    }
}

/// <summary>
/// Assigns SoftCorrection to an opted-in regular state identity. The client injects a one-time
/// state error and expects the probe to apply verified state to its live timeline without replay.
/// </summary>
public sealed class GenericSoftCorrectionScenario : Scenario
{
    private const float TimeoutSeconds = 60f;
    private const float SettleSeconds = 3f;
    private const float AllowedResidualError = 30f;

    private GameObject _probePrefab;
    private ulong _startTick;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = PredictionTestUtils.CreatePrefab<GenericSoftCorrectionProbe>(
            nameof(GenericSoftCorrectionProbe));
        _probePrefab.GetComponent<GenericSoftCorrectionProbe>().configuredPredictionPolicy =
            PredictionPolicy.SoftCorrection;
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _startTick = startTick;
        GenericSoftCorrectionProbe.ResetStats();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ctx.predictionManager.time.tick >= _startTick,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"never reached scheduled start tick {_startTick} " +
                $"(current tick {ctx.predictionManager.time.tick})");
        }

        if (!ctx.predictionManager.hierarchy.Create(_probePrefab).HasValue)
            return ScenarioResult.Fail("failed to create generic soft-correction probe");

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => GenericSoftCorrectionProbe.injectionApplied &&
                      GenericSoftCorrectionProbe.instances.Count > 0 &&
                      GenericSoftCorrectionProbe.instances[0].verifiedState.HasValue &&
                      GenericSoftCorrectionProbe.instances[0].postInjectionCallbacks > 0,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"generic probe did not receive usable state: injected={GenericSoftCorrectionProbe.injectionApplied}, " +
                $"instances={GenericSoftCorrectionProbe.instances.Count}; " +
                (GenericSoftCorrectionProbe.instances.Count > 0 ? GenericSoftCorrectionProbe.instances[0].Report() : "no probe"));
        }

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);

        var probe = GenericSoftCorrectionProbe.instances[0];
        if (probe.predictionPolicy != PredictionPolicy.SoftCorrection)
        {
            return ScenarioResult.Fail(
                $"generic probe did not opt into SoftCorrection: policy={probe.predictionPolicy}");
        }

        var verified = probe.verifiedState;
        if (!verified.HasValue)
            return ScenarioResult.Fail("generic probe lost its verified state");

        float divergence = probe.matchedDivergence;
        if (!(divergence <= AllowedResidualError))
        {
            return ScenarioResult.Fail(
                $"generic SoftCorrection state never converged: divergence={divergence:F1}, " +
                $"{probe.Report()}");
        }

        if (probe.replayViolations > 0)
            return ScenarioResult.Fail($"generic soft identity simulated during replay: {probe.Report()}");

        return ScenarioResult.Ok(probe.Report());
    }
}

public sealed class GenericSoftCorrectionProbe : PredictedIdentity<GenericSoftCorrectionProbe.ProbeState>
{
    private const float ClientOnlyError = 100f;
    private const int InjectAfterLiveTicks = 12;

    public static readonly List<GenericSoftCorrectionProbe> instances = new();
    public static bool injectionApplied { get; private set; }
    public static float maxObservedDivergence { get; private set; }

    private int _liveTicks;
    private bool _injectedThisLifetime;
    private GenericSoftCorrectionProgress _progress;
    private double _targetReceivedAt;
    private ulong _targetTick;
    private int _nativeReplacements;

    public int postInjectionCallbacks { get; private set; }
    public int replayViolations { get; private set; }
    public float matchedDivergence => _progress.Residual(currentState.value);

    public override bool supportsSoftCorrection => true;

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public float value;

        public void Dispose() { }
    }

    public static void ResetStats()
    {
        instances.Clear();
        injectionApplied = false;
        maxObservedDivergence = 0f;
    }

    protected override void LateAwake()
    {
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    protected override void Simulate(ref ProbeState state, float delta)
    {
        state.value += 1f;

        if (!predictionManager || predictionManager.cachedIsServer ||
            predictionManager.isReplaying || predictionManager.isVerified)
        {
            if (predictionManager && !predictionManager.cachedIsServer && _liveTicks > 0)
                replayViolations++;
            return;
        }

        _progress.AdvanceLiveTick();
        _liveTicks++;
        if (_injectedThisLifetime || _liveTicks < InjectAfterLiveTicks || !_progress.hasTarget)
            return;

        state.value += ClientOnlyError;
        _injectedThisLifetime = true;
        injectionApplied = true;
        maxObservedDivergence = Mathf.Max(maxObservedDivergence, ClientOnlyError);
    }

    protected override void OnVerifiedStateReceived(
        ulong tick,
        in ProbeState predicted,
        in ProbeState verified)
    {
        currentState.value = verified.value;
        RecordTarget(verified.value, tick);
        if (_injectedThisLifetime)
            postInjectionCallbacks++;
    }

    protected override void SetUnityState(ProbeState state)
    {
        // Native full-state rollback bypasses the soft callback. It is an actual
        // authoritative replacement, not permission to bless an ordinary live fault.
        if (predictionManager && !predictionManager.cachedIsServer && predictionManager.isVerified)
        {
            RecordTarget(state.value, 0); // This hook does not expose the incoming tick.
            _nativeReplacements++;
        }
    }

    private void RecordTarget(float value, ulong tick)
    {
        _progress.RecordTarget(value);
        _targetTick = tick;
        _targetReceivedAt = Time.realtimeSinceStartupAsDouble;
    }

    public string Report()
        => FormattableString.Invariant(
            $"residual={matchedDivergence:F1}; rawLag={(verifiedState.HasValue ? currentState.value - verifiedState.Value.value : float.NaN):F1}; live={currentState.value:F1}; target={_progress.targetValue:F1}; liveStepsSinceTarget={_progress.liveSteps}; targetTick={_targetTick}; targetAgeSeconds={Time.realtimeSinceStartupAsDouble - _targetReceivedAt:F3}; postInjectionCallbacks={postInjectionCallbacks}; nativeReplacements={_nativeReplacements}; maxInjected={maxObservedDivergence:F1}; replayViolations={replayViolations}; policy={predictionPolicy}");
}

// Count actual live calls, not tick-label differences: pacing can jump tick labels.
internal struct GenericSoftCorrectionProgress
{
    public bool hasTarget { get; private set; }
    public float targetValue { get; private set; }
    public ulong liveSteps { get; private set; }

    public void RecordTarget(float value)
    {
        hasTarget = true;
        targetValue = value;
        liveSteps = 0;
    }

    public void AdvanceLiveTick() => liveSteps++;
    public float Residual(float current) => hasTarget
        ? Mathf.Abs(current - (targetValue + liveSteps))
        : float.PositiveInfinity;
}

/// <summary>
/// Switches a dynamic body from FullPrediction to SoftCorrection from inside a replayed tick.
/// The next physics pass must see the body frozen; otherwise replay physics advances live state.
/// </summary>
public sealed class ReplayPolicyTransitionScenario : Scenario
{
    private const float TimeoutSeconds = 60f;

    private GameObject _probePrefab;
    private ulong _startTick;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _probePrefab = new GameObject(nameof(ReplayPolicyTransitionProbe));
        _probePrefab.SetActive(false);
        var body = _probePrefab.AddComponent<Rigidbody>();
        body.useGravity = false;
        body.constraints = RigidbodyConstraints.None;
        _probePrefab.AddComponent<PredictedTransform>();
        var probe = _probePrefab.AddComponent<ReplayPolicyTransitionProbe>();
        probe.configuredPredictionPolicy = PredictionPolicy.FullPrediction;
        UnityEngine.Object.DontDestroyOnLoad(_probePrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        _startTick = startTick;
        ReplayPolicyTransitionProbe.ResetStats();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ctx.predictionManager.time.tick >= _startTick,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"never reached scheduled start tick {_startTick} " +
                $"(current tick {ctx.predictionManager.time.tick})");
        }

        if (!ctx.predictionManager.hierarchy.Create(
                _probePrefab,
                new Vector3(80f, 4f, 0f),
                Quaternion.identity).HasValue)
        {
            return ScenarioResult.Fail("failed to create replay policy-transition probe");
        }

        if (ctx.role != NetworkRole.Client)
            return ScenarioResult.Ok();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ReplayPolicyTransitionProbe.transitionsDuringReplay > 0 &&
                      ReplayPolicyTransitionProbe.replayPhysicsChecks > 0,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"no replay transition was observed: transitions={ReplayPolicyTransitionProbe.transitionsDuringReplay}, " +
                $"physicsChecks={ReplayPolicyTransitionProbe.replayPhysicsChecks}");
        }

        if (ReplayPolicyTransitionProbe.unfrozenReplayPhysicsPasses != 0)
        {
            return ScenarioResult.Fail(
                $"body entered SoftCorrection during replay but remained unfrozen for " +
                $"{ReplayPolicyTransitionProbe.unfrozenReplayPhysicsPasses} physics pass(es)");
        }

        return ScenarioResult.Ok();
    }
}

public sealed class ReplayPolicyTransitionProbe : PredictedRigidbody
{
    public static int transitionsDuringReplay { get; private set; }
    public static int replayPhysicsChecks { get; private set; }
    public static int unfrozenReplayPhysicsPasses { get; private set; }

    private bool _pendingReplayPhysicsCheck;

    public static void ResetStats()
    {
        transitionsDuringReplay = 0;
        replayPhysicsChecks = 0;
        unfrozenReplayPhysicsPasses = 0;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        predictionManager.onAfterPhysicsPass += OnAfterPhysicsPass;
    }

    protected override void Destroyed()
    {
        if (predictionManager)
            predictionManager.onAfterPhysicsPass -= OnAfterPhysicsPass;
        base.Destroyed();
    }

    protected override void Simulate(ref UnityRigidbodyState state, float delta)
    {
        base.Simulate(ref state, delta);

        if (!predictionManager || predictionManager.cachedIsServer ||
            !predictionManager.isReplaying || predictionPolicy != PredictionPolicy.FullPrediction)
        {
            return;
        }

        transitionsDuringReplay++;
        _pendingReplayPhysicsCheck = true;
        SetPredictionPolicy(PredictionPolicy.SoftCorrection);
    }

    private void OnAfterPhysicsPass()
    {
        if (!_pendingReplayPhysicsCheck || !predictionManager || !predictionManager.isReplaying)
            return;

        replayPhysicsChecks++;
        if (rb.constraints != RigidbodyConstraints.FreezeAll)
            unfrozenReplayPhysicsPasses++;
        _pendingReplayPhysicsCheck = false;
    }
}
