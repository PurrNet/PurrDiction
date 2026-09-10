using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>Dedicated-server FULL prediction benchmark. No validation work is sampled.</summary>
public sealed class FullPredictionPhysicsBenchmarkScenario : Scenario
{
    private const int ReadyBarrier = 31001;
    private const int SampledBarrier = 31002;
    private const int ValidatedBarrier = 31003;
    private const float Timeout = 120f;
    private GameObject _bodyPrefab;
    private int _bodiesPerPlayer;
    private int _sharedBodies;
    private int _totalBodies;
    private float _seconds;
    private float _settleSeconds;
    private double _reconcileMs;
    private int _eventMask;
    private ulong _startReliable, _startFull, _startReceived, _startFullReceived;
    private ulong _startDeltaFrames, _startDeltaBytes, _startFullBytes;
    private string _metricsPath;
    private string _configurationError;
    private readonly List<FullPredictionBenchmarkBody> _bodies = new();
    private readonly List<PredictedRigidbody> _rigidbodies = new();
    private readonly List<PredictedTransform> _transforms = new();
    private readonly HashSet<ulong> _observedValidationTicks = new();
    private FullPredictionBenchmarkReport _report;
    private PredictionManager _world;
    private bool _started;
    private bool _ended;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        FullPredictionBenchmarkSignals.Reset();
        _observedValidationTicks.Clear();
        _bodiesPerPlayer = ReadInt("-fpBodiesPerPlayer", 8, 1);
        _sharedBodies = ReadInt("-fpSharedBodies", 16, 0);
        _totalBodies = ReadInt("-fpTotalBodies", ctx.expectedConnections * _bodiesPerPlayer + _sharedBodies, 1);
        _seconds = ReadFloat("-fpSeconds", 10f, 0.1f);
        _settleSeconds = ReadFloat("-fpSettleSeconds", 3f, 0f);
        _reconcileMs = ReadFloat("-fpReconcileMs", 0f, 0f);
        _eventMask = ReadInt("-fpEventMask", 0, 0);
        if (_eventMask > 127)
            _configurationError = "-fpEventMask must be between 0 and 127";
        CommandLineUtils.TryGetArgument("-fpMetrics", out _metricsPath);
        if (_totalBodies - _sharedBodies < ctx.expectedConnections)
            _configurationError = "-fpTotalBodies must leave at least one owned body per client after shared bodies";
        if (ctx.role == NetworkRole.Host)
            _configurationError = "FULL physics benchmark requires a dedicated server and separate client processes";

        Application.targetFrameRate = 60;
        typeof(PredictionManager).GetField("_physicsProvider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(ctx.predictionManager, PredictionPhysicsProvider.UnityPhysics3D);
        Physics.simulationMode = SimulationMode.Script;

        float half = Mathf.Ceil(Mathf.Sqrt(_totalBodies)) * 0.44f + 1f;
        CreateWall("FP floor", new Vector3(FullPredictionBenchmarkBody.ArenaX, -0.5f, 0f), new Vector3(half * 2 + 2f, 1f, half * 2 + 2f));
        CreateWall("FP left", new Vector3(FullPredictionBenchmarkBody.ArenaX - half, 1.5f, 0f), new Vector3(1f, 4f, half * 2));
        CreateWall("FP right", new Vector3(FullPredictionBenchmarkBody.ArenaX + half, 1.5f, 0f), new Vector3(1f, 4f, half * 2));
        CreateWall("FP back", new Vector3(FullPredictionBenchmarkBody.ArenaX, 1.5f, -half), new Vector3(half * 2, 4f, 1f));
        CreateWall("FP front", new Vector3(FullPredictionBenchmarkBody.ArenaX, 1.5f, half), new Vector3(half * 2, 4f, 1f));

        _bodyPrefab = new GameObject("FullPredictionBenchmarkBody");
        _bodyPrefab.SetActive(false);
        var rb = _bodyPrefab.AddComponent<Rigidbody>();
        rb.mass = 1f;
        rb.linearDamping = 0.4f;
        rb.angularDamping = 0.4f;
        rb.sleepThreshold = 0f;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _bodyPrefab.AddComponent<BoxCollider>().size = Vector3.one * 0.8f;
        var predictedTransform = _bodyPrefab.AddComponent<PredictedTransform>();
        predictedTransform.SetPredictionPolicyOverride(PredictionPolicy.FullPrediction);
        var predictedRigidbody = _bodyPrefab.AddComponent<PredictedRigidbody>();
        predictedRigidbody.SetPredictionPolicyOverride(PredictionPolicy.FullPrediction);
        typeof(PredictedTransform).GetField("_floatAccuracy", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(predictedTransform, FloatAccuracy.Purrfect);
        typeof(PredictedRigidbody).GetField("_floatAccuracy", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(predictedRigidbody, FloatAccuracy.Purrfect);
        typeof(PredictedRigidbody).GetField("_eventMask", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(predictedRigidbody, (PhysicsEventMask)_eventMask);
        _bodyPrefab.AddComponent<FullPredictionBenchmarkBody>().SetPredictionPolicyOverride(PredictionPolicy.FullPrediction);
        PredictionTestUtils.RegisterPrefab(ctx, _bodyPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        _world = ctx.predictionManager;
        _report = new FullPredictionBenchmarkReport
        {
            role = ctx.role.ToString(), clients = ctx.expectedConnections,
            bodiesPerPlayer = _bodiesPerPlayer, sharedBodies = _sharedBodies, totalBodies = _totalBodies,
            predictedEventMask = _eventMask,
            requestedSeconds = _seconds, settleSeconds = _settleSeconds, reconcileIntervalMs = _reconcileMs,
            tickRate = _world.tickRate, policy = "FullPrediction", physicsProvider = "UnityPhysics3D",
            stateAccuracy = "Purrfect", positionTolerance = 0.00001f,
            velocityTolerance = 0.00001f, rotationToleranceDegrees = 0.05f
        };
        ScenarioResult result;
        try
        {
            result = _configurationError != null ? ScenarioResult.Fail(_configurationError) : await Run(ctx);
        }
        catch (Exception exception)
        {
            result = ScenarioResult.Fail(exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            ctx.networkManager.tickModule.onPostTick -= OnPostTick;
            _world.onBeforePhysicsPass -= ObserveValidationTick;
            if (_started && !_ended)
                EndSampling();
            FullPredictionBenchmarkBody.sampling = false;
            PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
        }
        _report.success = result.success;
        _report.message = result.message;
        if (!string.IsNullOrEmpty(_metricsPath))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(_metricsPath));
            Directory.CreateDirectory(directory);
            File.WriteAllText(_metricsPath, JsonConvert.SerializeObject(_report, Formatting.Indented));
        }
        return result;
    }

    private async UniTask<ScenarioResult> Run(ScenarioContext ctx)
    {
        if (ctx.isServer)
        {
            var players = new List<PlayerID>(ctx.networkManager.players);
            players.Sort((a, b) => a.id.value.CompareTo(b.id.value));
            if (players.Count != ctx.expectedConnections)
                return ScenarioResult.Fail($"connected clients={players.Count}, expected={ctx.expectedConnections}");
            int columns = Mathf.CeilToInt(Mathf.Sqrt(_totalBodies));
            int ownedBodies = _totalBodies - _sharedBodies;
            for (int i = 0; i < _totalBodies; i++)
            {
                var position = new Vector3(FullPredictionBenchmarkBody.ArenaX + (i % columns - (columns - 1) * 0.5f) * 0.85f,
                    0.45f, (i / columns - (columns - 1) * 0.5f) * 0.85f);
                PlayerID? owner = i < ownedBodies ? players[i % players.Count] : null;
                if (!_world.hierarchy.Create(_bodyPrefab, position, Quaternion.identity, owner).HasValue)
                    return ScenarioResult.Fail($"failed to spawn body {i}");
            }
        }

        await UniTaskUtils.WaitWithTimeout(() => CollectBodies() == _totalBodies, Timeout, ctx.cancellationToken);
        string invalid = ValidateWorkload(ctx);
        if (invalid != null)
            return ScenarioResult.Fail(invalid);
        await ScenarioBarrier.Wait(ctx, ReadyBarrier, Timeout);
        if (ctx.isServer)
        {
            ulong start = _world.localTick + (ulong)Mathf.CeilToInt((_settleSeconds + 3f) * _world.tickRate);
            FullPredictionBenchmarkSignals.Schedule(start, start + (ulong)Mathf.CeilToInt(_seconds * _world.tickRate));
        }
        await UniTaskUtils.WaitWithTimeout(() => FullPredictionBenchmarkSignals.startTick > 0, Timeout, ctx.cancellationToken);
        _report.scheduledStartTick = FullPredictionBenchmarkSignals.startTick;
        _report.scheduledEndTick = FullPredictionBenchmarkSignals.endTick;
        PredictionPerformanceTelemetry.reconcileIntervalSeconds = _reconcileMs / 1000.0;
        ctx.networkManager.tickModule.onPostTick += OnPostTick;
        await UniTaskUtils.WaitWithTimeout(() => _ended, Timeout + _seconds + _settleSeconds, ctx.cancellationToken);
        await ScenarioBarrier.Wait(ctx, SampledBarrier, Timeout);
        _world.onBeforePhysicsPass += ObserveValidationTick;

        if (_report.contacts == 0 || _report.bodyContacts == 0 || _report.groundingQueries == 0)
            return ScenarioResult.Fail($"inactive workload: contacts={_report.contacts}, bodyContacts={_report.bodyContacts}, groundingQueries={_report.groundingQueries}");
        if (_report.actualStartTick != _report.scheduledStartTick || _report.actualEndTick != _report.scheduledEndTick)
            return ScenarioResult.Fail($"sampling missed agreed tick window: {_report.actualStartTick}..{_report.actualEndTick} vs {_report.scheduledStartTick}..{_report.scheduledEndTick}");

        // Validation is outside telemetry. A verified high-water mark does not prove that a
        // particular UDP frame was applied. Select a tick observed by every peer before
        // reading sparse authoritative histories; missing target ticks are reported explicitly.
        if (ctx.isServer)
            FullPredictionBenchmarkSignals.ValidateAt(_world.localTick + (ulong)(_world.tickRate * 2));
        await UniTaskUtils.WaitWithTimeout(() => FullPredictionBenchmarkSignals.requestedValidationTick > 0, Timeout, ctx.cancellationToken);
        ulong cutoff = FullPredictionBenchmarkSignals.requestedValidationTick;
        _report.requestedValidationTick = cutoff;
        await UniTaskUtils.WaitWithTimeout(() => HasVerifiedTick(cutoff), Timeout, ctx.cancellationToken);
        _world.onBeforePhysicsPass -= ObserveValidationTick;
        _report.requestedValidationTickApplied = _observedValidationTicks.Contains(cutoff);
        _report.validationCandidates = GetValidationCandidates(cutoff);
        if (ctx.isClient)
            FullPredictionBenchmarkSignals.ReportValidationCandidates(cutoff, _report.validationCandidates);
        if (ctx.isServer)
        {
            await UniTaskUtils.WaitWithTimeout(
                () => FullPredictionBenchmarkSignals.HasValidationCandidates(ctx.expectedConnections),
                Timeout, ctx.cancellationToken);
            FullPredictionBenchmarkSignals.SelectValidationTick(_report.validationCandidates, ctx.expectedConnections);
        }
        await UniTaskUtils.WaitWithTimeout(() => FullPredictionBenchmarkSignals.validationSelectionReady, Timeout, ctx.cancellationToken);
        if (FullPredictionBenchmarkSignals.validationSelectionError != null)
            return ScenarioResult.Fail(FullPredictionBenchmarkSignals.validationSelectionError);
        ulong validationTick = FullPredictionBenchmarkSignals.validationTick;
        _report.validationTick = validationTick;
        if (!_observedValidationTicks.Contains(validationTick))
            return ScenarioResult.Fail($"selected validation tick {validationTick} was not applied locally");
        var snapshots = CaptureVerified(validationTick);
        _report.validatedBodies = snapshots.Length;
        if (ctx.isServer)
            FullPredictionBenchmarkSignals.SendReference(snapshots);
        await UniTaskUtils.WaitWithTimeout(() => FullPredictionBenchmarkSignals.reference != null, Timeout, ctx.cancellationToken);
        string difference = Compare(snapshots, FullPredictionBenchmarkSignals.reference);
        _report.verifiedStateMatch = difference == null;
        if (!ctx.isServer)
        {
            string schedulingError = null;
            if (_report.telemetry.frameBatches.count == 0 || _report.telemetry.appliedVerifiedFrames == 0)
                schedulingError = "client sampling did not observe complete frames and applied authoritative frames";
            else if (_report.telemetry.frameBatches.max > 1)
                schedulingError = $"active client reconciled {_report.telemetry.frameBatches.max} times in one render frame";
            if (schedulingError != null)
                difference = difference == null ? schedulingError : $"{difference}; {schedulingError}";
        }
        if (ctx.isClient)
            FullPredictionBenchmarkSignals.ReportValidation(difference);
        await ScenarioBarrier.Wait(ctx, ValidatedBarrier, Timeout);
        if (ctx.isServer)
            FullPredictionBenchmarkSignals.Complete(ctx.expectedConnections);
        await UniTaskUtils.WaitWithTimeout(() => FullPredictionBenchmarkSignals.completed, Timeout, ctx.cancellationToken);
        if (FullPredictionBenchmarkSignals.failure != null)
            return ScenarioResult.Fail(FullPredictionBenchmarkSignals.failure);
        if (difference != null)
            return ScenarioResult.Fail(difference);
        return ScenarioResult.Ok($"FULL physics: {snapshots.Length} bodies, {_report.contacts} contacts, {_report.bodyContacts} body contacts, verified match at tick {validationTick}; metrics={_metricsPath}");
    }

    private void OnPostTick()
    {
        if (!_started && _world.localTick >= _report.scheduledStartTick)
        {
            _started = true;
            _report.actualStartTick = _world.localTick;
            _startReliable = _world.reliableFramesSentTotal;
            _startFull = _world.fullFramesSentTotal;
            _startReceived = _world.framesReceivedTotal;
            _startFullReceived = _world.fullFramesReceivedTotal;
            _startDeltaFrames = _world.deltaFramesWrittenTotal;
            _startDeltaBytes = _world.deltaFrameBytesTotal;
            _startFullBytes = _world.fullFrameBytesTotal;
            FullPredictionBenchmarkBody.BeginContacts();
            PredictionPerformanceTelemetry.Begin(_world);
        }
        if (_started && !_ended && _world.lastMaxAckLagTicks > _report.maxAckLagTicks)
            _report.maxAckLagTicks = _world.lastMaxAckLagTicks;
        if (_started && !_ended && _world.localTick >= _report.scheduledEndTick)
            EndSampling();
    }

    private void EndSampling()
    {
        _report.telemetry = PredictionPerformanceTelemetry.End();
        FullPredictionBenchmarkBody.sampling = false;
        _report.actualEndTick = _world.localTick;
        _report.contacts = FullPredictionBenchmarkBody.contactCallbacks;
        _report.contactPoints = FullPredictionBenchmarkBody.contactPoints;
        _report.bodyContacts = FullPredictionBenchmarkBody.bodyContactCallbacks;
        _report.groundingQueries = FullPredictionBenchmarkBody.groundingQueries;
        _report.reliableFramesSent = _world.reliableFramesSentTotal - _startReliable;
        _report.fullFramesSent = _world.fullFramesSentTotal - _startFull;
        _report.framesReceived = _world.framesReceivedTotal - _startReceived;
        _report.fullFramesReceived = _world.fullFramesReceivedTotal - _startFullReceived;
        _report.deltaFramesWritten = _world.deltaFramesWrittenTotal - _startDeltaFrames;
        _report.deltaFrameBytes = _world.deltaFrameBytesTotal - _startDeltaBytes;
        _report.fullFrameBytes = _world.fullFrameBytesTotal - _startFullBytes;
        _ended = true;
    }

    private int CollectBodies()
    {
        _bodies.Clear();
        _rigidbodies.Clear();
        _transforms.Clear();
        var records = _world.hierarchy.currentState.spawnedPrefabs;
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i].instanceId.TryGetComponent<FullPredictionBenchmarkBody>(_world, out var body))
                _bodies.Add(body);
        }
        _bodies.Sort((a, b) => a.id.objectId.instanceId.value.CompareTo(b.id.objectId.instanceId.value));
        for (int i = 0; i < _bodies.Count; i++)
        {
            _rigidbodies.Add(_bodies[i].GetComponent<PredictedRigidbody>());
            _transforms.Add(_bodies[i].GetComponent<PredictedTransform>());
        }
        return _bodies.Count;
    }

    private string ValidateWorkload(ScenarioContext ctx)
    {
        int owned = 0;
        for (int i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.owner.HasValue)
                owned++;
            if (body.GetResolvedPredictionPolicy() != PredictionPolicy.FullPrediction ||
                _rigidbodies[i].GetResolvedPredictionPolicy() != PredictionPolicy.FullPrediction ||
                _transforms[i].GetResolvedPredictionPolicy() != PredictionPolicy.FullPrediction ||
                body.GetComponent<Rigidbody>().isKinematic)
                return $"body {body.id} is not dynamic FULL prediction";
        }
        _report.ownedBodies = owned;
        return owned == _totalBodies - _sharedBodies ? null : $"owned body count={owned}, expected={_totalBodies - _sharedBodies}";
    }

    private bool HasVerifiedTick(ulong tick)
    {
        // This is only a high-water gate for closing candidate collection, not proof that
        // the particular tick was applied. ObserveValidationTick records that separately.
        for (int i = 0; i < _bodies.Count; i++)
            if (!_rigidbodies[i].lastVerifiedTick.HasValue || _rigidbodies[i].lastVerifiedTick.Value < tick ||
                !_transforms[i].lastVerifiedTick.HasValue || _transforms[i].lastVerifiedTick.Value < tick)
                return false;
        return true;
    }

    private void ObserveValidationTick()
    {
        if (!_world.isVerified)
            return;
        ulong tick = _world.localTickInContext;
        if (!_world.isServer)
        {
            // isVerified also covers gap catch-up. Only an actual addressed frame apply sets
            // every benchmark component's verified tick to this simulation tick. An omitted
            // unchanged record still advances that metadata and is valid coverage.
            for (int i = 0; i < _bodies.Count; i++)
                if (_rigidbodies[i].lastVerifiedTick != tick || _transforms[i].lastVerifiedTick != tick)
                    return;
        }
        _observedValidationTicks.Add(tick);
    }

    private ulong[] GetValidationCandidates(ulong cutoff)
    {
        ulong window = (ulong)_world.tickRate;
        ulong first = cutoff > window ? cutoff - window : 0;
        var candidates = new List<ulong>();
        foreach (ulong tick in _observedValidationTicks)
            if (tick >= first && tick <= cutoff)
                candidates.Add(tick);
        candidates.Sort();
        return candidates.ToArray();
    }

    private FullPredictionBodySnapshot[] CaptureVerified(ulong tick)
    {
        var result = new FullPredictionBodySnapshot[_bodies.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var pose = ReadVerified<PredictedTransformState>(_transforms[i], tick);
            var motion = ReadVerified<UnityRigidbodyState>(_rigidbodies[i], tick);
            result[i] = new FullPredictionBodySnapshot
            {
                objectId = _bodies[i].id.objectId.instanceId.value,
                position = pose.unityPosition, rotation = pose.unityRotation,
                velocity = motion.linearVelocity, angularVelocity = motion.angularVelocity,
                isKinematic = motion.isKinematic, useGravity = motion.useGravity, isSleeping = motion.isSleeping
            };
        }
        return result;
    }

    // Production histories are internal. Reflection is deliberately confined to post-sample QA.
    private static T ReadVerified<T>(PredictedIdentity<T> identity, ulong tick) where T : struct, IPredictedData<T>
    {
        var history = typeof(PredictedIdentity<T>).GetField("_verifiedHistory", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(identity);
        if (history == null)
            throw new InvalidOperationException($"missing verified history for {identity.id}");
        object[] args = { tick, null };
        if (!(bool)history.GetType().GetMethod("ReadOrPrevious").Invoke(history, args))
            throw new InvalidOperationException($"missing verified body state {identity.id} at {tick}");
        return (T)args[1].GetType().GetField("state").GetValue(args[1]);
    }

    private string Compare(FullPredictionBodySnapshot[] actual, FullPredictionBodySnapshot[] expected)
    {
        if (actual.Length != expected.Length || actual.Length != _totalBodies)
            return $"verified body count {actual.Length}/{expected.Length}, expected {_totalBodies}";
        for (int i = 0; i < actual.Length; i++)
        {
            var a = actual[i];
            var b = expected[i];
            float positionError = Vector3.Distance(a.position, b.position);
            float rotationError = Quaternion.Angle(a.rotation, b.rotation);
            float velocityError = Mathf.Max(Vector3.Distance(a.velocity, b.velocity), Vector3.Distance(a.angularVelocity, b.angularVelocity));
            _report.maxPositionError = Mathf.Max(_report.maxPositionError, positionError);
            _report.maxRotationErrorDegrees = Mathf.Max(_report.maxRotationErrorDegrees, rotationError);
            _report.maxVelocityError = Mathf.Max(_report.maxVelocityError, velocityError);
            if (a.objectId != b.objectId || !float.IsFinite(positionError) || !float.IsFinite(rotationError) || !float.IsFinite(velocityError) ||
                positionError > _report.positionTolerance || rotationError > _report.rotationToleranceDegrees || velocityError > _report.velocityTolerance ||
                a.isKinematic != b.isKinematic || a.isSleeping != b.isSleeping || a.useGravity != b.useGravity)
                return $"verified body {a.objectId} differs at {_report.validationTick}: position={positionError}, angle={rotationError}, velocity={velocityError}";
        }
        return null;
    }

    private int ReadInt(string flag, int fallback, int minimum)
    {
        if (!CommandLineUtils.TryGetArgument(flag, out var raw)) return fallback;
        if (int.TryParse(raw, out var value) && value >= minimum) return value;
        _configurationError = $"invalid {flag}: {raw}";
        return fallback;
    }

    private float ReadFloat(string flag, float fallback, float minimum)
    {
        if (!CommandLineUtils.TryGetArgument(flag, out var raw)) return fallback;
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value) && value >= minimum) return value;
        _configurationError = $"invalid {flag}: {raw}";
        return fallback;
    }

    private static void CreateWall(string name, Vector3 position, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.position = position;
        go.AddComponent<BoxCollider>().size = size;
    }
}

[Serializable]
public sealed class FullPredictionBenchmarkReport
{
    public string role, policy, physicsProvider, stateAccuracy, message;
    public bool success, verifiedStateMatch, requestedValidationTickApplied;
    public int clients, bodiesPerPlayer, sharedBodies, ownedBodies, totalBodies, tickRate, validatedBodies, predictedEventMask;
    public float requestedSeconds, settleSeconds, positionTolerance, velocityTolerance, rotationToleranceDegrees;
    public float maxPositionError, maxRotationErrorDegrees, maxVelocityError;
    public double reconcileIntervalMs;
    public ulong scheduledStartTick, scheduledEndTick, actualStartTick, actualEndTick, requestedValidationTick, validationTick;
    public ulong[] validationCandidates;
    public long contacts, contactPoints, bodyContacts, groundingQueries;
    public ulong reliableFramesSent, fullFramesSent, framesReceived, fullFramesReceived;
    public ulong deltaFramesWritten, deltaFrameBytes, fullFrameBytes, maxAckLagTicks;
    public PredictionPerformanceSnapshot telemetry;
}

public struct FullPredictionBodySnapshot : PurrNet.Packing.IPackedAuto
{
    public uint objectId;
    public Vector3 position, velocity, angularVelocity;
    public Quaternion rotation;
    public bool isKinematic, isSleeping, useGravity;
}

public static class FullPredictionBenchmarkSignals
{
    public static ulong startTick, endTick, requestedValidationTick, validationTick;
    public static FullPredictionBodySnapshot[] reference;
    public static bool completed;
    public static string failure;
    public static bool validationSelectionReady;
    public static string validationSelectionError;
    private static readonly Dictionary<PlayerID, string> Reports = new();
    private static readonly Dictionary<PlayerID, ulong[]> ValidationCandidates = new();

    public static void Reset()
    {
        startTick = endTick = requestedValidationTick = validationTick = 0;
        reference = null;
        completed = false;
        failure = null;
        validationSelectionReady = false;
        validationSelectionError = null;
        Reports.Clear();
        ValidationCandidates.Clear();
    }

    [ObserversRpc(runLocally: true)]
    public static void Schedule(ulong start, ulong end) { startTick = start; endTick = end; }
    [ObserversRpc(runLocally: true)]
    public static void ValidateAt(ulong tick) { requestedValidationTick = tick; }
    [ServerRpc(requireOwnership: false)]
    public static void ReportValidationCandidates(ulong cutoff, ulong[] ticks, RPCInfo info = default)
    {
        if (cutoff == requestedValidationTick)
            ValidationCandidates[info.sender] = ticks ?? Array.Empty<ulong>();
    }

    public static bool HasValidationCandidates(int expectedClients) => ValidationCandidates.Count >= expectedClients;

    public static void SelectValidationTick(ulong[] serverTicks, int expectedClients)
    {
        if (ValidationCandidates.Count != expectedClients)
        {
            PublishValidationSelection(0,
                $"received {ValidationCandidates.Count}/{expectedClients} validation candidate reports");
            return;
        }
        var common = new HashSet<ulong>(serverTicks);
        foreach (var ticks in ValidationCandidates.Values)
            common.IntersectWith(ticks);
        ulong selected = 0;
        foreach (ulong tick in common)
            if (tick > selected)
                selected = tick;
        PublishValidationSelection(selected, selected == 0
            ? $"no common actually applied validation tick in the one-second window ending at {requestedValidationTick}"
            : null);
    }

    [ObserversRpc(runLocally: true)]
    private static void PublishValidationSelection(ulong tick, string error)
    {
        validationTick = tick;
        validationSelectionError = error;
        validationSelectionReady = true;
    }
    [ObserversRpc(runLocally: true)]
    public static void SendReference(FullPredictionBodySnapshot[] bodies) { reference = bodies; }
    [ServerRpc(requireOwnership: false)]
    public static void ReportValidation(string error, RPCInfo info = default) { Reports[info.sender] = error; }

    public static void Complete(int expectedClients)
    {
        string error = Reports.Count < expectedClients ? $"received {Reports.Count}/{expectedClients} verified validation reports" : null;
        foreach (var report in Reports)
            if (report.Value != null)
                error = $"client {report.Key}: {report.Value}";
        CompleteRpc(error);
    }

    [ObserversRpc(runLocally: true)]
    private static void CompleteRpc(string error) { failure = error; completed = true; }
}
