using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Prediction.Profiler;
using UnityEngine;

public sealed class InterestManagementScenario : Scenario
{
    private const float Timeout = 90f;
    private const float RootOffset = 100f;
    private const float HoldTier0Distance = 35f;
    private const float Tier1Distance = 45f;
    private const float HoldTier1Distance = 35f;
    private const float Tier2Distance = 75f;
    private const float HoldTier2Distance = 65f;
    private const float CullDistance = 105f;
    private const float HoldCulledDistance = 95f;
    private const float ReentryTier2Distance = 85f;
    private const float ApproachDistance = 25f;
    private const int InitialBarrier = 21000;
    private const int HoldTier0Barrier = 21001;
    private const int Tier1Barrier = 21002;
    private const int HoldTier1Barrier = 21003;
    private const int Tier2Barrier = 21004;
    private const int HoldTier2Barrier = 21005;
    private const int OwnerBarrier = 21006;
    private const int OwnerReleaseBarrier = 21007;
    private const int CulledBarrier = 21008;
    private const int HoldCulledBarrier = 21009;
    private const int ReentryTier2Barrier = 21010;
    private const int ApproachBarrier = 21011;
    private const int DigestChannel = 21012;
    private const int FinalInputBarrier = 21013;
    private const int FrameSampleCount = 30;
    private const int RateSampleTicks = 32;

    private PredictionLODProfile _predictionProfile;
    private NetworkLODProfile _networkProfile;
    private GameObject _anchorPrefab;
    private GameObject _probePrefab;
    private GameObject _rateProbePrefab;
    private GameObject _replayTierProbePrefab;
    private GameObject _inputProbePrefab;
    private int _anchorPrefabId;
    private int _probePrefabId;
    private int _rateProbePrefabId;
    private int _replayTierProbePrefabId;
    private int _inputProbePrefabId;

    private readonly List<PlayerID> _players = new(2);
    private InterestAnchorMarker _serverAnchorA;
    private InterestAnchorMarker _serverAnchorB;
    private InterestStateProbe _serverProbeA;
    private InterestStateProbe _serverProbeB;
    private InterestRateProbe _serverRateProbeA;
    private InterestRateProbe _serverRateProbeB;
    private InterestReplayTierProbe _serverReplayTierProbeA;
    private InterestReplayTierProbe _serverReplayTierProbeB;
    private InterestInputProbe _serverInputProbeA;
    private InterestInputProbe _serverInputProbeB;
    private InterestAnchorMarker _localAnchor;
    private InterestStateProbe _localNearProbe;
    private InterestRateProbe _localNearRateProbe;
    private InterestReplayTierProbe _localNearReplayTierProbe;
    private InterestInputProbe _localObservedInputProbe;
    private PredictedObjectID _localNearProbeRoot;
    private PredictedObjectID _localFarProbeRoot;
    private PredictedObjectID _localNearRateProbeRoot;
    private PredictedObjectID _localFarRateProbeRoot;
    private PredictedObjectID _localNearReplayTierProbeRoot;
    private PredictedObjectID _localFarReplayTierProbeRoot;
    private PredictedObjectID _localObservedInputProbeRoot;
    private Dictionary<PlayerID, double> _visibleFrameBytes;
    private Dictionary<PlayerID, double> _culledFrameBytes;
    private RateWriteSample _tier0Writes;
    private RateWriteSample _tier1Writes;
    private RateWriteSample _tier2Writes;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _networkProfile = ScriptableObject.CreateInstance<NetworkLODProfile>();
        _networkProfile.Configure(new[]
        {
            new NetworkLODTier
            {
                maxDistance = 30f,
                hysteresis = 10f,
                sendIntervalTicks = 1
            },
            new NetworkLODTier
            {
                maxDistance = 60f,
                hysteresis = 10f,
                sendIntervalTicks = 2
            },
            new NetworkLODTier
            {
                maxDistance = 90f,
                hysteresis = 10f,
                sendIntervalTicks = 4
            }
        }, true);

        _predictionProfile = ScriptableObject.CreateInstance<PredictionLODProfile>();
        _predictionProfile.Configure(
            _networkProfile,
            new[]
            {
                new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.KeepConfigured },
                new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.SoftCorrection },
                new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.ServerRelay }
            },
            PredictionPolicyOverride.ServerRelay);
        ctx.predictionManager.predictionLODProfile = _predictionProfile;

        InterestAnchorMarker.ResetInstances();
        InterestStateProbe.ResetInstances();
        InterestRateProbe.ResetInstances();
        InterestReplayTierProbe.ResetInstances();
        InterestInputProbe.ResetInstances();
        InterestInputReportGate.Reset();
        InterestRateReportGate.Reset();

        _anchorPrefab = PredictionTestUtils.CreatePrefab<InterestAnchorMarker>("InterestAnchor");
        _anchorPrefab.AddComponent<PredictedTransform>();
        _anchorPrefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count;
        PredictionTestUtils.RegisterPrefab(ctx, _anchorPrefab);

        _probePrefab = PredictionTestUtils.CreatePrefab<InterestStateProbe>("InterestProbe");
        _probePrefab.AddComponent<PredictedTransform>();
        var probeBody = _probePrefab.AddComponent<Rigidbody>();
        probeBody.useGravity = false;
        _probePrefab.AddComponent<LocalPhysics>();
        _probePrefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count;
        PredictionTestUtils.RegisterPrefab(ctx, _probePrefab);

        _rateProbePrefab = PredictionTestUtils.CreatePrefab<InterestRateProbe>("InterestRateProbe");
        _rateProbePrefab.GetComponent<InterestRateProbe>().configuredPredictionPolicy =
            PredictionPolicy.FullPrediction;
        _rateProbePrefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count;
        PredictionTestUtils.RegisterPrefab(ctx, _rateProbePrefab);

        _replayTierProbePrefab = new GameObject("InterestReplayTierProbe");
        _replayTierProbePrefab.SetActive(false);
        var replayBody = _replayTierProbePrefab.AddComponent<Rigidbody>();
        replayBody.useGravity = false;
        _replayTierProbePrefab.AddComponent<PredictedTransform>();
        var replayTierProbe = _replayTierProbePrefab.AddComponent<InterestReplayTierProbe>();
        replayTierProbe.configuredPredictionPolicy = PredictionPolicy.FullPrediction;
        UnityEngine.Object.DontDestroyOnLoad(_replayTierProbePrefab);
        _replayTierProbePrefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count;
        PredictionTestUtils.RegisterPrefab(ctx, _replayTierProbePrefab);

        _inputProbePrefab = PredictionTestUtils.CreatePrefab<InterestInputProbe>("InterestInputProbe");
        _inputProbePrefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count;
        PredictionTestUtils.RegisterPrefab(ctx, _inputProbePrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (ctx.role == NetworkRole.Host || ctx.expectedConnections != 2)
            return ScenarioResult.Fail("interest scenario requires one pure server and exactly two client processes");

        if (!ctx.predictionManager.interest.enabled)
            return ScenarioResult.Fail("prediction interest did not initialize with the runtime profile");

        return await RunSplit(ctx, RunClient, RunServer);
    }

    private async UniTask<ScenarioResult> RunServer(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        string stage = "players";

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => CopyPlayers(pm),
                Timeout,
                ctx.cancellationToken);

            stage = "spawn";
            if (!CreateServerRoots(pm))
                return ScenarioResult.Fail("failed to create interest anchors or probes");

            stage = "initial tiers";
            await UniTaskUtils.WaitWithTimeout(
                () => HasNeighborhoodTiers(pm, 0),
                Timeout,
                ctx.cancellationToken);
            await WaitServerTicks(pm, 24, ctx);
            _tier0Writes = await SampleRateWrites(ctx, pm, 1);
            if (_tier0Writes.failure != null)
                return ScenarioResult.Fail(_tier0Writes.failure);
            _visibleFrameBytes = await SampleFrameBytes(ctx);
            await ScenarioBarrier.Wait(ctx, InitialBarrier, Timeout);

            stage = "tier 0 hysteresis";
            SetAnchorDistance(HoldTier0Distance);
            await WaitServerTicks(pm, 16, ctx);
            if (!HasNeighborhoodTiers(pm, 0))
                return ScenarioResult.Fail($"tier 0 hysteresis failed: {DescribeServerTiers(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldTier0Barrier, Timeout);

            stage = "tier 1";
            SetAnchorDistance(Tier1Distance);
            await UniTaskUtils.WaitWithTimeout(
                () => HasNeighborhoodTiers(pm, 1),
                Timeout,
                ctx.cancellationToken);
            await WaitServerTicks(pm, 24, ctx);
            _tier1Writes = await SampleRateWrites(ctx, pm, 2);
            if (_tier1Writes.failure != null)
                return ScenarioResult.Fail(_tier1Writes.failure);
            _serverRateProbeA.Freeze(1);
            _serverRateProbeB.Freeze(1);
            var convergenceResult = await InterestRateReportGate.Compare(
                ctx,
                1,
                _serverRateProbeA,
                _serverRateProbeB,
                Timeout);
            if (!convergenceResult.success)
                return convergenceResult;
            _serverRateProbeA.Resume();
            _serverRateProbeB.Resume();
            await ScenarioBarrier.Wait(ctx, Tier1Barrier, Timeout);

            stage = "tier 1 hysteresis";
            SetAnchorDistance(HoldTier1Distance);
            await WaitServerTicks(pm, 16, ctx);
            if (!HasNeighborhoodTiers(pm, 1))
                return ScenarioResult.Fail($"tier 1 hysteresis failed: {DescribeServerTiers(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldTier1Barrier, Timeout);

            stage = "tier 2";
            SetAnchorDistance(Tier2Distance);
            await UniTaskUtils.WaitWithTimeout(
                () => HasNeighborhoodTiers(pm, 2),
                Timeout,
                ctx.cancellationToken);
            await WaitServerTicks(pm, 24, ctx);
            _tier2Writes = await SampleRateWrites(ctx, pm, 4);
            if (_tier2Writes.failure != null)
                return ScenarioResult.Fail(_tier2Writes.failure);
            var rateFailure = ValidateRateScaling();
            if (rateFailure != null)
                return ScenarioResult.Fail(rateFailure);
            _serverRateProbeA.Freeze(2);
            _serverRateProbeB.Freeze(2);
            convergenceResult = await InterestRateReportGate.Compare(
                ctx,
                2,
                _serverRateProbeA,
                _serverRateProbeB,
                Timeout);
            if (!convergenceResult.success)
                return convergenceResult;
            _serverRateProbeA.Resume();
            _serverRateProbeB.Resume();
            await ScenarioBarrier.Wait(ctx, Tier2Barrier, Timeout);

            stage = "tier 2 hysteresis";
            SetAnchorDistance(HoldTier2Distance);
            await WaitServerTicks(pm, 16, ctx);
            if (!HasNeighborhoodTiers(pm, 2))
                return ScenarioResult.Fail($"tier 2 hysteresis failed: {DescribeServerTiers(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldTier2Barrier, Timeout);

            stage = "owner exemption";
            SetAnchorDistance(Tier2Distance);
            await WaitServerTicks(pm, 4, ctx);
            pm.SetOwnership(_serverRateProbeA.rootObjectId, _players[0]);
            pm.SetOwnership(_serverRateProbeB.rootObjectId, _players[1]);
            await UniTaskUtils.WaitWithTimeout(
                () => HasOwnedRateExemptionTiers(pm),
                Timeout,
                ctx.cancellationToken);
            await WaitServerTicks(pm, 12, ctx);
            await ScenarioBarrier.Wait(ctx, OwnerBarrier, Timeout);

            stage = "owner release";
            pm.SetOwnership(_serverRateProbeA.rootObjectId, null);
            pm.SetOwnership(_serverRateProbeB.rootObjectId, null);
            await UniTaskUtils.WaitWithTimeout(
                () => HasNeighborhoodTiers(pm, 2),
                Timeout,
                ctx.cancellationToken);
            await WaitServerTicks(pm, 12, ctx);
            await ScenarioBarrier.Wait(ctx, OwnerReleaseBarrier, Timeout);

            stage = "cull";
            SetAnchorDistance(CullDistance);
            await UniTaskUtils.WaitWithTimeout(
                () => HasCulledNeighborhoodTiers(pm),
                Timeout,
                ctx.cancellationToken);
            await WaitServerTicks(pm, 12, ctx);
            _culledFrameBytes = await SampleFrameBytes(ctx);
            var bandwidthFailure = ValidateFrameByteReduction();
            if (bandwidthFailure != null)
                return ScenarioResult.Fail(bandwidthFailure);
            await WaitServerTicks(pm, 40, ctx);
            await ScenarioBarrier.Wait(ctx, CulledBarrier, Timeout);

            stage = "culled hysteresis";
            SetAnchorDistance(HoldCulledDistance);
            await WaitServerTicks(pm, 16, ctx);
            if (!HasCulledNeighborhoodTiers(pm))
                return ScenarioResult.Fail($"culled hysteresis failed: {DescribeServerTiers(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldCulledBarrier, Timeout);

            stage = "tier 2 reentry";
            SetAnchorDistance(ReentryTier2Distance);
            await UniTaskUtils.WaitWithTimeout(
                () => HasNeighborhoodTiers(pm, 2),
                Timeout,
                ctx.cancellationToken);
            await ScenarioBarrier.Wait(ctx, ReentryTier2Barrier, Timeout);

            stage = "approach";
            SetAnchorDistance(ApproachDistance);
            await UniTaskUtils.WaitWithTimeout(
                () => HasNeighborhoodTiers(pm, 0),
                Timeout,
                ctx.cancellationToken);
            await ScenarioBarrier.Wait(ctx, ApproachBarrier, Timeout);

            BroadcastStopInputGeneration();
            stage = "input finalization";
            await UniTaskUtils.WaitWithTimeout(
                () => _serverInputProbeA.currentState.finalized && _serverInputProbeB.currentState.finalized,
                Timeout,
                ctx.cancellationToken);
            await ScenarioBarrier.Wait(ctx, FinalInputBarrier, Timeout);

            var inputResult = await InterestInputReportGate.Compare(
                ctx, _serverInputProbeA, _serverInputProbeB, Timeout);
            if (!inputResult.success)
                return inputResult;

            if (!_serverProbeA.stateIsValid || !_serverProbeB.stateIsValid ||
                !_serverRateProbeA.stateIsValid || !_serverRateProbeB.stateIsValid)
                return ScenarioResult.Fail("server probe state failed its checksum");

            var digestResult = await DigestExchange.Compare(
                ctx,
                DigestChannel,
                BuildDigest(pm, _serverProbeA, _serverRateProbeA),
                Timeout);
            return digestResult.success
                ? ScenarioResult.Ok($"{digestResult.message};{FrameByteSummary()}")
                : digestResult;
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"server timed out during {stage}: {DescribeServerTiers(pm)}");
        }
    }

    [ObserversRpc(runLocally: true)]
    private static void BroadcastStopInputGeneration()
    {
        InterestInputProbe.StopInputGeneration();
    }

    private async UniTask<ScenarioResult> RunClient(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        string stage = "layout";

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ResolveLocalLayout(pm),
                Timeout,
                ctx.cancellationToken);

            stage = "initial tiers";
            await UniTaskUtils.WaitWithTimeout(
                () => _localNearProbe.isRelevant &&
                      _localNearRateProbe.isRelevant &&
                      _localNearReplayTierProbe.isRelevant &&
                      _localObservedInputProbe.isRelevant &&
                      IsLocallyCulled(pm, _localFarProbeRoot) &&
                      IsLocallyCulled(pm, _localFarRateProbeRoot) &&
                      IsLocallyCulled(pm, _localFarReplayTierProbeRoot),
                Timeout,
                ctx.cancellationToken);

            if (!HasRetainedHierarchy(pm))
                return ScenarioResult.Fail("culled root was removed from the replicated hierarchy");
            if (pm.hierarchy.TryGetGameObject(_localFarProbeRoot, out _) ||
                pm.hierarchy.TryGetGameObject(_localFarRateProbeRoot, out _) ||
                pm.hierarchy.TryGetGameObject(_localFarReplayTierProbeRoot, out _))
                return ScenarioResult.Fail("initially culled roots were materialized locally");
            if (!_localObservedInputProbe.gameObject.activeInHierarchy)
                return ScenarioResult.Fail("input-driven root was locally despawned or deactivated");
            if (!LocalOwnerIsExempt())
                return ScenarioResult.Fail("locally owned anchor did not retain its configured policy");
            if (_localNearProbe.GetComponent<Rigidbody>().isKinematic)
                return ScenarioResult.Fail("visible raw rigidbody started frozen");

            await UniTaskUtils.WaitWithTimeout(
                () => _localNearProbe.localSimulationCalls >= 10 &&
                      _localNearProbe.localViewUpdates >= 3 &&
                      _localNearRateProbe.localSimulationCalls >= 10 &&
                      _localNearRateProbe.localViewUpdates >= 3 &&
                      _localNearReplayTierProbe.transitionAttempted,
                Timeout,
                ctx.cancellationToken);
            if (!_localNearReplayTierProbe.transitionDuringReplay ||
                !_localNearReplayTierProbe.tierPolicyApplied ||
                !_localNearReplayTierProbe.replayPhysicsApplied ||
                !_localNearReplayTierProbe.tierRestored ||
                !_localNearReplayTierProbe.configuredPolicyRestored ||
                !_localNearReplayTierProbe.configuredPhysicsRestored)
            {
                return ScenarioResult.Fail(
                    $"mid-replay tier flip failed: {_localNearReplayTierProbe.TransitionDigest()}");
            }
            var rateFailure = await WaitForRateConvergence(pm, _localNearRateProbe, 1, ctx);
            if (rateFailure != null)
                return ScenarioResult.Fail(rateFailure);
            if (!HasLocalPolicies(PredictionPolicy.FullPrediction, PredictionPolicy.FullPrediction))
                return ScenarioResult.Fail($"tier 0 did not preserve configured policies: {DescribeClient(pm)}");
            uint initialInputs = _localObservedInputProbe.currentState.appliedInputs;
            await ScenarioBarrier.Wait(ctx, InitialBarrier, Timeout);

            stage = "tier 0 hysteresis";
            await WaitClientTicks(pm, 24, ctx);
            if (!HasLocalNeighborhoodTier(pm, 0) ||
                !HasLocalPolicies(PredictionPolicy.FullPrediction, PredictionPolicy.FullPrediction) ||
                _localObservedInputProbe.currentState.appliedInputs <= initialInputs)
                return ScenarioResult.Fail($"tier 0 hysteresis or input continuity failed: {DescribeClient(pm)}");
            uint tier0Inputs = _localObservedInputProbe.currentState.appliedInputs;
            await ScenarioBarrier.Wait(ctx, HoldTier0Barrier, Timeout);

            stage = "tier 1";
            await UniTaskUtils.WaitWithTimeout(
                () => HasLocalNeighborhoodTier(pm, 1),
                Timeout,
                ctx.cancellationToken);
            rateFailure = await WaitForRateConvergence(pm, _localNearRateProbe, 2, ctx);
            if (rateFailure != null)
                return ScenarioResult.Fail(rateFailure);
            await UniTaskUtils.WaitWithTimeout(
                () => _localObservedInputProbe.currentState.appliedInputs >= tier0Inputs + 10,
                Timeout,
                ctx.cancellationToken);
            if (!HasLocalPolicies(PredictionPolicy.FullPrediction, PredictionPolicy.SoftCorrection))
                return ScenarioResult.Fail($"unsupported tier 1 SoftCorrection was not normalized: {DescribeClient(pm)}");
            if (!LocalOwnerIsExempt())
                return ScenarioResult.Fail("tier 1 overrode the locally owned anchor");
            await UniTaskUtils.WaitWithTimeout(
                () => _localNearRateProbe.currentState.checkpoint == 1 &&
                      !_localNearRateProbe.currentState.active,
                Timeout,
                ctx.cancellationToken);
            InterestRateReportGate.Report(1, _localNearRateProbe);
            uint tier1Inputs = _localObservedInputProbe.currentState.appliedInputs;
            await ScenarioBarrier.Wait(ctx, Tier1Barrier, Timeout);

            stage = "tier 1 hysteresis";
            await WaitClientTicks(pm, 24, ctx);
            if (!HasLocalNeighborhoodTier(pm, 1) ||
                _localObservedInputProbe.currentState.appliedInputs <= tier1Inputs)
                return ScenarioResult.Fail($"tier 1 hysteresis or input continuity failed: {DescribeClient(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldTier1Barrier, Timeout);

            stage = "tier 2";
            await UniTaskUtils.WaitWithTimeout(
                () => HasLocalNeighborhoodTier(pm, 2),
                Timeout,
                ctx.cancellationToken);
            rateFailure = await WaitForRateConvergence(pm, _localNearRateProbe, 4, ctx);
            if (rateFailure != null)
                return ScenarioResult.Fail(rateFailure);
            await UniTaskUtils.WaitWithTimeout(
                () => _localObservedInputProbe.currentState.appliedInputs >= tier1Inputs + 10,
                Timeout,
                ctx.cancellationToken);
            if (!HasLocalPolicies(PredictionPolicy.ServerRelay, PredictionPolicy.ServerRelay))
                return ScenarioResult.Fail($"tier 2 did not apply relay policy: {DescribeClient(pm)}");
            if (!_localNearReplayTierProbe.rb.isKinematic)
                return ScenarioResult.Fail("tier 2 relay policy did not apply its rigidbody side effect");
            if (!LocalOwnerIsExempt())
                return ScenarioResult.Fail("tier 2 overrode the locally owned anchor");
            await UniTaskUtils.WaitWithTimeout(
                () => _localNearRateProbe.currentState.checkpoint == 2 &&
                      !_localNearRateProbe.currentState.active,
                Timeout,
                ctx.cancellationToken);
            InterestRateReportGate.Report(2, _localNearRateProbe);
            uint tier2Inputs = _localObservedInputProbe.currentState.appliedInputs;
            await ScenarioBarrier.Wait(ctx, Tier2Barrier, Timeout);

            stage = "tier 2 hysteresis";
            await WaitClientTicks(pm, 24, ctx);
            if (!HasLocalNeighborhoodTier(pm, 2) ||
                !HasLocalPolicies(PredictionPolicy.ServerRelay, PredictionPolicy.ServerRelay) ||
                _localObservedInputProbe.currentState.appliedInputs <= tier2Inputs)
                return ScenarioResult.Fail($"tier 2 hysteresis or input continuity failed: {DescribeClient(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldTier2Barrier, Timeout);

            stage = "owner exemption";
            await UniTaskUtils.WaitWithTimeout(
                () => _localNearRateProbe.owner == pm.localPlayer &&
                      GetLocalTier(pm, _localNearRateProbe) == 0 &&
                      _localNearRateProbe.predictionPolicy == PredictionPolicy.FullPrediction,
                Timeout,
                ctx.cancellationToken);
            if (!_localNearRateProbe.isRelevant ||
                _localNearRateProbe.configuredPredictionPolicy != PredictionPolicy.FullPrediction)
                return ScenarioResult.Fail($"owned rate root did not preserve configured policy: {DescribeClient(pm)}");
            await ScenarioBarrier.Wait(ctx, OwnerBarrier, Timeout);

            stage = "owner release";
            await UniTaskUtils.WaitWithTimeout(
                () => !_localNearRateProbe.owner.HasValue &&
                      GetLocalTier(pm, _localNearRateProbe) == 2 &&
                      _localNearRateProbe.predictionPolicy == PredictionPolicy.ServerRelay,
                Timeout,
                ctx.cancellationToken);
            rateFailure = await WaitForRateConvergence(pm, _localNearRateProbe, 4, ctx);
            if (rateFailure != null)
                return ScenarioResult.Fail(rateFailure);
            await ScenarioBarrier.Wait(ctx, OwnerReleaseBarrier, Timeout);

            stage = "cull";
            await UniTaskUtils.WaitWithTimeout(
                () => IsLocallyCulled(pm, _localNearProbeRoot) &&
                      IsLocallyCulled(pm, _localNearRateProbeRoot) &&
                      IsLocallyCulled(pm, _localObservedInputProbeRoot),
                Timeout,
                ctx.cancellationToken);
            await UniTask.NextFrame(ctx.cancellationToken);
            await UniTask.NextFrame(ctx.cancellationToken);

            uint frozenSimulations = _localNearProbe.currentState.simulations;
            int frozenCalls = _localNearProbe.localSimulationCalls;
            int frozenViews = _localNearProbe.localViewUpdates;
            var frozenRateState = _localNearRateProbe.currentState;
            int frozenRateCalls = _localNearRateProbe.localSimulationCalls;
            int frozenRateViews = _localNearRateProbe.localViewUpdates;
            var frozenInputState = _localObservedInputProbe.currentState;
            int frozenInputCalls = _localObservedInputProbe.localSimulationCalls;
            ulong dormantStart = pm.localTick;
            await UniTaskUtils.WaitWithTimeout(
                () => pm.localTick >= dormantStart + 30,
                Timeout,
                ctx.cancellationToken);

            if (_localNearProbe.currentState.simulations != frozenSimulations ||
                _localNearProbe.localSimulationCalls != frozenCalls ||
                _localNearProbe.localViewUpdates != frozenViews)
                return ScenarioResult.Fail($"culled probe kept simulating or updating its view: {DescribeClient(pm)}");
            if (!_localNearRateProbe.StateEquals(frozenRateState) ||
                _localNearRateProbe.localSimulationCalls != frozenRateCalls ||
                _localNearRateProbe.localViewUpdates != frozenRateViews)
                return ScenarioResult.Fail($"culled rate probe kept simulating or updating its view: {DescribeClient(pm)}");
            if (!_localObservedInputProbe.StateEquals(frozenInputState) ||
                _localObservedInputProbe.localSimulationCalls != frozenInputCalls)
                return ScenarioResult.Fail($"culled input probe kept simulating: {DescribeClient(pm)}");
            if (!_localNearProbe.GetComponent<Rigidbody>().isKinematic)
                return ScenarioResult.Fail("culled raw rigidbody remained dynamic");
            if (!HasRetainedHierarchy(pm) ||
                pm.hierarchy.TryGetGameObject(_localNearProbeRoot, out _) ||
                pm.hierarchy.TryGetGameObject(_localNearRateProbeRoot, out _) ||
                pm.hierarchy.TryGetGameObject(_localObservedInputProbeRoot, out _))
                return ScenarioResult.Fail("culled roots did not retain records without live instances");
            if (_localNearRateProbe.predictionPolicy != PredictionPolicy.ServerRelay ||
                _localNearRateProbe.configuredPredictionPolicy != PredictionPolicy.FullPrediction)
                return ScenarioResult.Fail("culled dormancy did not remain stronger than the tier policy override");
            if (!_localNearReplayTierProbe.rb.isKinematic)
                return ScenarioResult.Fail("culled replay-tier rigidbody remained dynamic");
            if (!LocalOwnerIsExempt())
                return ScenarioResult.Fail("culling overrode the locally owned anchor");

            await ScenarioBarrier.Wait(ctx, CulledBarrier, Timeout);

            stage = "culled hysteresis";
            await WaitClientTicks(pm, 24, ctx);
            if (!IsLocallyCulled(pm, _localNearProbeRoot) ||
                !IsLocallyCulled(pm, _localNearRateProbeRoot) ||
                !IsLocallyCulled(pm, _localObservedInputProbeRoot))
                return ScenarioResult.Fail($"culled hysteresis failed: {DescribeClient(pm)}");
            if (!HasRetainedHierarchy(pm))
                return ScenarioResult.Fail("culled hierarchy records did not survive pool expiry");
            await ScenarioBarrier.Wait(ctx, HoldCulledBarrier, Timeout);

            stage = "tier 2 absolute reentry";
            await UniTaskUtils.WaitWithTimeout(
                () => RefreshLocalNearInstances(pm) &&
                      _localNearProbe.isRelevant &&
                      _localNearRateProbe.isRelevant &&
                      _localNearProbe.currentState.simulations >= frozenSimulations + 20 &&
                      _localNearProbe.stateIsValid,
                Timeout,
                ctx.cancellationToken);
            await UniTaskUtils.WaitWithTimeout(
                () => _localObservedInputProbe.isRelevant &&
                      _localObservedInputProbe.currentState.appliedInputs >= frozenInputState.appliedInputs + 10,
                Timeout,
                ctx.cancellationToken);
            await UniTaskUtils.WaitWithTimeout(
                () => _localNearProbe.localSimulationCalls > 0 &&
                      _localNearProbe.localViewUpdates > 0 &&
                      _localNearRateProbe.localSimulationCalls > 0 &&
                      _localNearRateProbe.localViewUpdates > 0,
                Timeout,
                ctx.cancellationToken);
            rateFailure = await WaitForRateConvergence(pm, _localNearRateProbe, 4, ctx);
            if (rateFailure != null)
                return ScenarioResult.Fail(rateFailure);

            if (!IsLocallyCulled(pm, _localFarProbeRoot) || !IsLocallyCulled(pm, _localFarRateProbeRoot))
                return ScenarioResult.Fail("reentry made the other client's far root relevant");
            if (!HasRetainedHierarchy(pm))
                return ScenarioResult.Fail("hierarchy diverged across cull and reentry");
            if (_localNearProbe.GetComponent<Rigidbody>().isKinematic)
                return ScenarioResult.Fail("reentered raw rigidbody remained frozen");
            if (!HasLocalNeighborhoodTier(pm, 2) ||
                !HasLocalPolicies(PredictionPolicy.ServerRelay, PredictionPolicy.ServerRelay))
                return ScenarioResult.Fail($"tier 2 reentry did not restore relay policy: {DescribeClient(pm)}");

            await ScenarioBarrier.Wait(ctx, ReentryTier2Barrier, Timeout);

            stage = "approach";
            await UniTaskUtils.WaitWithTimeout(
                () => HasLocalNeighborhoodTier(pm, 0) &&
                      HasLocalPolicies(PredictionPolicy.FullPrediction, PredictionPolicy.FullPrediction),
                Timeout,
                ctx.cancellationToken);
            rateFailure = await WaitForRateConvergence(pm, _localNearRateProbe, 1, ctx);
            if (rateFailure != null)
                return ScenarioResult.Fail(rateFailure);
            if (_localNearRateProbe.configuredPredictionPolicy != PredictionPolicy.FullPrediction)
                return ScenarioResult.Fail("approach did not restore the exact configured policy");
            if (_localNearReplayTierProbe.rb.isKinematic)
                return ScenarioResult.Fail("approach restored policy but not authoritative rigidbody mode");
            await ScenarioBarrier.Wait(ctx, ApproachBarrier, Timeout);

            stage = "input finalization";
            await UniTaskUtils.WaitWithTimeout(
                () => _localObservedInputProbe.currentState.finalized,
                Timeout,
                ctx.cancellationToken);
            if (_localObservedInputProbe.currentState.travel.rawValue == 0 ||
                _localObservedInputProbe.currentState.appliedInputs <= frozenInputState.appliedInputs)
                return ScenarioResult.Fail("input-driven probe did not move after absolute reentry");

            InterestInputReportGate.Report(_localObservedInputProbe);
            await ScenarioBarrier.Wait(ctx, FinalInputBarrier, Timeout);
            return await DigestExchange.Compare(
                ctx,
                DigestChannel,
                BuildDigest(pm, _localNearProbe, _localNearRateProbe),
                Timeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"client timed out during {stage}: {DescribeClient(pm)}");
        }
    }

    private bool CopyPlayers(PredictionManager pm)
    {
        var source = pm.players.players;
        if (source.Count != 2)
            return false;

        _players.Clear();
        _players.Add(source[0]);
        _players.Add(source[1]);
        _players.Sort((a, b) => a.id.value.CompareTo(b.id.value));
        return true;
    }

    private bool CreateServerRoots(PredictionManager pm)
    {
        var anchorA = pm.hierarchy.Create(_anchorPrefab, new Vector3(-RootOffset, 0f, 0f), Quaternion.identity, _players[0]);
        var anchorB = pm.hierarchy.Create(_anchorPrefab, new Vector3(RootOffset, 0f, 0f), Quaternion.identity, _players[1]);
        var probeA = pm.hierarchy.Create(_probePrefab, new Vector3(-RootOffset, 0f, 0f), Quaternion.identity);
        var probeB = pm.hierarchy.Create(_probePrefab, new Vector3(RootOffset, 0f, 0f), Quaternion.identity);
        var rateProbeA = pm.hierarchy.Create(_rateProbePrefab, new Vector3(-RootOffset, 0f, 0f), Quaternion.identity);
        var rateProbeB = pm.hierarchy.Create(_rateProbePrefab, new Vector3(RootOffset, 0f, 0f), Quaternion.identity);
        var replayTierProbeA = pm.hierarchy.Create(
            _replayTierProbePrefab,
            new Vector3(-RootOffset, 2f, 0f),
            Quaternion.identity);
        var replayTierProbeB = pm.hierarchy.Create(
            _replayTierProbePrefab,
            new Vector3(RootOffset, 2f, 0f),
            Quaternion.identity);
        var inputProbeA = pm.hierarchy.Create(_inputProbePrefab, new Vector3(RootOffset, 0f, 0f), Quaternion.identity, _players[0]);
        var inputProbeB = pm.hierarchy.Create(_inputProbePrefab, new Vector3(-RootOffset, 0f, 0f), Quaternion.identity, _players[1]);

        if (!anchorA.HasValue || !anchorB.HasValue || !probeA.HasValue || !probeB.HasValue ||
            !rateProbeA.HasValue || !rateProbeB.HasValue ||
            !replayTierProbeA.HasValue || !replayTierProbeB.HasValue ||
            !inputProbeA.HasValue || !inputProbeB.HasValue)
            return false;

        _serverAnchorA = pm.hierarchy.GetComponent<InterestAnchorMarker>(anchorA);
        _serverAnchorB = pm.hierarchy.GetComponent<InterestAnchorMarker>(anchorB);
        _serverProbeA = pm.hierarchy.GetComponent<InterestStateProbe>(probeA);
        _serverProbeB = pm.hierarchy.GetComponent<InterestStateProbe>(probeB);
        _serverRateProbeA = pm.hierarchy.GetComponent<InterestRateProbe>(rateProbeA);
        _serverRateProbeB = pm.hierarchy.GetComponent<InterestRateProbe>(rateProbeB);
        _serverReplayTierProbeA = pm.hierarchy.GetComponent<InterestReplayTierProbe>(replayTierProbeA);
        _serverReplayTierProbeB = pm.hierarchy.GetComponent<InterestReplayTierProbe>(replayTierProbeB);
        _serverInputProbeA = pm.hierarchy.GetComponent<InterestInputProbe>(inputProbeA);
        _serverInputProbeB = pm.hierarchy.GetComponent<InterestInputProbe>(inputProbeB);
        return _serverAnchorA && _serverAnchorB && _serverProbeA && _serverProbeB &&
               _serverRateProbeA && _serverRateProbeB &&
               _serverReplayTierProbeA && _serverReplayTierProbeB &&
               _serverInputProbeA && _serverInputProbeB;
    }

    private bool ResolveLocalLayout(PredictionManager pm)
    {
        if (!pm.localPlayer.HasValue || !pm.hierarchy || InterestAnchorMarker.instances.Count == 0 ||
            PredictionTestUtils.CountInstances(pm, _anchorPrefabId) != 2 ||
            PredictionTestUtils.CountInstances(pm, _probePrefabId) != 2 ||
            PredictionTestUtils.CountInstances(pm, _rateProbePrefabId) != 2 ||
            PredictionTestUtils.CountInstances(pm, _replayTierProbePrefabId) != 2 ||
            PredictionTestUtils.CountInstances(pm, _inputProbePrefabId) != 2)
            return false;

        _localAnchor = null;
        for (var i = 0; i < InterestAnchorMarker.instances.Count; i++)
        {
            var anchor = InterestAnchorMarker.instances[i];
            if (anchor && anchor.predictedTransform.owner == pm.localPlayer)
            {
                _localAnchor = anchor;
                break;
            }
        }

        if (!_localAnchor || Mathf.Abs(_localAnchor.position.x) < RootOffset * 0.5f)
            return false;

        if (!TryResolveRootPair(pm, _probePrefabId, out _localNearProbeRoot, out _localFarProbeRoot) ||
            !TryResolveRootPair(pm, _rateProbePrefabId, out _localNearRateProbeRoot, out _localFarRateProbeRoot) ||
            !TryResolveRootPair(pm, _replayTierProbePrefabId, out _localNearReplayTierProbeRoot,
                out _localFarReplayTierProbeRoot) ||
            !TryResolveRootPair(pm, _inputProbePrefabId, out _localObservedInputProbeRoot, out _))
            return false;

        if (!RefreshLocalNearInstances(pm))
            return false;

        return _localObservedInputProbe.owner != pm.localPlayer;
    }

    private bool TryResolveRootPair(PredictionManager pm, int prefabId,
        out PredictedObjectID nearRoot, out PredictedObjectID farRoot)
    {
        nearRoot = default;
        farRoot = default;
        PredictedObjectID firstRoot = default;
        PredictedObjectID secondRoot = default;
        Vector3 firstPosition = default;
        Vector3 secondPosition = default;
        int found = 0;
        var records = pm.hierarchy.currentState.spawnedPrefabs;

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (!record.isRootRecord || record.prefabId.value != prefabId)
                continue;

            if (found == 0)
            {
                firstRoot = record.rootId;
                firstPosition = record.spawnPosition;
            }
            else if (found == 1)
            {
                secondRoot = record.rootId;
                secondPosition = record.spawnPosition;
            }
            else
            {
                return false;
            }

            found++;
        }

        if (found != 2)
            return false;

        float firstDistance = Mathf.Abs(firstPosition.x - _localAnchor.position.x);
        float secondDistance = Mathf.Abs(secondPosition.x - _localAnchor.position.x);
        nearRoot = firstDistance < secondDistance ? firstRoot : secondRoot;
        farRoot = nearRoot.Equals(firstRoot) ? secondRoot : firstRoot;
        return true;
    }

    private bool RefreshLocalNearInstances(PredictionManager pm)
    {
        _localNearProbe = pm.hierarchy.GetComponent<InterestStateProbe>(_localNearProbeRoot);
        _localNearRateProbe = pm.hierarchy.GetComponent<InterestRateProbe>(_localNearRateProbeRoot);
        _localNearReplayTierProbe = pm.hierarchy.GetComponent<InterestReplayTierProbe>(_localNearReplayTierProbeRoot);
        _localObservedInputProbe = pm.hierarchy.GetComponent<InterestInputProbe>(_localObservedInputProbeRoot);
        return _localNearProbe && _localNearRateProbe && _localNearReplayTierProbe && _localObservedInputProbe;
    }

    private void SetAnchorDistance(float distance)
    {
        _serverAnchorA.SetServerPosition(new Vector3(-RootOffset - distance, 0f, 0f));
        _serverAnchorB.SetServerPosition(new Vector3(RootOffset + distance, 0f, 0f));
    }

    private bool HasNeighborhoodTiers(PredictionManager pm, byte tier)
    {
        return HasTier(pm, _players[0], _serverProbeA, tier) &&
               HasTier(pm, _players[0], _serverProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeB, tier) &&
               HasTier(pm, _players[0], _serverRateProbeA, tier) &&
               HasTier(pm, _players[0], _serverRateProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverRateProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverRateProbeB, tier) &&
               HasTier(pm, _players[0], _serverReplayTierProbeA, tier) &&
               HasTier(pm, _players[0], _serverReplayTierProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverReplayTierProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverReplayTierProbeB, tier) &&
               HasTier(pm, _players[0], _serverInputProbeA, 0) &&
               HasTier(pm, _players[0], _serverInputProbeB, tier) &&
               HasTier(pm, _players[1], _serverInputProbeA, tier) &&
               HasTier(pm, _players[1], _serverInputProbeB, 0);
    }

    private bool HasOwnedRateExemptionTiers(PredictionManager pm)
    {
        return HasTier(pm, _players[0], _serverRateProbeA, 0) &&
               HasTier(pm, _players[0], _serverRateProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverRateProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverRateProbeB, 0) &&
               HasTier(pm, _players[0], _serverProbeA, 2) &&
               HasTier(pm, _players[1], _serverProbeB, 2);
    }

    private bool HasCulledNeighborhoodTiers(PredictionManager pm)
    {
        return HasTier(pm, _players[0], _serverProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverRateProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverRateProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverRateProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverRateProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverReplayTierProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverReplayTierProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverReplayTierProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverReplayTierProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverInputProbeA, 0) &&
               HasTier(pm, _players[0], _serverInputProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverInputProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverInputProbeB, 0);
    }

    private static bool HasTier(PredictionManager pm, PlayerID player, PredictedIdentity probe, byte expected)
    {
        return probe && pm.interest.TryGetTier(player, probe.rootObjectId, out var tier) && tier == expected;
    }

    private static bool IsLocallyCulled(PredictionManager pm, PredictedObjectID root)
    {
        return pm.interest.TryGetLocalRelevance(root, out var tier) &&
               tier == NetworkLODProfile.CulledTier &&
               !pm.hierarchy.TryGetGameObject(root, out _);
    }

    private bool HasLocalNeighborhoodTier(PredictionManager pm, byte tier)
    {
        return _localNearProbe && _localNearProbe.isRelevant && GetLocalTier(pm, _localNearProbe) == tier &&
               _localNearRateProbe && _localNearRateProbe.isRelevant && GetLocalTier(pm, _localNearRateProbe) == tier &&
               _localNearReplayTierProbe && _localNearReplayTierProbe.isRelevant &&
               GetLocalTier(pm, _localNearReplayTierProbe) == tier &&
               _localObservedInputProbe && _localObservedInputProbe.isRelevant &&
               GetLocalTier(pm, _localObservedInputProbe) == tier &&
               IsLocallyCulled(pm, _localFarProbeRoot) &&
               IsLocallyCulled(pm, _localFarRateProbeRoot) &&
               IsLocallyCulled(pm, _localFarReplayTierProbeRoot);
    }

    private bool HasLocalPolicies(PredictionPolicy unsupportedPolicy, PredictionPolicy replayPolicy)
    {
        return _localNearProbe.predictionPolicy == unsupportedPolicy &&
               _localNearRateProbe.predictionPolicy == unsupportedPolicy &&
               _localObservedInputProbe.predictionPolicy == unsupportedPolicy &&
               _localNearReplayTierProbe.predictionPolicy == replayPolicy;
    }

    private bool LocalOwnerIsExempt()
    {
        return _localAnchor && _localAnchor.isRelevant &&
               _localAnchor.predictionPolicy == PredictionPolicy.FullPrediction &&
               _localAnchor.predictedTransform.isRelevant &&
               _localAnchor.predictedTransform.predictionPolicy == PredictionPolicy.FullPrediction;
    }

    private bool HasRetainedHierarchy(PredictionManager pm)
    {
        return PredictionTestUtils.CountInstances(pm, _anchorPrefabId) == 2 &&
               PredictionTestUtils.CountInstances(pm, _probePrefabId) == 2 &&
               PredictionTestUtils.CountInstances(pm, _rateProbePrefabId) == 2 &&
               PredictionTestUtils.CountInstances(pm, _replayTierProbePrefabId) == 2 &&
               PredictionTestUtils.CountInstances(pm, _inputProbePrefabId) == 2 &&
               HasRetainedRoot(pm, _localNearProbeRoot) &&
               HasRetainedRoot(pm, _localFarProbeRoot) &&
               HasRetainedRoot(pm, _localNearRateProbeRoot) &&
               HasRetainedRoot(pm, _localFarRateProbeRoot) &&
               HasRetainedRoot(pm, _localNearReplayTierProbeRoot) &&
               HasRetainedRoot(pm, _localFarReplayTierProbeRoot) &&
               HasRetainedRoot(pm, _localObservedInputProbeRoot);
    }

    private static bool HasRetainedRoot(PredictionManager pm, PredictedObjectID root)
    {
        return pm.hierarchy.TryGetRootId(root, out var retainedRoot) && retainedRoot.Equals(root);
    }

    private static async UniTask WaitServerTicks(PredictionManager pm, ulong ticks, ScenarioContext ctx)
    {
        ulong start = pm.time.tick;
        await UniTaskUtils.WaitWithTimeout(
            () => pm.time.tick >= start + ticks,
            Timeout,
            ctx.cancellationToken);
    }

    private static async UniTask WaitClientTicks(PredictionManager pm, ulong ticks, ScenarioContext ctx)
    {
        ulong start = pm.localTick;
        await UniTaskUtils.WaitWithTimeout(
            () => pm.localTick >= start + ticks,
            Timeout,
            ctx.cancellationToken);
    }

    private static async UniTask<string> WaitForRateConvergence(
        PredictionManager pm,
        InterestRateProbe probe,
        int interval,
        ScenarioContext ctx)
    {
        ulong start = probe.lastVerifiedTick ?? 0;
        await UniTaskUtils.WaitWithTimeout(
            () => probe.lastVerifiedTick.HasValue &&
                  probe.lastVerifiedTick.Value >= start + (ulong)(interval * 3) &&
                  ((uint)probe.lastVerifiedTick.Value + probe.rootObjectId.instanceId.value) % interval == 0 &&
                  probe.stateIsValid &&
                  probe.verifiedStateIsValid,
            Timeout,
            ctx.cancellationToken);

        if (GetLocalTier(pm, probe) == NetworkLODProfile.CulledTier)
            return "rate probe became culled while waiting for convergence";

        return null;
    }

    private async UniTask<RateWriteSample> SampleRateWrites(
        ScenarioContext ctx,
        PredictionManager pm,
        int interval)
    {
        using var sampler = new RateWriteSampler(
            pm,
            _serverRateProbeA,
            _serverRateProbeB,
            RateSampleTicks);
        await UniTaskUtils.WaitWithTimeout(
            () => sampler.complete,
            Timeout,
            ctx.cancellationToken);
        return sampler.Validate(interval);
    }

    private async UniTask<Dictionary<PlayerID, double>> SampleFrameBytes(ScenarioContext ctx)
    {
        using var sampler = new RecipientFrameSampler(_players);
        await UniTaskUtils.WaitWithTimeout(
            () => sampler.HasSamples(FrameSampleCount),
            Timeout,
            ctx.cancellationToken);
        return sampler.GetAverageBytes();
    }

    private string ValidateFrameByteReduction()
    {
        for (var i = 0; i < _players.Count; i++)
        {
            var player = _players[i];
            if (!_visibleFrameBytes.TryGetValue(player, out var visible) ||
                !_culledFrameBytes.TryGetValue(player, out var culled))
                return $"missing frame-byte samples for player {player.id.value}";
            if (culled >= visible * 0.9)
                return $"culling did not reduce player {player.id.value} frame bytes: visible={visible:F2} culled={culled:F2}";
        }

        return null;
    }

    private string ValidateRateScaling()
    {
        int expectedTier0 = RateSampleTicks * 2;
        int expectedTier1 = RateSampleTicks;
        int expectedTier2 = RateSampleTicks / 2;
        if (_tier0Writes.totalWrites != expectedTier0 ||
            _tier1Writes.totalWrites != expectedTier1 ||
            _tier2Writes.totalWrites != expectedTier2)
        {
            return $"rate scaling mismatch: tier0={_tier0Writes.totalWrites}/{expectedTier0} " +
                   $"tier1={_tier1Writes.totalWrites}/{expectedTier1} " +
                   $"tier2={_tier2Writes.totalWrites}/{expectedTier2}";
        }

        return null;
    }

    private string FrameByteSummary()
    {
        if (_players.Count != 2 || _visibleFrameBytes == null || _culledFrameBytes == null)
            return "bytes=unavailable";

        var p0 = _players[0];
        var p1 = _players[1];
        return $"bytes=p0:{_visibleFrameBytes[p0]:F2}->{_culledFrameBytes[p0]:F2}," +
               $"p1:{_visibleFrameBytes[p1]:F2}->{_culledFrameBytes[p1]:F2};" +
               $"writes={_tier0Writes.totalWrites}->{_tier1Writes.totalWrites}->{_tier2Writes.totalWrites}";
    }

    private string DescribeServerTiers(PredictionManager pm)
    {
        if (_players.Count != 2 || !_serverProbeA || !_serverProbeB)
            return $"players={_players.Count} probes={(_serverProbeA ? 1 : 0) + (_serverProbeB ? 1 : 0)}";

        return $"p0=({GetTier(pm, _players[0], _serverProbeA)},{GetTier(pm, _players[0], _serverProbeB)};" +
               $"{GetTier(pm, _players[0], _serverRateProbeA)},{GetTier(pm, _players[0], _serverRateProbeB)};" +
               $"{GetTier(pm, _players[0], _serverInputProbeA)},{GetTier(pm, _players[0], _serverInputProbeB)}) " +
               $"p1=({GetTier(pm, _players[1], _serverProbeA)},{GetTier(pm, _players[1], _serverProbeB)};" +
               $"{GetTier(pm, _players[1], _serverRateProbeA)},{GetTier(pm, _players[1], _serverRateProbeB)};" +
               $"{GetTier(pm, _players[1], _serverInputProbeA)},{GetTier(pm, _players[1], _serverInputProbeB)})";
    }

    private string DescribeClient(PredictionManager pm)
    {
        if (!_localNearProbe || !_localNearRateProbe || !_localObservedInputProbe)
            return $"anchors={InterestAnchorMarker.instances.Count} probes={InterestStateProbe.instances.Count} " +
                   $"rateProbes={InterestRateProbe.instances.Count} inputProbes={InterestInputProbe.instances.Count} " +
                   $"nearTier={GetLocalTier(pm, _localNearProbeRoot)} farTier={GetLocalTier(pm, _localFarProbeRoot)} " +
                   $"nearLive={pm.hierarchy.TryGetGameObject(_localNearProbeRoot, out _)} " +
                   $"farLive={pm.hierarchy.TryGetGameObject(_localFarProbeRoot, out _)}";

        return $"nearRelevant={_localNearProbe.isRelevant} farLive={pm.hierarchy.TryGetGameObject(_localFarProbeRoot, out _)} " +
               $"nearTier={GetLocalTier(pm, _localNearProbeRoot)} farTier={GetLocalTier(pm, _localFarProbeRoot)} " +
               $"state={_localNearProbe.currentState.simulations} calls={_localNearProbe.localSimulationCalls} " +
               $"views={_localNearProbe.localViewUpdates} rateTier={GetLocalTier(pm, _localNearRateProbe)} " +
               $"ratePolicy={_localNearRateProbe.predictionPolicy} rateState={_localNearRateProbe.StateDigest()} " +
               $"replayRelevant={_localNearReplayTierProbe.isRelevant} " +
               $"replayTier={GetLocalTier(pm, _localNearReplayTierProbe)} " +
               $"replayPolicy={_localNearReplayTierProbe.predictionPolicy} " +
               $"inputRelevant={_localObservedInputProbe.isRelevant} " +
               $"inputTier={GetLocalTier(pm, _localObservedInputProbe)} " +
               $"inputState={_localObservedInputProbe.StateDigest()}";
    }

    private static int GetTier(PredictionManager pm, PlayerID player, PredictedIdentity probe)
    {
        return pm.interest.TryGetTier(player, probe.rootObjectId, out var tier) ? tier : -1;
    }

    private static int GetLocalTier(PredictionManager pm, PredictedIdentity probe)
    {
        return probe ? GetLocalTier(pm, probe.rootObjectId) : -1;
    }

    private static int GetLocalTier(PredictionManager pm, PredictedObjectID root)
    {
        return pm.interest.TryGetLocalRelevance(root, out var tier) ? tier : 0;
    }

    private string BuildDigest(PredictionManager pm, InterestStateProbe probe, InterestRateProbe rateProbe)
    {
        return $"anchors={PredictionTestUtils.CountInstances(pm, _anchorPrefabId)};" +
               $"probes={PredictionTestUtils.CountInstances(pm, _probePrefabId)};" +
               $"rateProbes={PredictionTestUtils.CountInstances(pm, _rateProbePrefabId)};" +
               $"replayProbes={PredictionTestUtils.CountInstances(pm, _replayTierProbePrefabId)};" +
               $"inputProbes={PredictionTestUtils.CountInstances(pm, _inputProbePrefabId)};" +
               $"first={probe.currentState.firstTick};valid={probe.stateIsValid};" +
               $"rateFirst={rateProbe.currentState.firstTick};rateValid={rateProbe.stateIsValid};" +
               $"rateCheckpoint={rateProbe.currentState.checkpoint};" +
               $"rateCheckpointLast={rateProbe.currentState.checkpointLastTick};" +
               $"rateCheckpointSequence={rateProbe.currentState.checkpointSequence}";
    }

    private readonly struct RateWriteSample
    {
        public readonly int totalWrites;
        public readonly string failure;

        public RateWriteSample(int totalWrites, string failure)
        {
            this.totalWrites = totalWrites;
            this.failure = failure;
        }
    }

    private sealed class RateWriteSampler : IDisposable
    {
        private readonly PredictionManager _manager;
        private readonly InterestRateProbe _first;
        private readonly InterestRateProbe _second;
        private readonly int _sampleTicks;
        private readonly List<ulong> _ticks;
        private readonly Dictionary<(uint root, ulong tick), int> _writes = new();

        public RateWriteSampler(
            PredictionManager manager,
            InterestRateProbe first,
            InterestRateProbe second,
            int sampleTicks)
        {
            _manager = manager;
            _first = first;
            _second = second;
            _sampleTicks = sampleTicks;
            _ticks = new List<ulong>(sampleTicks);
            TickBandwidthProfiler.onTickEnded += OnTickEnded;
        }

        public bool complete => _ticks.Count >= _sampleTicks;

        public void Dispose()
        {
            TickBandwidthProfiler.onTickEnded -= OnTickEnded;
        }

        public RateWriteSample Validate(int interval)
        {
            int total = 0;
            for (var i = 0; i < _ticks.Count; i++)
            {
                ulong tick = _ticks[i];
                var failure = ValidateProbe(_first, tick, interval, ref total);
                if (failure != null)
                    return new RateWriteSample(total, failure);
                failure = ValidateProbe(_second, tick, interval, ref total);
                if (failure != null)
                    return new RateWriteSample(total, failure);
            }

            return new RateWriteSample(total, null);
        }

        private string ValidateProbe(
            InterestRateProbe probe,
            ulong tick,
            int interval,
            ref int total)
        {
            uint root = probe.rootObjectId.instanceId.value;
            int actual = _writes.GetValueOrDefault((root, tick));
            int expected = ((uint)tick + root) % interval == 0 ? 1 : 0;
            total += actual;
            return actual == expected
                ? null
                : $"tier interval {interval} root {root} tick {tick} wrote {actual}, expected {expected}";
        }

        private void OnTickEnded()
        {
            if (_ticks.Count >= _sampleTicks)
                return;

            ulong tick = _manager.localTick == 0 ? 0 : _manager.localTick - 1;
            _ticks.Add(tick);

            var writes = TickBandwidthProfiler.wroteStates;
            for (var i = 0; i < writes.Count; i++)
            {
                var reference = writes[i].reference;
                uint root;
                if (ReferenceEquals(reference, _first))
                    root = _first.rootObjectId.instanceId.value;
                else if (ReferenceEquals(reference, _second))
                    root = _second.rootObjectId.instanceId.value;
                else
                    continue;

                var key = (root, tick);
                _writes[key] = _writes.GetValueOrDefault(key) + 1;
            }
        }
    }

    private sealed class RecipientFrameSampler : IDisposable
    {
        private readonly List<PlayerID> _players;
        private readonly Dictionary<PlayerID, long> _bits = new();
        private readonly Dictionary<PlayerID, int> _frames = new();

        public RecipientFrameSampler(List<PlayerID> players)
        {
            _players = players;
            TickBandwidthProfiler.onTickEnded += OnTickEnded;
        }

        public void Dispose()
        {
            TickBandwidthProfiler.onTickEnded -= OnTickEnded;
        }

        public bool HasSamples(int count)
        {
            for (var i = 0; i < _players.Count; i++)
            {
                if (_frames.GetValueOrDefault(_players[i]) < count)
                    return false;
            }
            return true;
        }

        public Dictionary<PlayerID, double> GetAverageBytes()
        {
            var result = new Dictionary<PlayerID, double>(_players.Count);
            for (var i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                result[player] = _bits[player] / 8.0 / _frames[player];
            }
            return result;
        }

        private void OnTickEnded()
        {
            var frames = TickBandwidthProfiler.wroteFrames;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (!_players.Contains(frame.player))
                    continue;
                _bits[frame.player] = _bits.GetValueOrDefault(frame.player) + frame.bitCount;
                _frames[frame.player] = _frames.GetValueOrDefault(frame.player) + 1;
            }
        }
    }
}

public sealed class InterestAnchorMarker : PredictedIdentity<InterestAnchorMarker.AnchorState>
{
    public struct AnchorState : IPredictedData<AnchorState>
    {
        public void Dispose() { }
    }

    public static readonly List<InterestAnchorMarker> instances = new();

    public PredictedTransform predictedTransform { get; private set; }

    public Vector3 position => predictedTransform.currentState.unityPosition;

    public static void ResetInstances()
    {
        instances.Clear();
    }

    private void Awake()
    {
        predictedTransform = GetComponent<PredictedTransform>();
    }

    protected override void LateAwake()
    {
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    public void SetServerPosition(Vector3 position)
    {
        transform.position = position;
        ref var state = ref predictedTransform.currentState;
        state.unityPosition = position;
    }
}

public sealed class InterestStateProbe : DeterministicIdentity<InterestStateProbe.ProbeState>
{
    private const ulong InitialSequence = 14695981039346656037UL;

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public ulong firstTick;
        public ulong lastTick;
        public ulong sequence;
        public uint simulations;

        public void Dispose() { }
    }

    public static readonly List<InterestStateProbe> instances = new();

    private PredictedTransform _predictedTransform;

    public int localSimulationCalls { get; private set; }
    public int localViewUpdates { get; private set; }
    public Vector3 position => _predictedTransform.currentState.unityPosition;
    public bool stateIsValid => Validate(currentState);

    public static void ResetInstances()
    {
        instances.Clear();
    }

    private void Awake()
    {
        _predictedTransform = GetComponent<PredictedTransform>();
    }

    protected override void LateAwake()
    {
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    protected override ProbeState GetInitialState()
    {
        return new ProbeState
        {
            sequence = InitialSequence
        };
    }

    protected override void Simulate(ref ProbeState state, sfloat delta)
    {
        ulong tick = predictionManager.time.tick;
        if (state.simulations == 0)
            state.firstTick = tick;
        state.lastTick = tick;
        state.sequence = Advance(state.sequence, tick);
        state.simulations += 1;
        localSimulationCalls += 1;
    }

    protected override void UpdateView(ProbeState viewState, ProbeState? verified)
    {
        localViewUpdates += 1;
    }

    private static bool Validate(ProbeState state)
    {
        if (state.simulations == 0 || state.lastTick != state.firstTick + state.simulations - 1)
            return false;

        ulong sequence = InitialSequence;
        for (uint i = 0; i < state.simulations; i++)
            sequence = Advance(sequence, state.firstTick + i);
        return sequence == state.sequence;
    }

    private static ulong Advance(ulong sequence, ulong tick)
    {
        return unchecked((sequence ^ tick) * 1099511628211UL + 11400714819323198485UL);
    }
}

public sealed class InterestRateProbe : PredictedIdentity<InterestRateProbe.ProbeState>
{
    private const ulong InitialSequence = 1099511628211UL;

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public ulong firstTick;
        public ulong lastTick;
        public ulong sequence;
        public ulong checkpointLastTick;
        public ulong checkpointSequence;
        public uint simulations;
        public uint checkpoint;
        public bool active;

        public void Dispose() { }
    }

    public static readonly List<InterestRateProbe> instances = new();

    public int localSimulationCalls { get; private set; }
    public int localViewUpdates { get; private set; }
    public Vector3 position => transform.position;
    public bool stateIsValid => Validate(currentState);

    public bool verifiedStateIsValid
    {
        get
        {
            var state = verifiedState;
            return state.HasValue && Validate(state.Value);
        }
    }

    public static void ResetInstances()
    {
        instances.Clear();
    }

    protected override void LateAwake()
    {
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    protected override ProbeState GetInitialState()
    {
        return new ProbeState
        {
            sequence = InitialSequence,
            active = true
        };
    }

    protected override void Simulate(ref ProbeState state, float delta)
    {
        if (!state.active)
            return;

        ulong tick = state.simulations == 0
            ? predictionManager.time.tick
            : state.lastTick + 1;
        if (state.simulations == 0)
            state.firstTick = tick;
        state.lastTick = tick;
        state.sequence = Advance(state.sequence, tick);
        state.simulations += 1;
        localSimulationCalls += 1;
    }

    protected override void UpdateView(ProbeState viewState, ProbeState? verified)
    {
        localViewUpdates += 1;
    }

    public bool StateEquals(ProbeState other)
    {
        ref var state = ref currentState;
        return state.firstTick == other.firstTick &&
               state.lastTick == other.lastTick &&
               state.sequence == other.sequence &&
               state.checkpointLastTick == other.checkpointLastTick &&
               state.checkpointSequence == other.checkpointSequence &&
               state.simulations == other.simulations &&
               state.checkpoint == other.checkpoint &&
               state.active == other.active;
    }

    public void Freeze(uint checkpoint)
    {
        ref var state = ref currentState;
        state.active = false;
        state.checkpoint = checkpoint;
        state.checkpointLastTick = state.lastTick;
        state.checkpointSequence = state.sequence;
    }

    public void Resume()
    {
        currentState.active = true;
    }

    public string StateDigest()
    {
        ref var state = ref currentState;
        return $"first={state.firstTick};last={state.lastTick};simulations={state.simulations};" +
               $"sequence={state.sequence};checkpoint={state.checkpoint};" +
               $"checkpointLast={state.checkpointLastTick};checkpointSequence={state.checkpointSequence};" +
               $"active={state.active};verified={lastVerifiedTick}";
    }

    public string ExactStateDigest()
    {
        ref var state = ref currentState;
        return $"first={state.firstTick};last={state.lastTick};simulations={state.simulations};" +
               $"sequence={state.sequence};checkpoint={state.checkpoint};" +
               $"checkpointLast={state.checkpointLastTick};checkpointSequence={state.checkpointSequence};" +
               $"active={state.active}";
    }

    private static bool Validate(ProbeState state)
    {
        if (state.simulations == 0 || state.lastTick != state.firstTick + state.simulations - 1)
            return false;

        ulong sequence = InitialSequence;
        for (uint i = 0; i < state.simulations; i++)
            sequence = Advance(sequence, state.firstTick + i);
        return sequence == state.sequence;
    }

    private static ulong Advance(ulong sequence, ulong tick)
    {
        return unchecked((sequence ^ tick) * 14695981039346656037UL + 11400714819323198485UL);
    }
}

public sealed class InterestReplayTierProbe : PredictedRigidbody
{
    public static readonly List<InterestReplayTierProbe> instances = new();
    private static readonly HashSet<PredictedObjectID> _transitionedRoots = new();

    public bool transitionAttempted { get; private set; }
    public bool transitionDuringReplay { get; private set; }
    public bool tierPolicyApplied { get; private set; }
    public bool replayPhysicsApplied { get; private set; }
    public bool tierRestored { get; private set; }
    public bool configuredPolicyRestored { get; private set; }
    public bool configuredPhysicsRestored { get; private set; }

    public static void ResetInstances()
    {
        instances.Clear();
        _transitionedRoots.Clear();
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
        base.Destroyed();
    }

    protected override void Simulate(ref UnityRigidbodyState state, float delta)
    {
        base.Simulate(ref state, delta);

        if (transitionAttempted || _transitionedRoots.Contains(rootObjectId) || !predictionManager ||
            predictionManager.cachedIsServer ||
            !predictionManager.isReplaying || !isRelevant)
            return;

        _transitionedRoots.Add(rootObjectId);
        transitionAttempted = true;
        transitionDuringReplay = predictionManager.isReplaying;
        bool wasKinematic = rb.isKinematic;
        var previousConstraints = rb.constraints;
        ulong serverTick = lastVerifiedTick ?? predictionManager.localTick;
        predictionManager.interest.ReceiveLocalTier(rootObjectId, 1, serverTick);
        tierPolicyApplied = predictionPolicy == PredictionPolicy.SoftCorrection;
        replayPhysicsApplied = rb && rb.constraints == RigidbodyConstraints.FreezeAll;
        predictionManager.interest.ReceiveLocalTier(rootObjectId, 0, serverTick);
        tierRestored = !predictionManager.interest.TryGetLocalRelevance(rootObjectId, out _);
        configuredPolicyRestored = predictionPolicy == PredictionPolicy.FullPrediction;
        configuredPhysicsRestored = rb && rb.isKinematic == wasKinematic &&
                                    rb.constraints == previousConstraints;
    }

    public string TransitionDigest()
    {
        return $"attempted={transitionAttempted};duringReplay={transitionDuringReplay};" +
               $"tierPolicy={tierPolicyApplied};replayPhysics={replayPhysicsApplied};" +
               $"tierRestored={tierRestored};restoredPolicy={configuredPolicyRestored};" +
               $"restoredPhysics={configuredPhysicsRestored};" +
               $"policy={predictionPolicy};kinematic={(rb ? rb.isKinematic : false)}";
    }
}

public sealed class InterestInputProbe : DeterministicIdentity<InterestInputProbe.ProbeInput, InterestInputProbe.ProbeState>
{
    private const ulong InitialChecksum = 7809847782465536322UL;
    private static bool _generateActiveInputs = true;

    public struct ProbeInput : IPredictedData
    {
        public bool valid;
        public bool active;
        public int direction;
        public uint sequence;
        public ulong tokenA;
        public ulong tokenB;
        public ulong tokenC;
        public ulong tokenD;

        public void Dispose() { }
    }

    public struct ProbeState : IPredictedData<ProbeState>
    {
        public sfloat x;
        public sfloat z;
        public sfloat travel;
        public ulong checksum;
        public uint appliedInputs;
        public uint lastSequence;
        public uint finalSequence;
        public bool finalized;

        public void Dispose() { }
    }

    public static readonly List<InterestInputProbe> instances = new();

    private uint _nextSequence;

    public int localSimulationCalls { get; private set; }
    public Vector3 position => transform.position;

    public static void ResetInstances()
    {
        instances.Clear();
        _generateActiveInputs = true;
    }

    public static void StopInputGeneration()
    {
        _generateActiveInputs = false;
    }

    protected override void LateAwake()
    {
        instances.Add(this);
    }

    protected override void Destroyed()
    {
        instances.Remove(this);
    }

    protected override ProbeState GetInitialState()
    {
        return new ProbeState
        {
            x = sfloat.FromFloat(transform.position.x),
            z = sfloat.FromFloat(transform.position.z),
            checksum = InitialChecksum
        };
    }

    protected override void GetFinalInput(ref ProbeInput input)
    {
        _nextSequence += 1;
        if (_nextSequence == 0)
            _nextSequence = 1;

        input.valid = true;
        input.active = _generateActiveInputs;
        input.direction = (_nextSequence & 1) == 0 ? 1 : -1;
        input.sequence = _nextSequence;
        input.tokenA = Mix(_nextSequence, 0x9E3779B97F4A7C15UL);
        input.tokenB = Mix(_nextSequence, 0xD1B54A32D192ED03UL);
        input.tokenC = Mix(_nextSequence, 0x94D049BB133111EBUL);
        input.tokenD = Mix(_nextSequence, 0xBF58476D1CE4E5B9UL);
    }

    protected override void ModifyExtrapolatedInput(ref ProbeInput input)
    {
        input.valid = false;
    }

    protected override void Simulate(ProbeInput input, ref ProbeState state, sfloat delta)
    {
        localSimulationCalls += 1;

        if (!input.valid || state.finalized)
            return;

        if (!input.active)
        {
            state.finalized = true;
            state.finalSequence = input.sequence;
            return;
        }

        state.checksum = Advance(state.checksum, input);
        state.appliedInputs += 1;
        state.lastSequence = input.sequence;

        var step = sfloat.FromFloat(input.direction * 0.025f);
        state.z += step;
        state.travel += sfloat.FromFloat(Mathf.Abs(input.direction) * 0.025f);
        SetUnityState(state);
    }

    protected override void SetUnityState(ProbeState state)
    {
        transform.position = new Vector3(state.x.ToFloat(), 0f, state.z.ToFloat());
    }

    public bool StateEquals(ProbeState other)
    {
        ref var state = ref currentState;
        return state.x.rawValue == other.x.rawValue &&
               state.z.rawValue == other.z.rawValue &&
               state.travel.rawValue == other.travel.rawValue &&
               state.checksum == other.checksum &&
               state.appliedInputs == other.appliedInputs &&
               state.lastSequence == other.lastSequence &&
               state.finalSequence == other.finalSequence &&
               state.finalized == other.finalized;
    }

    public string StateDigest()
    {
        ref var state = ref currentState;
        return $"applied={state.appliedInputs};last={state.lastSequence};final={state.finalSequence};" +
               $"checksum={state.checksum};x={state.x.rawValue};z={state.z.rawValue};travel={state.travel.rawValue}";
    }

    private static ulong Advance(ulong checksum, ProbeInput input)
    {
        checksum = unchecked((checksum ^ input.tokenA) * 1099511628211UL + input.sequence);
        checksum = unchecked((checksum ^ input.tokenB) * 1099511628211UL + (uint)input.direction);
        checksum = unchecked((checksum ^ input.tokenC) * 1099511628211UL);
        return unchecked((checksum ^ input.tokenD) * 1099511628211UL);
    }

    private static ulong Mix(uint sequence, ulong salt)
    {
        ulong value = sequence + salt;
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }
}

public static class InterestRateReportGate
{
    private readonly struct RateReport
    {
        public readonly uint root;
        public readonly string digest;

        public RateReport(uint root, string digest)
        {
            this.root = root;
            this.digest = digest;
        }
    }

    private static readonly Dictionary<uint, Dictionary<PlayerID, RateReport>> _reports = new();

    public static void Reset()
    {
        _reports.Clear();
    }

    public static void Report(uint checkpoint, InterestRateProbe probe)
    {
        Submit(checkpoint, probe.rootObjectId.instanceId.value, probe.ExactStateDigest());
    }

    public static async UniTask<ScenarioResult> Compare(
        ScenarioContext ctx,
        uint checkpoint,
        InterestRateProbe first,
        InterestRateProbe second,
        float timeout)
    {
        await UniTaskUtils.WaitWithTimeout(
            () => _reports.TryGetValue(checkpoint, out var reports) &&
                  reports.Count >= ctx.externalClientCount,
            timeout,
            ctx.cancellationToken);

        var failures = new List<string>();
        var received = _reports[checkpoint];
        foreach (var pair in received)
        {
            InterestRateProbe expected = null;
            if (first.rootObjectId.instanceId.value == pair.Value.root)
                expected = first;
            else if (second.rootObjectId.instanceId.value == pair.Value.root)
                expected = second;

            if (!expected)
            {
                failures.Add(
                    $"player {pair.Key.id.value} reported unknown rate root {pair.Value.root} at checkpoint {checkpoint}");
                continue;
            }

            var expectedDigest = expected.ExactStateDigest();
            if (pair.Value.digest != expectedDigest)
            {
                failures.Add(
                    $"player {pair.Key.id.value} rate root {pair.Value.root} checkpoint {checkpoint} diverged: " +
                    $"'{pair.Value.digest}' != '{expectedDigest}'");
            }
        }

        _reports.Remove(checkpoint);
        return failures.Count == 0
            ? ScenarioResult.Ok()
            : ScenarioResult.Fail(string.Join(" | ", failures));
    }

    [ServerRpc(requireOwnership: false)]
    private static void Submit(uint checkpoint, uint root, string digest, RPCInfo info = default)
    {
        if (!_reports.TryGetValue(checkpoint, out var reports))
        {
            reports = new Dictionary<PlayerID, RateReport>();
            _reports[checkpoint] = reports;
        }

        reports[info.sender] = new RateReport(root, digest);
    }
}

public static class InterestInputReportGate
{
    private readonly struct InputReport
    {
        public readonly uint root;
        public readonly string digest;

        public InputReport(uint root, string digest)
        {
            this.root = root;
            this.digest = digest;
        }
    }

    private static readonly Dictionary<PlayerID, InputReport> _reports = new();

    public static void Reset()
    {
        _reports.Clear();
    }

    public static void Report(InterestInputProbe probe)
    {
        Submit(probe.rootObjectId.instanceId.value, probe.StateDigest());
    }

    public static async UniTask<ScenarioResult> Compare(ScenarioContext ctx, InterestInputProbe first,
        InterestInputProbe second, float timeout)
    {
        await UniTaskUtils.WaitWithTimeout(
            () => _reports.Count >= ctx.externalClientCount,
            timeout,
            ctx.cancellationToken);

        var failures = new List<string>();
        foreach (var pair in _reports)
        {
            InterestInputProbe expected = null;
            if (first.rootObjectId.instanceId.value == pair.Value.root)
                expected = first;
            else if (second.rootObjectId.instanceId.value == pair.Value.root)
                expected = second;

            if (!expected)
            {
                failures.Add($"player {pair.Key.id.value} reported unknown input root {pair.Value.root}");
                continue;
            }

            var expectedDigest = expected.StateDigest();
            if (pair.Value.digest != expectedDigest)
                failures.Add($"player {pair.Key.id.value} input root {pair.Value.root} diverged: " +
                             $"'{pair.Value.digest}' != '{expectedDigest}'");
        }

        _reports.Clear();
        return failures.Count == 0
            ? ScenarioResult.Ok()
            : ScenarioResult.Fail(string.Join(" | ", failures));
    }

    [ServerRpc(requireOwnership: false)]
    private static void Submit(uint root, string digest, RPCInfo info = default)
    {
        _reports[info.sender] = new InputReport(root, digest);
    }
}
