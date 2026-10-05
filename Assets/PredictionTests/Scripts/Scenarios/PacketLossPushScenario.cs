using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Replication of the "CSP very unstable on client when Simulate Packet Loss is enabled"
/// report: one player's ball (the host's in host mode) is driven with keyboard-style input
/// through a pen of 16 predicted boxes, while every other peer watches it as a remote
/// object. Remote peers log the presented pose, prediction ticks and frame counters every
/// rendered frame; the server logs the driver's authoritative position every tick. The
/// scenario reports the longest gap between verified ticks and the largest per-frame view
/// step; `analyze-push.py` joins the traces into presented-pose error against the truth.
/// Runs alone via `-packetLossPushScenarioOnly`.
/// </summary>
public class PacketLossPushScenario : Scenario
{
    private const int ReadyBarrierId = 22_001;
    private const int DoneBarrierId = 22_002;
    private const float Timeout = 120f;
    private const int BoxCount = 16;

    private static readonly Vector3 Base = new(-500f, 0f, -500f);

    private float _seconds = 30f;
    private GameObject _ballPrefab;
    private GameObject _boxPrefab;

    private static readonly System.Reflection.FieldInfo EventMaskField = typeof(PredictedRigidbody).GetField(
        "_eventMask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.FieldInfo InterpolationField = typeof(PredictedTransform).GetField(
        "_interpolationSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        if (CommandLineUtils.TryGetArgument("-pushSeconds", out var seconds) &&
            float.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
            _seconds = parsed;

        PushBallPawn.arenaCenter = Base;
        PushBallPawn.serverTrace.Clear();

        var interpolation = ScriptableObject.CreateInstance<TransformInterpolationSettings>();
        interpolation.positionInterpolation.correctionBlendMinMax = new Vector2(0f, 4f);
        bool interpolate = !CommandLineUtils.HasFlag("-pushNoInterpolation");
        interpolation.useInterpolation = interpolate;

        var eventMask = PredictedRigidbody.DEFAULT_EVENT_MASK;
        if (CommandLineUtils.TryGetArgument("-pushEventMask", out var mask) && int.TryParse(mask, out var parsedMask))
            eventMask = (PhysicsEventMask)parsedMask;

        bool userContacts = CommandLineUtils.HasFlag("-pushUserContacts");

        BuildPen();

        _ballPrefab = new GameObject("PushBall");
        _ballPrefab.SetActive(false);
        _ballPrefab.transform.localScale = Vector3.one * 3f;
        var ballBody = _ballPrefab.AddComponent<Rigidbody>();
        ballBody.mass = 3f;
        ballBody.linearDamping = 1f;
        ballBody.angularDamping = 0.05f;
        _ballPrefab.AddComponent<SphereCollider>().radius = 0.5f;
        InterpolationField.SetValue(_ballPrefab.AddComponent<PredictedTransform>(), interpolation);
        EventMaskField.SetValue(_ballPrefab.AddComponent<PredictedRigidbody>(), eventMask);
        if (userContacts)
            _ballPrefab.AddComponent<UserContactLog>();
        var pawn = _ballPrefab.AddComponent<PushBallPawn>();
        pawn.extrapolateInput = CommandLineUtils.HasFlag("-pushExtrapolateInput");
        DontDestroyOnLoad(_ballPrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _ballPrefab);

        _boxPrefab = new GameObject("PushBox");
        _boxPrefab.SetActive(false);
        var boxBody = _boxPrefab.AddComponent<Rigidbody>();
        boxBody.mass = 0.5f;
        _boxPrefab.AddComponent<BoxCollider>();
        InterpolationField.SetValue(_boxPrefab.AddComponent<PredictedTransform>(), interpolation);
        EventMaskField.SetValue(_boxPrefab.AddComponent<PredictedRigidbody>(), eventMask);
        if (userContacts)
            _boxPrefab.AddComponent<UserContactLog>();
        DontDestroyOnLoad(_boxPrefab);
        PredictionTestUtils.RegisterPrefab(ctx, _boxPrefab);

        Debug.Log($"[PacketLossPush] setup extrapolateInput={pawn.extrapolateInput} interpolation={interpolate} eventMask={eventMask} userContacts={userContacts} seconds={_seconds}");
    }

    private static void BuildPen()
    {
        var floor = new GameObject("PushFloor");
        floor.AddComponent<BoxCollider>().size = new Vector3(40f, 1f, 40f);
        floor.transform.position = Base + new Vector3(0f, -0.5f, 0f);

        for (var i = 0; i < 4; i++)
        {
            var wall = new GameObject($"PushWall{i}");
            var collider = wall.AddComponent<BoxCollider>();
            bool alongX = i < 2;
            collider.size = alongX ? new Vector3(40f, 4f, 1f) : new Vector3(1f, 4f, 40f);
            float side = i % 2 == 0 ? -19.5f : 19.5f;
            wall.transform.position = Base + (alongX ? new Vector3(0f, 2f, side) : new Vector3(side, 2f, 0f));
        }
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_ballPrefab, out var ballPrefabId);
        pm.TryGetPrefab(_boxPrefab, out var boxPrefabId);

        if (ctx.isServer)
        {
            var owners = new List<PlayerID>(pm.players.players);
            owners.Sort((a, b) => a.id.value.CompareTo(b.id.value));

            for (var i = 0; i < BoxCount; i++)
            {
                // 2 x 4 footprint, two layers, straddling the driver's first crossing.
                int layer = i / 8;
                int slot = i % 8;
                var position = Base + new Vector3(-0.55f + slot / 4 * 1.1f, 0.5f + layer, -1.65f + slot % 4 * 1.1f);
                if (!pm.hierarchy.Create(_boxPrefab, position, Quaternion.identity).HasValue)
                    return ScenarioResult.Fail($"failed to create box {i}");
            }

            for (var i = 0; i < owners.Count; i++)
            {
                var position = Base + (i == 0 ? new Vector3(8f, 1.5f, 0f) : new Vector3(-15f + i * 4f, 1.5f, 15f));
                if (!pm.hierarchy.Create(_ballPrefab, position, Quaternion.identity, owners[i]).HasValue)
                    return ScenarioResult.Fail($"failed to create ball {i}");
            }
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PredictionTestUtils.CountInstances(pm, ballPrefabId) == ctx.expectedConnections &&
                      PredictionTestUtils.CountInstances(pm, boxPrefabId) == BoxCount,
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"rig never spawned: balls={PredictionTestUtils.CountInstances(pm, ballPrefabId)} " +
                $"boxes={PredictionTestUtils.CountInstances(pm, boxPrefabId)}");
        }

        if (!TryGetDriver(pm, ballPrefabId, out var driver))
            return ScenarioResult.Fail("no driver ball found");

        var driverOwner = driver.owner.GetValueOrDefault();
        bool localDriver = pm.localPlayer.HasValue && pm.localPlayer.Value == driverOwner;
        var driverTransform = driver.GetComponent<PredictedTransform>();

        try
        {
            await ScenarioBarrier.Wait(ctx, ReadyBarrierId, Timeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out synchronizing push start");
        }

        PushBallPawn.serverTrace.Clear();
        PushBallPawn.driverOwnerId = driverOwner.id.value;

        var rows = new StringBuilder();
        rows.AppendLine("t,localTick,viewTick,verifiedTick,viewX,viewZ,simX,simZ,frames,fullFrames,leadJumps,leadPauses,starvationJumps,viewStarved");

        bool observe = !localDriver && ctx.isClient;
        ulong lastVerified = pm.verifiedServerTick;
        float lastVerifiedAt = Time.realtimeSinceStartup;
        float maxVerifiedGap = 0f;
        float maxViewStep = 0f;
        Vector3? lastView = null;
        int samples = 0;

        var startDeltaFrames = pm.deltaFramesWrittenTotal;
        var startDeltaBytes = pm.deltaFrameBytesTotal;
        var startFullSent = pm.fullFramesSentTotal;
        var startFrames = pm.framesReceivedTotal;
        var startFull = pm.fullFramesReceivedTotal;
        var startStarvation = pm.starvationJumpsTotal;
        var start = Time.realtimeSinceStartup;

        while (Time.realtimeSinceStartup - start < _seconds)
        {
            await UniTask.Yield(PlayerLoopTiming.PostLateUpdate, ctx.cancellationToken);

            if (!observe || !driver)
                continue;

            var now = Time.realtimeSinceStartup;
            var verified = pm.verifiedServerTick;
            if (verified != lastVerified)
            {
                maxVerifiedGap = Mathf.Max(maxVerifiedGap, now - lastVerifiedAt);
                lastVerified = verified;
                lastVerifiedAt = now;
            }

            driverTransform.GetViewWorldPose(out var view, out _);
            var sim = driver.transform.position;
            if (lastView.HasValue)
                maxViewStep = Mathf.Max(maxViewStep, (view - lastView.Value).magnitude);
            lastView = view;
            samples++;

            rows.Append((now - start).ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(pm.localTick).Append(',')
                .Append(pm.viewTick.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(verified).Append(',')
                .Append(F(view.x - Base.x)).Append(',').Append(F(view.z - Base.z)).Append(',')
                .Append(F(sim.x - Base.x)).Append(',').Append(F(sim.z - Base.z)).Append(',')
                .Append(pm.framesReceivedTotal).Append(',')
                .Append(pm.fullFramesReceivedTotal).Append(',')
                .Append(pm.leadJumpsTotal).Append(',')
                .Append(pm.leadPausesTotal).Append(',')
                .Append(pm.starvationJumpsTotal).Append(',')
                .Append(pm.viewBufferStarvedFramesTotal).AppendLine();
        }

        PushBallPawn.driverOwnerId = 0;

        if (observe)
            WriteTrace("push-view", rows.ToString());

        if (ctx.isServer)
        {
            var trace = new StringBuilder();
            trace.AppendLine("tick,x,z");
            foreach (var (tick, position) in PushBallPawn.serverTrace)
                trace.Append(tick).Append(',').Append(F(position.x - Base.x)).Append(',').Append(F(position.z - Base.z)).AppendLine();
            WriteTrace("push-truth", trace.ToString());
        }

        try
        {
            await ScenarioBarrier.Wait(ctx, DoneBarrierId, Timeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out synchronizing push end");
        }

        var report = observe
            ? $"observer samples={samples} maxVerifiedGapMs={maxVerifiedGap * 1000f:F0} maxViewStep={maxViewStep:F2} " +
              $"frames={pm.framesReceivedTotal - startFrames} fullFrames={pm.fullFramesReceivedTotal - startFull} " +
              $"starvationJumps={pm.starvationJumpsTotal - startStarvation} " +
              $"extrapolateInput={driver.extrapolateInput}"
            : $"driver={localDriver} serverTraceTicks={PushBallPawn.serverTrace.Count}";
        if (ctx.isServer)
        {
            var deltaFrames = pm.deltaFramesWrittenTotal - startDeltaFrames;
            report += $" deltaFrames={deltaFrames} avgDeltaBytes={(deltaFrames > 0 ? (pm.deltaFrameBytesTotal - startDeltaBytes) / deltaFrames : 0)} " +
                      $"maxDeltaBytes={pm.maxDeltaFrameBytes} fullFramesSent={pm.fullFramesSentTotal - startFullSent}";
        }

        Debug.Log($"[PacketLossPush] {ctx.role} {report}");
        return ScenarioResult.Ok(report);
    }

    private static string F(float value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static bool TryGetDriver(PredictionManager pm, int ballPrefabId, out PushBallPawn driver)
    {
        driver = null;
        ulong best = ulong.MaxValue;
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != ballPrefabId || !details.owner.HasValue)
                continue;

            var ownerId = details.owner.Value.id.value;
            if (ownerId >= best || !details.instanceId.TryGetComponent<PushBallPawn>(pm, out var pawn))
                continue;

            best = ownerId;
            driver = pawn;
        }

        return driver;
    }

    private static void WriteTrace(string kind, string contents)
    {
        string path;
        if (CommandLineUtils.TryGetArgument("-results", out var results) && !string.IsNullOrEmpty(results))
            path = results.Replace(".results.json", $".{kind}.csv");
        else
            path = Path.Combine(Application.persistentDataPath, $"{kind}.csv");

        File.WriteAllText(path, contents);
        Debug.Log($"[PacketLossPush] wrote {path}");
    }
}
