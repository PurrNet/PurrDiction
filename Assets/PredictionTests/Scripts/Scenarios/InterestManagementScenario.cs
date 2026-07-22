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
    private const float HoldVisibleDistance = 32f;
    private const float CullDistance = 45f;
    private const float HoldCulledDistance = 35f;
    private const float ReentryDistance = 25f;
    private const int InitialBarrier = 21000;
    private const int HoldVisibleBarrier = 21001;
    private const int CulledBarrier = 21002;
    private const int HoldCulledBarrier = 21003;
    private const int ReentryBarrier = 21004;
    private const int DigestChannel = 21005;
    private const int FinalInputBarrier = 21006;
    private const int FrameSampleCount = 30;

    private PredictionLODProfile _predictionProfile;
    private NetworkLODProfile _networkProfile;
    private GameObject _anchorPrefab;
    private GameObject _probePrefab;
    private GameObject _inputProbePrefab;
    private int _anchorPrefabId;
    private int _probePrefabId;
    private int _inputProbePrefabId;

    private readonly List<PlayerID> _players = new(2);
    private InterestAnchorMarker _serverAnchorA;
    private InterestAnchorMarker _serverAnchorB;
    private InterestStateProbe _serverProbeA;
    private InterestStateProbe _serverProbeB;
    private InterestInputProbe _serverInputProbeA;
    private InterestInputProbe _serverInputProbeB;
    private InterestAnchorMarker _localAnchor;
    private InterestStateProbe _localNearProbe;
    private InterestStateProbe _localFarProbe;
    private InterestInputProbe _localObservedInputProbe;
    private Dictionary<PlayerID, double> _visibleFrameBytes;
    private Dictionary<PlayerID, double> _culledFrameBytes;

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
            }
        }, true);

        _predictionProfile = ScriptableObject.CreateInstance<PredictionLODProfile>();
        _predictionProfile.Configure(_networkProfile);
        ctx.predictionManager.predictionLODProfile = _predictionProfile;

        InterestAnchorMarker.ResetInstances();
        InterestStateProbe.ResetInstances();
        InterestInputProbe.ResetInstances();
        InterestInputReportGate.Reset();

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
                () => HasVisibleNeighborhoodTiers(pm),
                Timeout,
                ctx.cancellationToken);
            await WaitServerTicks(pm, 12, ctx);
            _visibleFrameBytes = await SampleFrameBytes(ctx);
            await ScenarioBarrier.Wait(ctx, InitialBarrier, Timeout);

            stage = "visible hysteresis";
            SetAnchorDistance(HoldVisibleDistance);
            await WaitServerTicks(pm, 12, ctx);
            if (!HasVisibleNeighborhoodTiers(pm))
                return ScenarioResult.Fail($"visible hysteresis failed: {DescribeServerTiers(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldVisibleBarrier, Timeout);

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
            await WaitServerTicks(pm, 12, ctx);
            if (!HasCulledNeighborhoodTiers(pm))
                return ScenarioResult.Fail($"culled hysteresis failed: {DescribeServerTiers(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldCulledBarrier, Timeout);

            stage = "reentry";
            SetAnchorDistance(ReentryDistance);
            await UniTaskUtils.WaitWithTimeout(
                () => HasVisibleNeighborhoodTiers(pm),
                Timeout,
                ctx.cancellationToken);
            await ScenarioBarrier.Wait(ctx, ReentryBarrier, Timeout);

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

            if (!_serverProbeA.stateIsValid || !_serverProbeB.stateIsValid)
                return ScenarioResult.Fail("server probe state failed its deterministic checksum");

            var digestResult = await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(pm, _serverProbeA), Timeout);
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
                      _localObservedInputProbe.isRelevant &&
                      IsLocallyCulled(pm, _localFarProbe),
                Timeout,
                ctx.cancellationToken);

            if (!HasRetainedHierarchy(pm))
                return ScenarioResult.Fail("culled root was removed from the replicated hierarchy");
            if (!_localFarProbe.gameObject.activeInHierarchy)
                return ScenarioResult.Fail("culled root was locally despawned or deactivated");
            if (!_localObservedInputProbe.gameObject.activeInHierarchy)
                return ScenarioResult.Fail("input-driven root was locally despawned or deactivated");
            if (!_localAnchor.predictedTransform.isRelevant)
                return ScenarioResult.Fail("locally owned anchor was culled");
            if (_localNearProbe.GetComponent<Rigidbody>().isKinematic)
                return ScenarioResult.Fail("visible raw rigidbody started frozen");

            await UniTaskUtils.WaitWithTimeout(
                () => _localNearProbe.localSimulationCalls >= 10 && _localNearProbe.localViewUpdates >= 3,
                Timeout,
                ctx.cancellationToken);
            await ScenarioBarrier.Wait(ctx, InitialBarrier, Timeout);

            stage = "visible hysteresis";
            await WaitClientTicks(pm, 24, ctx);
            if (!_localNearProbe.isRelevant || !_localObservedInputProbe.isRelevant || IsLocallyCulled(pm, _localObservedInputProbe) ||
                !IsLocallyCulled(pm, _localFarProbe))
                return ScenarioResult.Fail($"visible hysteresis failed: {DescribeClient(pm)}");
            await ScenarioBarrier.Wait(ctx, HoldVisibleBarrier, Timeout);

            stage = "cull";
            await UniTaskUtils.WaitWithTimeout(
                () => IsLocallyCulled(pm, _localNearProbe) && IsLocallyCulled(pm, _localObservedInputProbe),
                Timeout,
                ctx.cancellationToken);
            await UniTask.NextFrame(ctx.cancellationToken);
            await UniTask.NextFrame(ctx.cancellationToken);

            uint frozenSimulations = _localNearProbe.currentState.simulations;
            int frozenCalls = _localNearProbe.localSimulationCalls;
            int frozenViews = _localNearProbe.localViewUpdates;
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
            if (!_localObservedInputProbe.StateEquals(frozenInputState) ||
                _localObservedInputProbe.localSimulationCalls != frozenInputCalls)
                return ScenarioResult.Fail($"culled input probe kept simulating: {DescribeClient(pm)}");
            if (!_localNearProbe.GetComponent<Rigidbody>().isKinematic)
                return ScenarioResult.Fail("culled raw rigidbody remained dynamic");
            if (!HasRetainedHierarchy(pm) || !_localNearProbe.gameObject.activeInHierarchy)
                return ScenarioResult.Fail("culled probe did not remain as an active replicated hierarchy instance");

            await ScenarioBarrier.Wait(ctx, CulledBarrier, Timeout);

            stage = "culled hysteresis";
            await WaitClientTicks(pm, 24, ctx);
            if (!IsLocallyCulled(pm, _localNearProbe) || !IsLocallyCulled(pm, _localObservedInputProbe))
                return ScenarioResult.Fail($"culled hysteresis failed: {DescribeClient(pm)}");
            if (_localNearProbe.currentState.simulations != frozenSimulations ||
                _localNearProbe.localSimulationCalls != frozenCalls ||
                _localNearProbe.localViewUpdates != frozenViews)
                return ScenarioResult.Fail("culled probe resumed before crossing the reentry edge");
            if (!_localObservedInputProbe.StateEquals(frozenInputState) ||
                _localObservedInputProbe.localSimulationCalls != frozenInputCalls)
                return ScenarioResult.Fail("culled input probe resumed before crossing the reentry edge");
            await ScenarioBarrier.Wait(ctx, HoldCulledBarrier, Timeout);

            stage = "absolute reentry";
            await UniTaskUtils.WaitWithTimeout(
                () => _localNearProbe.isRelevant &&
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
                () => _localNearProbe.localSimulationCalls > frozenCalls &&
                      _localNearProbe.localViewUpdates > frozenViews,
                Timeout,
                ctx.cancellationToken);

            if (!IsLocallyCulled(pm, _localFarProbe))
                return ScenarioResult.Fail("reentry made the other client's far root relevant");
            if (!HasRetainedHierarchy(pm))
                return ScenarioResult.Fail("hierarchy diverged across cull and reentry");
            if (_localNearProbe.GetComponent<Rigidbody>().isKinematic)
                return ScenarioResult.Fail("reentered raw rigidbody remained frozen");

            await ScenarioBarrier.Wait(ctx, ReentryBarrier, Timeout);

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
            return await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(pm, _localNearProbe), Timeout);
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
        var inputProbeA = pm.hierarchy.Create(_inputProbePrefab, new Vector3(RootOffset, 0f, 0f), Quaternion.identity, _players[0]);
        var inputProbeB = pm.hierarchy.Create(_inputProbePrefab, new Vector3(-RootOffset, 0f, 0f), Quaternion.identity, _players[1]);

        if (!anchorA.HasValue || !anchorB.HasValue || !probeA.HasValue || !probeB.HasValue ||
            !inputProbeA.HasValue || !inputProbeB.HasValue)
            return false;

        _serverAnchorA = pm.hierarchy.GetComponent<InterestAnchorMarker>(anchorA);
        _serverAnchorB = pm.hierarchy.GetComponent<InterestAnchorMarker>(anchorB);
        _serverProbeA = pm.hierarchy.GetComponent<InterestStateProbe>(probeA);
        _serverProbeB = pm.hierarchy.GetComponent<InterestStateProbe>(probeB);
        _serverInputProbeA = pm.hierarchy.GetComponent<InterestInputProbe>(inputProbeA);
        _serverInputProbeB = pm.hierarchy.GetComponent<InterestInputProbe>(inputProbeB);
        return _serverAnchorA && _serverAnchorB && _serverProbeA && _serverProbeB &&
               _serverInputProbeA && _serverInputProbeB;
    }

    private bool ResolveLocalLayout(PredictionManager pm)
    {
        if (!pm.localPlayer.HasValue || InterestAnchorMarker.instances.Count != 2 ||
            InterestStateProbe.instances.Count != 2 || InterestInputProbe.instances.Count != 2)
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

        var first = InterestStateProbe.instances[0];
        var second = InterestStateProbe.instances[1];
        if (!first || !second || Mathf.Abs(first.position.x) < RootOffset * 0.5f || Mathf.Abs(second.position.x) < RootOffset * 0.5f)
            return false;

        float firstDistance = Mathf.Abs(first.position.x - _localAnchor.position.x);
        float secondDistance = Mathf.Abs(second.position.x - _localAnchor.position.x);
        _localNearProbe = firstDistance < secondDistance ? first : second;
        _localFarProbe = ReferenceEquals(_localNearProbe, first) ? second : first;

        _localObservedInputProbe = null;
        for (var i = 0; i < InterestInputProbe.instances.Count; i++)
        {
            var inputProbe = InterestInputProbe.instances[i];
            if (!inputProbe || inputProbe.owner == pm.localPlayer)
                continue;
            if (Mathf.Abs(inputProbe.position.x - _localAnchor.position.x) <= 2f)
            {
                _localObservedInputProbe = inputProbe;
                break;
            }
        }

        return _localObservedInputProbe;
    }

    private void SetAnchorDistance(float distance)
    {
        _serverAnchorA.SetServerPosition(new Vector3(-RootOffset - distance, 0f, 0f));
        _serverAnchorB.SetServerPosition(new Vector3(RootOffset + distance, 0f, 0f));
    }

    private bool HasVisibleNeighborhoodTiers(PredictionManager pm)
    {
        return HasTier(pm, _players[0], _serverProbeA, 0) &&
               HasTier(pm, _players[0], _serverProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeB, 0) &&
               HasTier(pm, _players[0], _serverInputProbeA, 0) &&
               HasTier(pm, _players[0], _serverInputProbeB, 0) &&
               HasTier(pm, _players[1], _serverInputProbeA, 0) &&
               HasTier(pm, _players[1], _serverInputProbeB, 0);
    }

    private bool HasCulledNeighborhoodTiers(PredictionManager pm)
    {
        return HasTier(pm, _players[0], _serverProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[0], _serverInputProbeA, 0) &&
               HasTier(pm, _players[0], _serverInputProbeB, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverInputProbeA, NetworkLODProfile.CulledTier) &&
               HasTier(pm, _players[1], _serverInputProbeB, 0);
    }

    private static bool HasTier(PredictionManager pm, PlayerID player, PredictedIdentity probe, byte expected)
    {
        return probe && pm.interest.TryGetTier(player, probe.rootObjectId, out var tier) && tier == expected;
    }

    private static bool IsLocallyCulled(PredictionManager pm, PredictedIdentity probe)
    {
        return probe && !probe.isRelevant &&
               pm.interest.TryGetLocalRelevance(probe.rootObjectId, out var tier) &&
               tier == NetworkLODProfile.CulledTier;
    }

    private bool HasRetainedHierarchy(PredictionManager pm)
    {
        return PredictionTestUtils.CountInstances(pm, _anchorPrefabId) == 2 &&
               PredictionTestUtils.CountInstances(pm, _probePrefabId) == 2 &&
               PredictionTestUtils.CountInstances(pm, _inputProbePrefabId) == 2 &&
               InterestAnchorMarker.instances.Count == 2 &&
               InterestStateProbe.instances.Count == 2 &&
               InterestInputProbe.instances.Count == 2;
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

    private string FrameByteSummary()
    {
        if (_players.Count != 2 || _visibleFrameBytes == null || _culledFrameBytes == null)
            return "bytes=unavailable";

        var p0 = _players[0];
        var p1 = _players[1];
        return $"bytes=p0:{_visibleFrameBytes[p0]:F2}->{_culledFrameBytes[p0]:F2}," +
               $"p1:{_visibleFrameBytes[p1]:F2}->{_culledFrameBytes[p1]:F2}";
    }

    private string DescribeServerTiers(PredictionManager pm)
    {
        if (_players.Count != 2 || !_serverProbeA || !_serverProbeB)
            return $"players={_players.Count} probes={(_serverProbeA ? 1 : 0) + (_serverProbeB ? 1 : 0)}";

        return $"p0=({GetTier(pm, _players[0], _serverProbeA)},{GetTier(pm, _players[0], _serverProbeB)};" +
               $"{GetTier(pm, _players[0], _serverInputProbeA)},{GetTier(pm, _players[0], _serverInputProbeB)}) " +
               $"p1=({GetTier(pm, _players[1], _serverProbeA)},{GetTier(pm, _players[1], _serverProbeB)};" +
               $"{GetTier(pm, _players[1], _serverInputProbeA)},{GetTier(pm, _players[1], _serverInputProbeB)})";
    }

    private string DescribeClient(PredictionManager pm)
    {
        if (!_localNearProbe || !_localFarProbe || !_localObservedInputProbe)
            return $"anchors={InterestAnchorMarker.instances.Count} probes={InterestStateProbe.instances.Count} " +
                   $"inputProbes={InterestInputProbe.instances.Count}";

        return $"nearRelevant={_localNearProbe.isRelevant} farRelevant={_localFarProbe.isRelevant} " +
               $"nearTier={GetLocalTier(pm, _localNearProbe)} farTier={GetLocalTier(pm, _localFarProbe)} " +
               $"state={_localNearProbe.currentState.simulations} calls={_localNearProbe.localSimulationCalls} " +
               $"views={_localNearProbe.localViewUpdates} inputRelevant={_localObservedInputProbe.isRelevant} " +
               $"inputTier={GetLocalTier(pm, _localObservedInputProbe)} " +
               $"inputState={_localObservedInputProbe.StateDigest()}";
    }

    private static int GetTier(PredictionManager pm, PlayerID player, PredictedIdentity probe)
    {
        return pm.interest.TryGetTier(player, probe.rootObjectId, out var tier) ? tier : -1;
    }

    private static int GetLocalTier(PredictionManager pm, PredictedIdentity probe)
    {
        return pm.interest.TryGetLocalRelevance(probe.rootObjectId, out var tier) ? tier : 0;
    }

    private string BuildDigest(PredictionManager pm, InterestStateProbe probe)
    {
        return $"anchors={PredictionTestUtils.CountInstances(pm, _anchorPrefabId)};" +
               $"probes={PredictionTestUtils.CountInstances(pm, _probePrefabId)};" +
               $"inputProbes={PredictionTestUtils.CountInstances(pm, _inputProbePrefabId)};" +
               $"first={probe.currentState.firstTick};valid={probe.stateIsValid}";
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
