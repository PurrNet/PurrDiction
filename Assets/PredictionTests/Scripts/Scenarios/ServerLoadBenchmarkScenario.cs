using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Transports;
using Unity.Profiling;
using UnityEngine;

public class ServerLoadBenchmarkScenario : Scenario
{
    private static readonly ProfilerMarker ApplyVisibilityEventsMarker =
        new("ServerLoadBenchmark.ApplyVisibilityEvents");

    private static readonly ProfilerMarker ApplyDeleteChurnMarker =
        new("ServerLoadBenchmark.ApplyDeleteChurn");

    private const float SpawnTimeout = 120f;
    private const int ReadyBarrierId = 20_001;
    private const int FinalVisibilityBarrierId = 20_002;
    private const int DefaultVisibilityPercent = 25;
    private const int DefaultVisibilityChurnPercent = 10;
    private const int DefaultVisibilityChurnTicks = 30;

    private GameObject _moverPrefab;
    private GameObject _passiveMoverPrefab;
    private GameObject _driverPrefab;
    private int _moverCount = 200;
    private int _inputEvery = 1;
    private float _minClientApplyRate;
    private float _benchSeconds = 20f;
    private float _settleSeconds = 3f;
    private BenchmarkVisibilityMode _visibilityMode;
    private int _visibilityPercent = DefaultVisibilityPercent;
    private int _visibilityChurnPercent = DefaultVisibilityChurnPercent;
    private int _visibilityChurnTicks = DefaultVisibilityChurnTicks;
    private int _deleteChurnPerSecond;
    private string _configurationError;
    private BenchmarkVisibilityController _visibilityController;
    private BenchmarkDeleteChurnController _deleteChurnController;
    private readonly List<PredictedObjectID> _signatureRootsScratch = new();
    private readonly List<PredictedObjectID> _expectedRootsScratch = new();
    private ulong _windowDataSent;
    private ulong _windowDataReceived;
    private ulong _windowFullFrames;
    private ulong _windowDeltaFrames;
    private ulong _windowDeltaBytes;
    private ulong _windowFullBytes;
    private int _windowMaxDeltaBytes;
    private ulong _windowDeleteBits;
    private ulong _windowHierarchyBits;
    private ulong _windowInputBits;
    private ulong _windowStateBits;
    private ulong _clientFramesReceived;
    private ulong _clientFullFramesReceived;
    private ulong _clientRenderApplies;
    private ulong _clientTickApplies;
    private int _clientMaxApplyAge;
    private int _visibilityValidatedPlayers;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        if (CommandLineUtils.TryGetArgument("-benchObjects", out var objects)
            && int.TryParse(objects, out var parsedObjects) && parsedObjects > 0)
            _moverCount = parsedObjects;

        if (CommandLineUtils.TryGetArgument("-benchSeconds", out var seconds)
            && float.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
            && parsedSeconds > 0)
            _benchSeconds = parsedSeconds;

        if (CommandLineUtils.TryGetArgument("-benchInputEvery", out var inputEvery)
            && int.TryParse(inputEvery, out var parsedInputEvery) && parsedInputEvery >= 0)
            _inputEvery = parsedInputEvery;

        BenchMover.inputChangePeriod = 1;
        if (CommandLineUtils.TryGetArgument("-benchInputPeriod", out var inputPeriod)
            && int.TryParse(inputPeriod, out var parsedInputPeriod) && parsedInputPeriod >= 1)
            BenchMover.inputChangePeriod = parsedInputPeriod;

        if (CommandLineUtils.TryGetArgument("-benchMinClientApplyRate", out var minApplyRate)
            && float.TryParse(minApplyRate, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var parsedMinApplyRate)
            && parsedMinApplyRate > 0)
            _minClientApplyRate = parsedMinApplyRate;

        if (CommandLineUtils.TryGetArgument("-benchVisibilityMode", out var visibilityMode)
            && !TryParseVisibilityMode(visibilityMode, out _visibilityMode))
        {
            _configurationError =
                $"unknown -benchVisibilityMode '{visibilityMode}' " +
                "(expected none, static, churn, or acquire-churn)";
        }

        if (CommandLineUtils.TryGetArgument("-benchVisibilityPercent", out var visibilityPercent))
        {
            if (!int.TryParse(visibilityPercent, out _visibilityPercent) ||
                _visibilityPercent < 0 || _visibilityPercent > 100)
            {
                _configurationError =
                    $"-benchVisibilityPercent must be between 0 and 100, got '{visibilityPercent}'";
            }
        }

        if (CommandLineUtils.TryGetArgument(
                "-benchVisibilityChurnPercent",
                out var visibilityChurnPercent))
        {
            if (!int.TryParse(visibilityChurnPercent, out _visibilityChurnPercent) ||
                _visibilityChurnPercent < 0 || _visibilityChurnPercent > 100)
            {
                _configurationError =
                    "-benchVisibilityChurnPercent must be between 0 and 100, " +
                    $"got '{visibilityChurnPercent}'";
            }
        }

        if (CommandLineUtils.TryGetArgument(
                "-benchVisibilityChurnTicks",
                out var visibilityChurnTicks))
        {
            if (!int.TryParse(visibilityChurnTicks, out _visibilityChurnTicks) ||
                _visibilityChurnTicks <= 0)
            {
                _configurationError =
                    "-benchVisibilityChurnTicks must be greater than zero, " +
                    $"got '{visibilityChurnTicks}'";
            }
        }

        if (CommandLineUtils.TryGetArgument("-benchDeleteChurn", out var deleteChurn))
        {
            if (!int.TryParse(deleteChurn, out _deleteChurnPerSecond) ||
                _deleteChurnPerSecond <= 0)
            {
                _configurationError =
                    $"-benchDeleteChurn must be greater than zero, got '{deleteChurn}'";
            }
        }

        if (_deleteChurnPerSecond > 0 && _visibilityMode != BenchmarkVisibilityMode.None)
        {
            _configurationError =
                "-benchDeleteChurn cannot be combined with -benchVisibilityMode " +
                GetVisibilityModeName(_visibilityMode);
        }

        Application.targetFrameRate = 60;

        _moverPrefab = PredictionTestUtils.CreatePrefab<BenchMover>("BenchMover");
        PredictionTestUtils.RegisterPrefab(ctx, _moverPrefab);

        _passiveMoverPrefab = PredictionTestUtils.CreatePrefab<BenchPassiveMover>("BenchPassiveMover");
        PredictionTestUtils.RegisterPrefab(ctx, _passiveMoverPrefab);

        _driverPrefab = PredictionTestUtils.CreatePrefab<BenchDriver>("BenchDriver");
        var driver = _driverPrefab.GetComponent<BenchDriver>();
        driver.moverPrefab = _moverPrefab;
        driver.passiveMoverPrefab = _passiveMoverPrefab;
        driver.targetCount = 0;
        driver.inputEvery = _inputEvery;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);

        ServerLoadBenchmarkSignals.Reset();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (_configurationError != null)
            return ScenarioResult.Fail(_configurationError);

        if (ctx.role == NetworkRole.Host)
        {
            var clientTask = Client(ctx);
            var serverTask = Server(ctx);
            var (clientResult, serverResult) = await UniTask.WhenAll(clientTask, serverTask);
            if (!clientResult.success && !serverResult.success)
                return ScenarioResult.Fail($"client: {clientResult.message} | server: {serverResult.message}");
            if (!clientResult.success)
                return clientResult;
            return serverResult;
        }

        if (ctx.isServer)
            return await Server(ctx);
        return await Client(ctx);
    }

    private int CountMovers(PredictionManager pm)
    {
        pm.TryGetPrefab(_moverPrefab, out var moverPrefabId);
        pm.TryGetPrefab(_passiveMoverPrefab, out var passivePrefabId);
        return PredictionTestUtils.CountInstances(pm, moverPrefabId)
               + PredictionTestUtils.CountInstances(pm, passivePrefabId);
    }

    private async UniTask<ScenarioResult> Client(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var localPlayer = ctx.networkManager.localPlayer;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ServerLoadBenchmarkSignals.TryGetExpectedMovers(localPlayer, out _) ||
                      ServerLoadBenchmarkSignals.completed,
                SpawnTimeout + _settleSeconds + 10f,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("server never reported the expected mover count");
        }

        if (!ServerLoadBenchmarkSignals.TryGetExpectedMovers(localPlayer, out var expectedMovers))
            return ScenarioResult.Fail("server ended before reporting the expected mover count");

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => CountMovers(pm) == expectedMovers ||
                      ServerLoadBenchmarkSignals.completed,
                SpawnTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"visible movers never converged on client ({CountMovers(pm)}/{expectedMovers})");
        }

        var actualMovers = CountMovers(pm);
        if (actualMovers != expectedMovers)
        {
            return ScenarioResult.Fail(
                $"server ended before visible movers converged ({actualMovers}/{expectedMovers})");
        }

        try
        {
            await ScenarioBarrier.Wait(ctx, ReadyBarrierId, SpawnTimeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out synchronizing visibility benchmark readiness");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ServerLoadBenchmarkSignals.started ||
                      ServerLoadBenchmarkSignals.completed,
                30f,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("server never started the benchmark");
        }

        if (!ServerLoadBenchmarkSignals.started)
            return ScenarioResult.Fail("server ended before the benchmark started");

        bool sampleClient = ctx.role == NetworkRole.Client;
        ScenarioPerformanceSampler sampler = null;
        ScenarioPerformanceDetails clientPerf = default;
        ulong clientStartTick = pm.localTick;
        ulong clientElapsedTicks = 0;
        long clientFrameApplies = 0;
        float clientWindowSeconds = 0f;
        ulong clientStartFramesReceived = pm.framesReceivedTotal;
        ulong clientStartFullFramesReceived = pm.fullFramesReceivedTotal;
        void OnClientFrameApplied() => clientFrameApplies++;

        if (sampleClient)
        {
            sampler = ScenarioPerformanceSampler.StartDefault();
            pm.onRollbackFinished += OnClientFrameApplied;
        }

        float clientWindowStart = Time.realtimeSinceStartup;

        try
        {
            var validationDeadline = Time.realtimeSinceStartup + _benchSeconds + 30f;
            while (!ServerLoadBenchmarkSignals.TryGetExpectedFinalVisibility(
                       localPlayer,
                       out _) &&
                   !ServerLoadBenchmarkSignals.completed)
            {
                if (Time.realtimeSinceStartup > validationDeadline)
                {
                    return ScenarioResult.Fail(
                        "server never requested final visibility validation");
                }

                await UniTask.NextFrame(ctx.cancellationToken);
                sampler?.SampleFrame(pm);
            }

            if (sampleClient)
            {
                clientElapsedTicks = pm.localTick - clientStartTick;
                clientWindowSeconds = Time.realtimeSinceStartup - clientWindowStart;
                _clientFramesReceived = pm.framesReceivedTotal - clientStartFramesReceived;
                _clientFullFramesReceived = pm.fullFramesReceivedTotal - clientStartFullFramesReceived;
                _clientRenderApplies = pm.renderPhaseFrameAppliesTotal;
                _clientTickApplies = pm.tickPhaseFrameAppliesTotal;
                _clientMaxApplyAge = pm.maxFrameApplyAgeFrames;
                clientPerf = sampler.Stop(pm);
            }
        }
        finally
        {
            if (sampleClient)
                pm.onRollbackFinished -= OnClientFrameApplied;
            sampler?.Dispose();
        }

        if (!ServerLoadBenchmarkSignals.TryGetExpectedFinalVisibility(
                localPlayer,
                out var expectedFinalVisibility))
        {
            return ScenarioResult.Fail(
                "server ended before requesting final visibility validation");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ComputeCurrentMoverRootSignature(pm).Equals(
                          expectedFinalVisibility) ||
                      ServerLoadBenchmarkSignals.completed,
                SpawnTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            var actual = ComputeCurrentMoverRootSignature(pm);
            return ScenarioResult.Fail(
                BuildVisibilityMismatchMessage(
                    "final visibility never converged",
                    expectedFinalVisibility,
                    actual));
        }

        var actualFinalVisibility = ComputeCurrentMoverRootSignature(pm);
        if (!actualFinalVisibility.Equals(expectedFinalVisibility))
        {
            return ScenarioResult.Fail(
                BuildVisibilityMismatchMessage(
                    "server ended before final visibility converged",
                    expectedFinalVisibility,
                    actualFinalVisibility));
        }

        ServerLoadBenchmarkSignals.ReportFinalVisibility(actualFinalVisibility);

        try
        {
            await ScenarioBarrier.Wait(
                ctx,
                FinalVisibilityBarrierId,
                SpawnTimeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                "timed out synchronizing final visibility validation");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ServerLoadBenchmarkSignals.completed,
                30f,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("server never completed the benchmark");
        }

        if (!sampleClient)
            return ScenarioResult.Ok();

        var clientReport = BuildClientReport(
            clientPerf,
            clientElapsedTicks,
            clientFrameApplies,
            clientWindowSeconds);

        if (_minClientApplyRate > 0)
        {
            float applyRate = clientWindowSeconds > 0
                ? clientFrameApplies / clientWindowSeconds
                : 0f;
            if (applyRate < _minClientApplyRate)
            {
                return ScenarioResult.Fail(
                    "client verified-frame apply rate " +
                    applyRate.ToString("0.##", CultureInfo.InvariantCulture) +
                    "/s fell below the required " +
                    _minClientApplyRate.ToString("0.##", CultureInfo.InvariantCulture) +
                    $"/s (frameApplies={clientFrameApplies} over " +
                    clientWindowSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                    $"s) | {clientReport}");
            }
        }

        return ScenarioResult.Ok(clientReport);
    }

    private async UniTask<ScenarioResult> Server(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;

        try
        {
            var driverId = pm.hierarchy.Create(_driverPrefab);
            if (!driverId.HasValue)
                return ScenarioResult.Fail("failed to create bench driver");
            if (!driverId.Value.TryGetComponent(pm, out BenchDriver driver))
                return ScenarioResult.Fail("failed to resolve bench driver");

            driver.targetCount = _moverCount;

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => CountMovers(pm) >= _moverCount,
                    SpawnTimeout,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail(
                    $"movers never spawned on server ({CountMovers(pm)}/{_moverCount})");
            }

            await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

            var benchmarkRoots = CollectBenchmarkRoots(pm);
            if (benchmarkRoots.Count != _moverCount)
            {
                return ScenarioResult.Fail(
                    $"failed to resolve all benchmark mover roots " +
                    $"({benchmarkRoots.Count}/{_moverCount})");
            }

            if (_visibilityMode != BenchmarkVisibilityMode.None)
            {
                _visibilityController = new BenchmarkVisibilityController(
                    pm,
                    _visibilityMode,
                    benchmarkRoots,
                    _visibilityPercent,
                    _visibilityChurnPercent,
                    _visibilityChurnTicks);
                _visibilityController.ApplyInitial(
                    ctx.networkManager.players,
                    ctx.role == NetworkRole.Host
                        ? ctx.networkManager.localPlayer
                        : (PlayerID?)null);
            }

            if (_deleteChurnPerSecond > 0)
            {
                _deleteChurnController = new BenchmarkDeleteChurnController(
                    pm,
                    _moverPrefab,
                    _passiveMoverPrefab,
                    _deleteChurnPerSecond,
                    _moverCount);
                if (_deleteChurnController.rotationCount != _moverCount)
                {
                    return ScenarioResult.Fail(
                        "failed to capture delete churn rotation " +
                        $"({_deleteChurnController.rotationCount}/{_moverCount})");
                }
            }

            BroadcastExpectedMoverCounts(ctx, benchmarkRoots.Count);
            try
            {
                await ScenarioBarrier.Wait(ctx, ReadyBarrierId, SpawnTimeout);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail("timed out waiting for visibility benchmark clients");
            }

            _visibilityController?.BeginMeasurement(pm.localTick);
            ServerLoadBenchmarkSignals.BroadcastStarted();

            _windowDataSent = 0;
            _windowDataReceived = 0;
            _windowFullFrames = 0;
            var transport = ctx.networkManager.rawTransport;
            if (transport != null)
            {
                transport.onDataSent += OnWindowDataSent;
                transport.onDataReceived += OnWindowDataReceived;
            }

            var sampler = ScenarioPerformanceSampler.StartDefault();
            var startTick = pm.localTick;
            var startFullFrames = pm.fullFramesSentTotal;
            var startDeltaFrames = pm.deltaFramesWrittenTotal;
            var startDeltaBytes = pm.deltaFrameBytesTotal;
            var startFullBytes = pm.fullFrameBytesTotal;
            var startDeleteBits = pm.deltaSectionDeleteBitsTotal;
            var startHierarchyBits = pm.deltaSectionHierarchyBitsTotal;
            var startInputBits = pm.deltaSectionInputBitsTotal;
            var startStateBits = pm.deltaSectionStateBitsTotal;
            double lagSum = 0;
            ulong lagMax = 0;
            int lagSamples = 0;
            float start = Time.realtimeSinceStartup;
            ulong elapsedTicks = 0;
            ScenarioPerformanceDetails perf = default;
            _deleteChurnController?.BeginMeasurement(start);

            try
            {
                while (Time.realtimeSinceStartup - start < _benchSeconds)
                {
                    await UniTask.NextFrame(ctx.cancellationToken);
                    using (ApplyVisibilityEventsMarker.Auto())
                        _visibilityController?.Update(pm.localTick);
                    using (ApplyDeleteChurnMarker.Auto())
                        _deleteChurnController?.Update(Time.realtimeSinceStartup);
                    sampler.SampleFrame(pm);

                    var lag = pm.lastMaxAckLagTicks;
                    lagSum += lag;
                    if (lag > lagMax)
                        lagMax = lag;
                    lagSamples++;
                }

                elapsedTicks = pm.localTick - startTick;
                _windowFullFrames = pm.fullFramesSentTotal - startFullFrames;
                _windowDeltaFrames = pm.deltaFramesWrittenTotal - startDeltaFrames;
                _windowDeltaBytes = pm.deltaFrameBytesTotal - startDeltaBytes;
                _windowFullBytes = pm.fullFrameBytesTotal - startFullBytes;
                _windowMaxDeltaBytes = pm.maxDeltaFrameBytes;
                _windowDeleteBits = pm.deltaSectionDeleteBitsTotal - startDeleteBits;
                _windowHierarchyBits = pm.deltaSectionHierarchyBitsTotal - startHierarchyBits;
                _windowInputBits = pm.deltaSectionInputBitsTotal - startInputBits;
                _windowStateBits = pm.deltaSectionStateBitsTotal - startStateBits;
                perf = sampler.Stop(pm);
            }
            finally
            {
                if (transport != null)
                {
                    transport.onDataSent -= OnWindowDataSent;
                    transport.onDataReceived -= OnWindowDataReceived;
                }
                sampler.Dispose();
            }

            if (_deleteChurnController != null)
            {
                try
                {
                    await UniTaskUtils.WaitWithTimeout(
                        () =>
                        {
                            CollectBenchmarkRoots(pm, benchmarkRoots);
                            return benchmarkRoots.Count == _moverCount;
                        },
                        30f,
                        ctx.cancellationToken);
                }
                catch (TimeoutException)
                {
                    return ScenarioResult.Fail(
                        "mover roots never settled after delete churn " +
                        $"({benchmarkRoots.Count}/{_moverCount})");
                }
            }

            var validationFailure = await ValidateFinalVisibility(
                ctx,
                pm,
                benchmarkRoots);
            if (validationFailure != null)
                return ScenarioResult.Fail(validationFailure);

            return ScenarioResult.Ok(
                BuildReport(perf, elapsedTicks, lagSum, lagMax, lagSamples));
        }
        finally
        {
            try
            {
                ServerLoadBenchmarkSignals.BroadcastCompleted();
            }
            finally
            {
                _visibilityController?.Dispose();
                _visibilityController = null;
                _deleteChurnController = null;
            }
        }
    }

    private List<PredictedObjectID> CollectBenchmarkRoots(PredictionManager pm)
    {
        var result = new List<PredictedObjectID>(_moverCount);
        CollectBenchmarkRoots(pm, result);
        return result;
    }

    private void CollectBenchmarkRoots(
        PredictionManager pm,
        List<PredictedObjectID> destination)
    {
        pm.TryGetPrefab(_moverPrefab, out var moverPrefabId);
        pm.TryGetPrefab(_passiveMoverPrefab, out var passivePrefabId);

        destination.Clear();
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (!record.isRootRecord ||
                (record.prefabId != moverPrefabId &&
                 record.prefabId != passivePrefabId))
            {
                continue;
            }

            destination.Add(record.rootId);
        }

        destination.Sort(CompareRootIds);
    }

    private static int CompareRootIds(
        PredictedObjectID left,
        PredictedObjectID right)
    {
        return left.instanceId.value.CompareTo(right.instanceId.value);
    }

    private void BroadcastExpectedMoverCounts(
        ScenarioContext ctx,
        int benchmarkRootCount)
    {
        var players = ctx.networkManager.players;
        var localPlayer = ctx.networkManager.localPlayer;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            bool unfilteredLocalHost =
                ctx.role == NetworkRole.Host && player == localPlayer;
            int expected = unfilteredLocalHost || _visibilityController == null
                ? benchmarkRootCount
                : _visibilityController.visibleCount;
            ServerLoadBenchmarkSignals.BroadcastExpectedMovers(player, expected);
        }
    }
    private async UniTask<string> ValidateFinalVisibility(
        ScenarioContext ctx,
        PredictionManager pm,
        List<PredictedObjectID> benchmarkRoots)
    {
        _visibilityValidatedPlayers = 0;
        var players = ctx.networkManager.players;
        var localPlayer = ctx.networkManager.localPlayer;

        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            bool unfilteredLocalHost =
                ctx.role == NetworkRole.Host && player == localPlayer;

            _expectedRootsScratch.Clear();
            if (unfilteredLocalHost || _visibilityController == null)
            {
                _expectedRootsScratch.AddRange(benchmarkRoots);
            }
            else if (!_visibilityController.CopyExpectedRoots(
                         player,
                         _expectedRootsScratch))
            {
                return $"no final visibility window exists for player {player}";
            }

            _expectedRootsScratch.Sort(CompareRootIds);
            var signature = ServerLoadBenchmarkSignals.ComputeRootSignature(
                _expectedRootsScratch);
            ServerLoadBenchmarkSignals.BroadcastExpectedFinalVisibility(
                player,
                signature);
        }

        try
        {
            await ScenarioBarrier.Wait(
                ctx,
                FinalVisibilityBarrierId,
                SpawnTimeout);
        }
        catch (TimeoutException)
        {
            return "timed out waiting for final visibility validation";
        }

        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (!ServerLoadBenchmarkSignals.TryGetExpectedFinalVisibility(
                    player,
                    out var expected))
            {
                return $"missing expected final visibility for player {player}";
            }

            if (!ServerLoadBenchmarkSignals.TryGetReportedFinalVisibility(
                    player,
                    out var actual))
            {
                return $"player {player} did not report final visibility";
            }

            if (!actual.Equals(expected))
            {
                return BuildVisibilityMismatchMessage(
                    $"player {player} final visibility mismatch",
                    expected,
                    actual);
            }

            _visibilityValidatedPlayers++;
        }

        return null;
    }

    private ServerLoadBenchmarkSignals.RootSignature
        ComputeCurrentMoverRootSignature(PredictionManager pm)
    {
        CollectBenchmarkRoots(pm, _signatureRootsScratch);
        return ServerLoadBenchmarkSignals.ComputeRootSignature(
            _signatureRootsScratch);
    }

    private static string BuildVisibilityMismatchMessage(
        string prefix,
        ServerLoadBenchmarkSignals.RootSignature expected,
        ServerLoadBenchmarkSignals.RootSignature actual)
    {
        return $"{prefix}: expected count={expected.count} " +
               $"fnv={expected.fnv:X16}, actual count={actual.count} " +
               $"fnv={actual.fnv:X16}";
    }

    private void OnWindowDataSent(Connection connection, ByteData data, bool asServer)
    {
        _windowDataSent += (ulong)data.length;
    }

    private void OnWindowDataReceived(Connection connection, ByteData data, bool asServer)
    {
        _windowDataReceived += (ulong)data.length;
    }

    private string BuildReport(
        ScenarioPerformanceDetails perf,
        ulong elapsedTicks,
        double lagSum,
        ulong lagMax,
        int lagSamples)
    {
        var sb = new StringBuilder();
        sb.Append("bench");
        sb.Append(" objects=").Append(_moverCount);
        sb.Append(" inputEvery=").Append(_inputEvery);
        sb.Append(" seconds=").Append(_benchSeconds.ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(" visibilityMode=").Append(GetVisibilityModeName(_visibilityMode));
        sb.Append(" visibilityPercent=").Append(
            _visibilityMode == BenchmarkVisibilityMode.None
                ? 100
                : _visibilityPercent);
        bool churnMode =
            _visibilityMode is BenchmarkVisibilityMode.Churn or
                BenchmarkVisibilityMode.AcquireChurn;
        sb.Append(" visibilityChurnPercent=").Append(
            churnMode ? _visibilityChurnPercent : 0);
        sb.Append(" visibilityCappedChurnPercent=").Append(
            churnMode && _visibilityController != null
                ? _visibilityController.cappedChurnPercent
                : 0);
        sb.Append(" visibilityChurnTicks=").Append(
            churnMode ? _visibilityChurnTicks : 0);
        sb.Append(" visibilityMutationCalls=")
            .Append(_visibilityController?.mutationCalls ?? 0);
        sb.Append(" visibilityAcquisitions=")
            .Append(_visibilityController?.acquisitions ?? 0);
        sb.Append(" visibilityReleases=")
            .Append(_visibilityController?.releases ?? 0);
        sb.Append(" visibilityActiveHandles=")
            .Append(_visibilityController?.activeHandles ?? 0);
        sb.Append(" visibilityEpochs=")
            .Append(_visibilityController?.epochs ?? 0);
        sb.Append(" deleteChurnPerSecond=").Append(_deleteChurnPerSecond);
        sb.Append(" deleteChurnDeletes=")
            .Append(_deleteChurnController?.deletes ?? 0);
        sb.Append(" deleteChurnRespawns=")
            .Append(_deleteChurnController?.respawns ?? 0);
        sb.Append(" deleteChurnRespawnFailures=")
            .Append(_deleteChurnController?.respawnFailures ?? 0);
        sb.Append(" ticks=").Append(elapsedTicks);
        sb.Append(" ackLagAvg=").Append((lagSamples > 0 ? lagSum / lagSamples : 0).ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(" ackLagMax=").Append(lagMax);
        sb.Append(" fullFrames=").Append(_windowFullFrames);
        sb.Append(" deltaFrames=").Append(_windowDeltaFrames);
        sb.Append(" deltaBytes=").Append(_windowDeltaBytes);
        sb.Append(" fullBytes=").Append(_windowFullBytes);
        sb.Append(" maxDeltaBytes=").Append(_windowMaxDeltaBytes);
        sb.Append(" deleteKB=").Append((_windowDeleteBits / 8 / 1024).ToString(CultureInfo.InvariantCulture));
        sb.Append(" hierarchyKB=").Append((_windowHierarchyBits / 8 / 1024).ToString(CultureInfo.InvariantCulture));
        sb.Append(" inputKB=").Append((_windowInputBits / 8 / 1024).ToString(CultureInfo.InvariantCulture));
        sb.Append(" stateKB=").Append((_windowStateBits / 8 / 1024).ToString(CultureInfo.InvariantCulture));
        sb.Append(" windowDataSent=").Append(_windowDataSent);
        sb.Append(" windowDataReceived=").Append(_windowDataReceived);
        sb.Append(" visibilityValidatedPlayers=")
            .Append(_visibilityValidatedPlayers);

        AppendMarkerSections(sb, perf, elapsedTicks);

        return sb.ToString();
    }

    private string BuildClientReport(
        ScenarioPerformanceDetails perf,
        ulong elapsedTicks,
        long frameApplies,
        float windowSeconds)
    {
        var sb = new StringBuilder();
        sb.Append("clientBench");
        sb.Append(" objects=").Append(_moverCount);
        sb.Append(" ticks=").Append(elapsedTicks);
        sb.Append(" frameApplies=").Append(frameApplies);
        sb.Append(" framesPerSecond=").Append(
            (windowSeconds > 0 ? frameApplies / windowSeconds : 0)
            .ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(" framesReceived=").Append(_clientFramesReceived);
        sb.Append(" fullFramesReceived=").Append(_clientFullFramesReceived);
        sb.Append(" renderApplies=").Append(_clientRenderApplies);
        sb.Append(" tickApplies=").Append(_clientTickApplies);
        sb.Append(" maxApplyAge=").Append(_clientMaxApplyAge);
        AppendMarkerSections(sb, perf, elapsedTicks);
        return sb.ToString();
    }

    private static void AppendMarkerSections(
        StringBuilder sb,
        ScenarioPerformanceDetails perf,
        ulong elapsedTicks)
    {
        if (perf.markers == null)
            return;

        for (var i = 0; i < perf.markers.Length; i++)
        {
            var marker = perf.markers[i];
            if (marker.sampleBlockCount == 0)
                continue;

            var shortName = marker.name.StartsWith("PredictionManager.")
                ? marker.name["PredictionManager.".Length..]
                : marker.name;

            sb.Append(" | ").Append(shortName);
            sb.Append(" totalMs=").Append(marker.elapsedMilliseconds.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(" blocks=").Append(marker.sampleBlockCount);
            sb.Append(" perTickUs=").Append(
                (elapsedTicks > 0 ? marker.elapsedNanoseconds / 1000.0 / elapsedTicks : 0)
                .ToString("0.##", CultureInfo.InvariantCulture));
        }
    }

    private static bool TryParseVisibilityMode(
        string value,
        out BenchmarkVisibilityMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "none":
            case "default":
            case "off":
                mode = BenchmarkVisibilityMode.None;
                return true;
            case "static":
            case "stable":
                mode = BenchmarkVisibilityMode.Static;
                return true;
            case "churn":
                mode = BenchmarkVisibilityMode.Churn;
                return true;
            case "acquire-churn":
            case "acquire":
            case "lease-churn":
                mode = BenchmarkVisibilityMode.AcquireChurn;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static string GetVisibilityModeName(BenchmarkVisibilityMode mode)
    {
        return mode switch
        {
            BenchmarkVisibilityMode.None => "none",
            BenchmarkVisibilityMode.Static => "static",
            BenchmarkVisibilityMode.Churn => "churn",
            BenchmarkVisibilityMode.AcquireChurn => "acquire-churn",
            _ => "unknown"
        };
    }

    private enum BenchmarkVisibilityMode
    {
        None,
        Static,
        Churn,
        AcquireChurn
    }
    private sealed class BenchmarkVisibilityController : IDisposable
    {
        readonly PredictionManager _manager;
        readonly BenchmarkVisibilityMode _mode;
        readonly List<PredictedObjectID> _roots;
        readonly List<PlayerWindow> _players = new();
        readonly int _swapCount;
        readonly ulong _churnTicks;

        ulong _measurementStartTick;
        ulong _lastEpoch;
        bool _measurementStarted;
        bool _disposed;

        public int visibleCount { get; }
        public int cappedChurnPercent { get; }
        public long mutationCalls { get; private set; }
        public long acquisitions { get; private set; }
        public long releases { get; private set; }
        public long activeHandles { get; private set; }
        public long epochs { get; private set; }

        public BenchmarkVisibilityController(
            PredictionManager manager,
            BenchmarkVisibilityMode mode,
            List<PredictedObjectID> roots,
            int visibilityPercent,
            int churnPercent,
            int churnTicks)
        {
            _manager = manager;
            _mode = mode;
            _roots = roots;
            _churnTicks = (ulong)churnTicks;
            visibleCount = roots.Count * visibilityPercent / 100;

            int hiddenCount = roots.Count - visibleCount;
            int requestedSwap = roots.Count * churnPercent / 200;
            _swapCount = Math.Min(
                requestedSwap,
                Math.Min(visibleCount, hiddenCount));
            cappedChurnPercent = roots.Count == 0
                ? 0
                : _swapCount * 200 / roots.Count;
        }

        public void ApplyInitial(
            IReadOnlyList<PlayerID> players,
            PlayerID? excludedPlayer)
        {
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (excludedPlayer.HasValue && player == excludedPlayer.Value)
                    continue;

                var window = new PlayerWindow(
                    player,
                    BuildPlayerOrder(player));
                _players.Add(window);

                if (_mode == BenchmarkVisibilityMode.AcquireChurn)
                    ApplyInitialAcquiredWindow(window);
                else
                    ApplyInitialDirectWindow(window);
            }
        }

        public void BeginMeasurement(ulong tick)
        {
            _measurementStartTick = tick;
            _lastEpoch = 0;
            _measurementStarted = true;
            mutationCalls = 0;
            acquisitions = 0;
            releases = 0;
            epochs = 0;
        }

        public void Update(ulong tick)
        {
            if (!_measurementStarted ||
                _swapCount == 0 ||
                _mode is not (BenchmarkVisibilityMode.Churn or
                    BenchmarkVisibilityMode.AcquireChurn))
            {
                return;
            }

            ulong elapsed = tick >= _measurementStartTick
                ? tick - _measurementStartTick
                : 0;
            ulong targetEpoch = elapsed / _churnTicks;
            while (_lastEpoch < targetEpoch)
            {
                AdvanceOneEpoch();
                _lastEpoch++;
                epochs++;
            }
        }

        void ApplyInitialDirectWindow(PlayerWindow window)
        {
            for (var i = visibleCount; i < window.order.Length; i++)
                Hide(window.player, window.order[i]);
        }

        void ApplyInitialAcquiredWindow(PlayerWindow window)
        {
            for (var i = 0; i < window.order.Length; i++)
                Hide(window.player, window.order[i]);
            for (var i = 0; i < visibleCount; i++)
                Acquire(window, window.order[i]);
        }

        void AdvanceOneEpoch()
        {
            for (var playerIndex = 0; playerIndex < _players.Count; playerIndex++)
            {
                var window = _players[playerIndex];
                int count = window.order.Length;

                for (var i = 0; i < _swapCount; i++)
                {
                    var leaving = window.order[(window.start + i) % count];
                    var entering = window.order[
                        (window.start + visibleCount + i) % count];

                    if (_mode == BenchmarkVisibilityMode.AcquireChurn)
                    {
                        Release(window, leaving);
                        Acquire(window, entering);
                    }
                    else
                    {
                        Hide(window.player, leaving);
                        Show(window.player, entering);
                    }
                }

                window.start = (window.start + _swapCount) % count;
            }
        }

        PredictedObjectID[] BuildPlayerOrder(PlayerID player)
        {
            var order = new PredictedObjectID[_roots.Count];
            if (order.Length == 0)
                return order;

            int offset = (int)(Mix(player.id.value) % (ulong)order.Length);
            for (var i = 0; i < order.Length; i++)
                order[i] = _roots[(i + offset) % order.Length];
            return order;
        }

        void Hide(PlayerID player, PredictedObjectID rootId)
        {
            _manager.HideFrom(player, rootId);
            mutationCalls++;
        }

        void Show(PlayerID player, PredictedObjectID rootId)
        {
            _manager.ShowTo(player, rootId);
            mutationCalls++;
        }

        void Acquire(PlayerWindow window, PredictedObjectID rootId)
        {
            window.handles.Add(
                rootId,
                _manager.AcquireVisibility(window.player, rootId));
            acquisitions++;
            activeHandles++;
        }

        void Release(PlayerWindow window, PredictedObjectID rootId)
        {
            if (!window.handles.Remove(rootId, out var handle))
                return;

            handle.Dispose();
            releases++;
            activeHandles--;
        }

        public bool CopyExpectedRoots(
            PlayerID player,
            List<PredictedObjectID> destination)
        {
            destination.Clear();
            for (var playerIndex = 0; playerIndex < _players.Count; playerIndex++)
            {
                var window = _players[playerIndex];
                if (window.player != player)
                    continue;

                for (var i = 0; i < visibleCount; i++)
                {
                    destination.Add(
                        window.order[(window.start + i) % window.order.Length]);
                }

                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            for (var playerIndex = 0; playerIndex < _players.Count; playerIndex++)
            {
                var window = _players[playerIndex];
                foreach (var handle in window.handles.Values)
                    handle.Dispose();
                window.handles.Clear();

                for (var rootIndex = 0; rootIndex < _roots.Count; rootIndex++)
                    _manager.ShowTo(window.player, _roots[rootIndex]);
            }

            activeHandles = 0;
            _players.Clear();
        }

        static ulong Mix(ulong value)
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }

        sealed class PlayerWindow
        {
            public readonly PlayerID player;
            public readonly PredictedObjectID[] order;
            public readonly Dictionary<PredictedObjectID, IDisposable> handles = new();
            public int start;

            public PlayerWindow(
                PlayerID player,
                PredictedObjectID[] order)
            {
                this.player = player;
                this.order = order;
            }
        }
    }

    private sealed class BenchmarkDeleteChurnController
    {
        readonly PredictionManager _manager;
        readonly GameObject _moverPrefab;
        readonly GameObject _passiveMoverPrefab;
        readonly int _perSecond;
        readonly Queue<ChurnEntry> _rotation = new();

        float _startTime;
        long _performedSteps;
        int _spawnCursor;

        public int rotationCount => _rotation.Count;
        public long deletes { get; private set; }
        public long respawns { get; private set; }
        public long respawnFailures { get; private set; }

        struct ChurnEntry
        {
            public PredictedObjectID rootId;
            public bool passive;
        }

        public BenchmarkDeleteChurnController(
            PredictionManager manager,
            GameObject moverPrefab,
            GameObject passiveMoverPrefab,
            int perSecond,
            int spawnCursorStart)
        {
            _manager = manager;
            _moverPrefab = moverPrefab;
            _passiveMoverPrefab = passiveMoverPrefab;
            _perSecond = perSecond;
            _spawnCursor = spawnCursorStart;

            manager.TryGetPrefab(moverPrefab, out var moverPrefabId);
            manager.TryGetPrefab(passiveMoverPrefab, out var passivePrefabId);

            ref var state = ref manager.hierarchy.currentState;
            for (var i = 0; i < state.spawnedPrefabs.Count; i++)
            {
                var record = state.spawnedPrefabs[i];
                if (!record.isRootRecord)
                    continue;

                if (record.prefabId == moverPrefabId)
                {
                    _rotation.Enqueue(new ChurnEntry
                    {
                        rootId = record.rootId,
                        passive = false
                    });
                }
                else if (record.prefabId == passivePrefabId)
                {
                    _rotation.Enqueue(new ChurnEntry
                    {
                        rootId = record.rootId,
                        passive = true
                    });
                }
            }
        }

        public void BeginMeasurement(float now)
        {
            _startTime = now;
            _performedSteps = 0;
            deletes = 0;
            respawns = 0;
            respawnFailures = 0;
        }

        public void Update(float now)
        {
            if (_rotation.Count == 0)
                return;

            long targetSteps = (long)((now - _startTime) * _perSecond);
            while (_performedSteps < targetSteps && _rotation.Count > 0)
            {
                StepOnce();
                _performedSteps++;
            }
        }

        void StepOnce()
        {
            var entry = _rotation.Dequeue();
            _manager.hierarchy.Delete(entry.rootId);
            deletes++;

            var prefab = entry.passive ? _passiveMoverPrefab : _moverPrefab;
            var lane = _spawnCursor % 20;
            var row = _spawnCursor / 20;
            _spawnCursor++;

            var created = _manager.hierarchy.Create(
                prefab,
                new Vector3(lane * 3f, 0f, row * 3f),
                Quaternion.identity);
            if (created.HasValue)
            {
                _rotation.Enqueue(new ChurnEntry
                {
                    rootId = created.Value,
                    passive = entry.passive
                });
                respawns++;
            }
            else
            {
                respawnFailures++;
            }
        }
    }
}
public static class ServerLoadBenchmarkSignals
{
    private const ulong FnvOffsetBasis = 14_695_981_039_346_656_037UL;
    private const ulong FnvPrime = 1_099_511_628_211UL;

    private static readonly Dictionary<PlayerID, int> _expectedMovers = new();
    private static readonly Dictionary<PlayerID, RootSignature>
        _expectedFinalVisibility = new();
    private static readonly Dictionary<PlayerID, RootSignature>
        _reportedFinalVisibility = new();

    public static bool started { get; private set; }
    public static bool completed { get; private set; }

    public static void Reset()
    {
        _expectedMovers.Clear();
        _expectedFinalVisibility.Clear();
        _reportedFinalVisibility.Clear();
        started = false;
        completed = false;
    }

    public static bool TryGetExpectedMovers(PlayerID player, out int count)
    {
        return _expectedMovers.TryGetValue(player, out count);
    }

    public static void BroadcastExpectedMovers(PlayerID player, int count)
    {
        BroadcastExpectedMoversRpc(player, count);
    }

    public static RootSignature ComputeRootSignature(
        IReadOnlyList<PredictedObjectID> sortedRoots)
    {
        ulong hash = FnvOffsetBasis;
        for (var i = 0; i < sortedRoots.Count; i++)
        {
            uint value = sortedRoots[i].instanceId.value;
            for (var byteIndex = 0; byteIndex < sizeof(uint); byteIndex++)
            {
                hash ^= (byte)(value >> (byteIndex * 8));
                hash *= FnvPrime;
            }
        }

        return new RootSignature(sortedRoots.Count, hash);
    }

    public static void BroadcastExpectedFinalVisibility(
        PlayerID player,
        RootSignature signature)
    {
        BroadcastExpectedFinalVisibilityRpc(
            player,
            signature.count,
            signature.fnv);
    }

    public static bool TryGetExpectedFinalVisibility(
        PlayerID player,
        out RootSignature signature)
    {
        return _expectedFinalVisibility.TryGetValue(player, out signature);
    }

    public static void ReportFinalVisibility(RootSignature signature)
    {
        ReportFinalVisibilityRpc(signature.count, signature.fnv);
    }

    public static bool TryGetReportedFinalVisibility(
        PlayerID player,
        out RootSignature signature)
    {
        return _reportedFinalVisibility.TryGetValue(player, out signature);
    }

    public static void BroadcastStarted()
    {
        BroadcastStartedRpc();
    }

    public static void BroadcastCompleted()
    {
        BroadcastCompletedRpc();
    }

    [ObserversRpc(runLocally: true)]
    private static void BroadcastExpectedMoversRpc(PlayerID player, int count)
    {
        _expectedMovers[player] = count;
    }

    [ObserversRpc(runLocally: true)]
    private static void BroadcastExpectedFinalVisibilityRpc(
        PlayerID player,
        int count,
        ulong fnv)
    {
        _expectedFinalVisibility[player] = new RootSignature(count, fnv);
    }

    [ServerRpc(requireOwnership: false)]
    private static void ReportFinalVisibilityRpc(
        int count,
        ulong fnv,
        RPCInfo info = default)
    {
        _reportedFinalVisibility[info.sender] = new RootSignature(count, fnv);
    }

    [ObserversRpc(runLocally: true)]
    private static void BroadcastStartedRpc()
    {
        started = true;
    }

    [ObserversRpc(runLocally: true)]
    private static void BroadcastCompletedRpc()
    {
        completed = true;
    }

    public readonly struct RootSignature : IEquatable<RootSignature>
    {
        public readonly int count;
        public readonly ulong fnv;

        public RootSignature(int count, ulong fnv)
        {
            this.count = count;
            this.fnv = fnv;
        }

        public bool Equals(RootSignature other)
        {
            return count == other.count && fnv == other.fnv;
        }

        public override bool Equals(object obj)
        {
            return obj is RootSignature other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(count, fnv);
        }
    }
}
