using System;
using System.Globalization;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Prediction.Profiler;
using UnityEngine;

public class ServerLoadBenchmarkScenario : Scenario
{
    private const float SpawnTimeout = 120f;

    private GameObject _moverPrefab;
    private GameObject _passiveMoverPrefab;
    private GameObject _driverPrefab;
    private int _moverCount = 200;
    private int _inputEvery = 1;
    private float _benchSeconds = 20f;
    private float _settleSeconds = 3f;

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

        Application.targetFrameRate = 60;

        _moverPrefab = PredictionTestUtils.CreatePrefab<BenchMover>("BenchMover");
        PredictionTestUtils.RegisterPrefab(ctx, _moverPrefab);

        _passiveMoverPrefab = PredictionTestUtils.CreatePrefab<BenchPassiveMover>("BenchPassiveMover");
        PredictionTestUtils.RegisterPrefab(ctx, _passiveMoverPrefab);

        _driverPrefab = PredictionTestUtils.CreatePrefab<BenchDriver>("BenchDriver");
        var driver = _driverPrefab.GetComponent<BenchDriver>();
        driver.moverPrefab = _moverPrefab;
        driver.passiveMoverPrefab = _passiveMoverPrefab;
        driver.targetCount = _moverCount;
        driver.inputEvery = _inputEvery;
        PredictionTestUtils.RegisterPrefab(ctx, _driverPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (ctx.role == NetworkRole.Host)
        {
            var clientTask = Client(ctx);
            var serverTask = Server(ctx);
            var (clientResult, serverResult) = await UniTask.WhenAll(clientTask, serverTask);
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
                $"movers never spawned on client ({CountMovers(pm)}/{_moverCount})");
        }

        await UniTask.WaitForSeconds(_settleSeconds + _benchSeconds, cancellationToken: ctx.cancellationToken);
        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> Server(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;

        if (!pm.hierarchy.Create(_driverPrefab).HasValue)
            return ScenarioResult.Fail("failed to create bench driver");

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

        var sampler = ScenarioPerformanceSampler.StartDefault();
        using var bandwidth = new BandwidthSampler();
        var startTick = pm.localTick;
        double lagSum = 0;
        ulong lagMax = 0;
        int lagSamples = 0;
        float start = Time.realtimeSinceStartup;

        try
        {
            while (Time.realtimeSinceStartup - start < _benchSeconds)
            {
                await UniTask.NextFrame(ctx.cancellationToken);
                sampler.SampleFrame(pm);

                var lag = pm.lastMaxAckLagTicks;
                lagSum += lag;
                if (lag > lagMax)
                    lagMax = lag;
                lagSamples++;
            }

            var elapsedTicks = pm.localTick - startTick;
            var perf = sampler.Stop(pm);
            return ScenarioResult.Ok(BuildReport(perf, bandwidth, elapsedTicks, lagSum, lagMax, lagSamples));
        }
        finally
        {
            sampler.Dispose();
        }
    }

    private string BuildReport(
        ScenarioPerformanceDetails perf,
        BandwidthSampler bandwidth,
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
        sb.Append(" ticks=").Append(elapsedTicks);
        sb.Append(" ackLagAvg=").Append((lagSamples > 0 ? lagSum / lagSamples : 0).ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(" ackLagMax=").Append(lagMax);
        sb.Append(" bandwidthTicks=").Append(bandwidth.tickCount);
        sb.Append(" bandwidthClients=").Append(bandwidth.clientCount);
        sb.Append(" bandwidthFrames=").Append(bandwidth.frameCount);
        sb.Append(" bandwidthBytes=").Append(bandwidth.byteCount);
        sb.Append(" bytesPerClientTick=").Append(
            bandwidth.bytesPerClientTick.ToString("0.##", CultureInfo.InvariantCulture));

        if (perf.markers != null)
        {
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

        return sb.ToString();
    }

    private sealed class BandwidthSampler : IDisposable
    {
        private readonly System.Collections.Generic.HashSet<PlayerID> _players = new();
        private long _bitCount;

        public int tickCount { get; private set; }
        public int frameCount { get; private set; }
        public int clientCount => _players.Count;
        public long byteCount => _bitCount / 8;
        public double bytesPerClientTick => tickCount > 0 && clientCount > 0
            ? _bitCount / 8.0 / tickCount / clientCount
            : 0;

        public BandwidthSampler()
        {
            TickBandwidthProfiler.onTickEnded += OnTickEnded;
        }

        public void Dispose()
        {
            TickBandwidthProfiler.onTickEnded -= OnTickEnded;
        }

        private void OnTickEnded()
        {
            var frames = TickBandwidthProfiler.wroteFrames;
            if (frames.Count == 0)
                return;

            tickCount++;

            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                _players.Add(frame.player);
                _bitCount += frame.bitCount;
                frameCount++;
            }
        }
    }
}
