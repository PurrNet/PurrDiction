using System;
using System.Globalization;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;

public class InputBandwidthScenario : Scenario
{
    private const float SpawnTimeout = 120f;
    private const int ReadyBarrierId = 21_001;
    private const int DoneBarrierId = 21_002;

    private GameObject _pawnPrefab;
    private bool _useHalfVectors;
    private float _benchSeconds = 15f;
    private float _settleSeconds = 3f;
    private string _configurationError;

    private ulong _windowDataSent;
    private ulong _windowDataReceived;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        if (CommandLineUtils.TryGetArgument("-ibSeconds", out var seconds)
            && float.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
            && parsedSeconds > 0)
            _benchSeconds = parsedSeconds;

        InputBandwidthPawn.noiseMode = InputBandwidthPawn.NoiseMode.Full;
        if (CommandLineUtils.TryGetArgument("-ibNoise", out var noise))
        {
            switch (noise.Trim().ToLowerInvariant())
            {
                case "constant":
                    InputBandwidthPawn.noiseMode = InputBandwidthPawn.NoiseMode.Constant;
                    break;
                case "smooth":
                    InputBandwidthPawn.noiseMode = InputBandwidthPawn.NoiseMode.Smooth;
                    break;
                case "full":
                    InputBandwidthPawn.noiseMode = InputBandwidthPawn.NoiseMode.Full;
                    break;
                default:
                    _configurationError = $"unknown -ibNoise '{noise}' (expected constant, smooth, or full)";
                    break;
            }
        }

        _useHalfVectors = CommandLineUtils.HasFlag("-ibHalf");

        Application.targetFrameRate = 60;

        _pawnPrefab = _useHalfVectors
            ? PredictionTestUtils.CreatePrefab<InputBandwidthPawnHalf>("InputBandwidthPawnHalf")
            : PredictionTestUtils.CreatePrefab<InputBandwidthPawn>("InputBandwidthPawn");
        PredictionTestUtils.RegisterPrefab(ctx, _pawnPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (_configurationError != null)
            return ScenarioResult.Fail(_configurationError);

        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_pawnPrefab, out var pawnPrefabId);
        int expectedPawns = ctx.expectedConnections;

        if (ctx.isServer)
        {
            var players = ctx.networkManager.players;
            for (var i = 0; i < players.Count; i++)
            {
                var spawnPos = new Vector3(i * 3f, 0f, 0f);
                var created = pm.hierarchy.Create(_pawnPrefab, spawnPos, Quaternion.identity, players[i]);
                if (!created.HasValue)
                    return ScenarioResult.Fail($"failed to create pawn for player {players[i]}");
            }
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PredictionTestUtils.CountInstances(pm, pawnPrefabId) >= expectedPawns,
                SpawnTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"pawns never spawned ({PredictionTestUtils.CountInstances(pm, pawnPrefabId)}/{expectedPawns})");
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: ctx.cancellationToken);

        try
        {
            await ScenarioBarrier.Wait(ctx, ReadyBarrierId, SpawnTimeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out synchronizing benchmark start");
        }

        _windowDataSent = 0;
        _windowDataReceived = 0;
        var transport = ctx.networkManager.rawTransport;
        if (transport != null)
        {
            transport.onDataSent += OnWindowDataSent;
            transport.onDataReceived += OnWindowDataReceived;
        }

        var startTick = pm.localTick;
        var startInputSends = pm.inputSendsTotal;
        var startInputBytes = pm.inputBytesSentTotal;
        var startInputTicks = pm.inputTicksSentTotal;
        var startFramesReceived = pm.framesReceivedTotal;
        var startDeltaFrames = pm.deltaFramesWrittenTotal;
        var startDeltaBytes = pm.deltaFrameBytesTotal;
        var startFullBytes = pm.fullFrameBytesTotal;
        var startDeleteBits = pm.deltaSectionDeleteBitsTotal;
        var startHierarchyBits = pm.deltaSectionHierarchyBitsTotal;
        var startInputSectionBits = pm.deltaSectionInputBitsTotal;
        var startStateBits = pm.deltaSectionStateBitsTotal;
        double ackLagSum = 0;
        ulong ackLagMax = 0;
        int ackLagSamples = 0;

        float start = Time.realtimeSinceStartup;
        float windowSeconds;

        try
        {
            while (Time.realtimeSinceStartup - start < _benchSeconds)
            {
                await UniTask.NextFrame(ctx.cancellationToken);

                if (ctx.isServer)
                {
                    var lag = pm.lastMaxAckLagTicks;
                    ackLagSum += lag;
                    if (lag > ackLagMax)
                        ackLagMax = lag;
                    ackLagSamples++;
                }
            }
        }
        finally
        {
            windowSeconds = Time.realtimeSinceStartup - start;
            if (transport != null)
            {
                transport.onDataSent -= OnWindowDataSent;
                transport.onDataReceived -= OnWindowDataReceived;
            }
        }

        var elapsedTicks = pm.localTick - startTick;
        var inputSends = pm.inputSendsTotal - startInputSends;
        var inputBytes = pm.inputBytesSentTotal - startInputBytes;
        var inputTicks = pm.inputTicksSentTotal - startInputTicks;
        var framesReceived = pm.framesReceivedTotal - startFramesReceived;
        var deltaFrames = pm.deltaFramesWrittenTotal - startDeltaFrames;
        var deltaBytes = pm.deltaFrameBytesTotal - startDeltaBytes;
        var fullBytes = pm.fullFrameBytesTotal - startFullBytes;
        var deleteBits = pm.deltaSectionDeleteBitsTotal - startDeleteBits;
        var hierarchyBits = pm.deltaSectionHierarchyBitsTotal - startHierarchyBits;
        var inputSectionBits = pm.deltaSectionInputBitsTotal - startInputSectionBits;
        var stateBits = pm.deltaSectionStateBitsTotal - startStateBits;

        try
        {
            await ScenarioBarrier.Wait(ctx, DoneBarrierId, SpawnTimeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out synchronizing benchmark end");
        }

        if (elapsedTicks == 0)
            return ScenarioResult.Fail("no ticks elapsed during the benchmark window");

        if (!ctx.isServer && framesReceived == 0)
            return ScenarioResult.Fail("client received no frames during the benchmark window");

        if (CommandLineUtils.HasFlag("-ibAssert") &&
            ctx.role == NetworkRole.Client &&
            InputBandwidthPawn.noiseMode == InputBandwidthPawn.NoiseMode.Constant &&
            !_useHalfVectors)
        {
            double bytesPerWindowTick = inputTicks > 0 ? (double)inputBytes / inputTicks : 0;
            double windowAvg = inputSends > 0 ? (double)inputTicks / inputSends : 0;
            ulong redundancy = pm.inputRedundancyTickCount;

            if (bytesPerWindowTick > 12.0)
            {
                return ScenarioResult.Fail(
                    "upload dedup budget exceeded: " +
                    bytesPerWindowTick.ToString("0.#", CultureInfo.InvariantCulture) +
                    " bytes/window-tick with constant inputs (budget 12; repeat bits broken?)");
            }

            if (windowAvg < redundancy - 1 || windowAvg > redundancy + 2)
            {
                return ScenarioResult.Fail(
                    "upload window out of band: avg " +
                    windowAvg.ToString("0.##", CultureInfo.InvariantCulture) +
                    $" ticks vs redundancy {redundancy} (cap broken or ack stalled)");
            }
        }

        var sb = new StringBuilder();
        sb.Append("inputBench");
        sb.Append(" role=").Append(ctx.role);
        sb.Append(" players=").Append(expectedPawns);
        sb.Append(" noise=").Append(InputBandwidthPawn.noiseMode);
        sb.Append(" vectors=").Append(_useHalfVectors ? "half" : "full");
        sb.Append(" tickRate=").Append(pm.tickRate);
        sb.Append(" inputSystems=").Append(pm.inputHistorySystems);
        sb.Append(" redundancyTicks=").Append(pm.inputRedundancyTickCount);
        sb.Append(" seconds=").Append(windowSeconds.ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(" ticks=").Append(elapsedTicks);
        AppendRate(sb, " txKBs=", _windowDataSent, windowSeconds);
        AppendRate(sb, " rxKBs=", _windowDataReceived, windowSeconds);

        if (ctx.role != NetworkRole.Server)
        {
            sb.Append(" inputSends=").Append(inputSends);
            AppendRate(sb, " inputPayloadKBs=", inputBytes, windowSeconds);
            sb.Append(" inputWindowAvgTicks=").Append(
                (inputSends > 0 ? (double)inputTicks / inputSends : 0)
                .ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(" inputBytesPerWindowTick=").Append(
                (inputTicks > 0 ? (double)inputBytes / inputTicks : 0)
                .ToString("0.#", CultureInfo.InvariantCulture));
            sb.Append(" framesReceived=").Append(framesReceived);
        }

        if (ctx.isServer)
        {
            sb.Append(" ackLagAvg=").Append(
                (ackLagSamples > 0 ? ackLagSum / ackLagSamples : 0)
                .ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(" ackLagMax=").Append(ackLagMax);
            sb.Append(" deltaFrames=").Append(deltaFrames);
            AppendRate(sb, " deltaKBs=", deltaBytes, windowSeconds);
            AppendRate(sb, " fullKBs=", fullBytes, windowSeconds);
            AppendRate(sb, " sectionInputKBs=", inputSectionBits / 8, windowSeconds);
            AppendRate(sb, " sectionStateKBs=", stateBits / 8, windowSeconds);
            AppendRate(sb, " sectionHierarchyKBs=", hierarchyBits / 8, windowSeconds);
            AppendRate(sb, " sectionDeleteKBs=", deleteBits / 8, windowSeconds);
            ulong frameBytes = deltaBytes + fullBytes;
            sb.Append(" inputSectionShare=").Append(
                (frameBytes > 0 ? inputSectionBits / 8.0 / frameBytes : 0)
                .ToString("0.###", CultureInfo.InvariantCulture));
        }

        return ScenarioResult.Ok(sb.ToString());
    }

    private static void AppendRate(StringBuilder sb, string label, ulong bytes, float seconds)
    {
        sb.Append(label).Append(
            (seconds > 0 ? bytes / 1024.0 / seconds : 0)
            .ToString("0.##", CultureInfo.InvariantCulture));
    }

    private void OnWindowDataSent(Connection connection, ByteData data, bool asServer)
    {
        _windowDataSent += (ulong)data.length;
    }

    private void OnWindowDataReceived(Connection connection, ByteData data, bool asServer)
    {
        _windowDataReceived += (ulong)data.length;
    }
}
