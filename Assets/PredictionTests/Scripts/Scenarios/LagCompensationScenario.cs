using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Server-controlled target sliding along X on a triangle wave that is linear in ticks, so a
/// lag-compensated hit point converts straight back into the tick the collider history was
/// sampled at. Carries a PurrNet ColliderRollback component like a user's target would.
/// </summary>
public class LagCompTarget : PredictedIdentity<LagCompTarget.TargetState>
{
    public const float HalfExtent = 1f;
    public const float Amplitude = 20f;
    public const uint HalfPeriodTicks = 40;
    public const float UnitsPerTick = Amplitude / HalfPeriodTicks;

    public const float ArenaX = 1000f;
    public const float ArenaY = 200f;
    public const float LaneZ = 900f;

    public static ulong baseTick;

    public struct TargetState : IPredictedData<TargetState>
    {
        public uint steps;

        public void Dispose() { }
    }

    /// <summary>X position of the collider centre at a (fractional) state tick.</summary>
    public static double PositionX(double stateTick)
    {
        double phase = stateTick - baseTick;
        if (phase < 0) phase = 0;
        phase %= 2.0 * HalfPeriodTicks;
        return Amplitude * (phase < HalfPeriodTicks ? phase / HalfPeriodTicks : 2.0 - phase / HalfPeriodTicks);
    }

    /// <summary>Ticks to the nearest direction change; interpolation across a turn is not linear.</summary>
    public static double TicksToNearestTurn(double stateTick)
    {
        double phase = stateTick - baseTick;
        phase = (phase % HalfPeriodTicks + HalfPeriodTicks) % HalfPeriodTicks;
        return Math.Min(phase, HalfPeriodTicks - phase);
    }

    public static LagCompTarget instance;

    protected override void Simulate(ref TargetState state, float delta)
    {
        instance = this;
        state.steps += 1;
        ulong stateTick = predictionManager.localTickInContext + 1;
        transform.position = new Vector3(ArenaX + (float)PositionX(stateTick), ArenaY, LaneZ);
    }
}

/// <summary>
/// Per-player hitscan shooter. Fires along +X at the target and resolves the shot through
/// lag compensation at <see cref="PredictedIdentity.lagCompensationTick"/>.
/// </summary>
public class LagCompShooter : PredictedIdentity<LagCompShooter.FireInput, LagCompShooter.ShooterState>
{
    public const uint FireEveryTicks = 6;
    public const float ShooterX = -60f;
    public const float Range = 200f;

    public static ulong fireStartTick = ulong.MaxValue;
    public static ulong fireEndTick;

    public struct FireInput : IPredictedData
    {
        public bool fire;

        public void Dispose() { }
    }

    public struct ShooterState : IPredictedData<ShooterState>
    {
        public int shots;
        public int hits;

        public void Dispose() { }
    }

    public struct Shot
    {
        public ulong tick;
        public double viewTick;
        public bool hit;
        public double measuredX;
        public double expectedX;
        public double errorTicks;
        public double naiveErrorTicks;
        public bool nearTurn;
        // Client collider history holds rendered poses: a render stall freezes them and a view
        // phase shift moves them off their ticks. Only the server records tick-exact poses.
        public bool renderSkew;
        public bool authoritative;
        public double liveX;
        public double rollbackTick;
        public double sampleBefore;
        public double sampleAt;
        public double sampleAfter;
    }

    private static bool Frozen(double earlier, double later)
        => !double.IsNaN(earlier) && !double.IsNaN(later) && Math.Abs(later - earlier) < 0.01;

    private static bool OneTickApart(double earlier, double later, double tolerance)
        => !double.IsNaN(earlier) && !double.IsNaN(later) &&
           Math.Abs(Math.Abs(later - earlier) - LagCompTarget.UnitsPerTick) <= tolerance;

    private static double SampleX(PredictionManager pm, Collider collider, double predictionTick)
    {
        if (!collider || !pm.lagCompensation.TryResolve(predictionTick, out var module, out var tick))
            return double.NaN;
        return module.TryGetColliderState(tick, collider, out var state) ? state.position.x - LagCompTarget.ArenaX : double.NaN;
    }

    public static readonly List<Shot> shots = new();

    public static void ResetAll() => shots.Clear();

    protected override void GetFinalInput(ref FireInput input)
    {
        var tick = predictionManager.localTick;
        input.fire = tick >= fireStartTick && tick < fireEndTick && tick % FireEveryTicks == 0;
    }

    protected override void Simulate(FireInput input, ref ShooterState state, float delta)
    {
        if (!input.fire)
            return;

        state.shots += 1;

        var pm = predictionManager;
        ulong tick = pm.localTickInContext;
        double viewTick = lagCompensationTick;
        var ray = new Ray(new Vector3(LagCompTarget.ArenaX + ShooterX, LagCompTarget.ArenaY, LagCompTarget.LaneZ), Vector3.right);
        bool hit = pm.lagCompensation.Raycast(viewTick, ray, out var hitInfo, Range);
        if (hit)
            state.hits += 1;

        bool authoritative = pm.isServer;
        if (!authoritative && (!isController || pm.isReplaying))
            return;

        double measured = hit ? hitInfo.point.x + LagCompTarget.HalfExtent - LagCompTarget.ArenaX : double.NaN;
        double expected = LagCompTarget.PositionX(viewTick);
        var target = LagCompTarget.instance;
        var collider = target ? target.GetComponent<Collider>() : null;
        double floor = Math.Floor(viewTick);
        pm.TryGetColliderRollbackTick(viewTick, out var rollbackTick);
        double sampleBefore = SampleX(pm, collider, floor - 1);
        double sampleAt = SampleX(pm, collider, floor);
        double sampleAfter = SampleX(pm, collider, floor + 1);
        // The sample at the rewind tick is consistent with the recorded view offset by construction,
        // so an integer rewind is exact unless the pose was frozen (no re-render between ticks). A
        // fractional rewind interpolates between two samples, which only has a single time when
        // they are one tick of motion apart; a render phase shift between them breaks that.
        double fraction = viewTick - floor;
        bool renderSkew = !authoritative &&
                          (Frozen(sampleBefore, sampleAt) ||
                           fraction > 1e-6 && !OneTickApart(sampleAt, sampleAfter, 0.15 * LagCompTarget.UnitsPerTick));
        shots.Add(new Shot
        {
            tick = tick,
            viewTick = viewTick,
            hit = hit,
            measuredX = measured,
            expectedX = expected,
            errorTicks = hit ? (measured - expected) / LagCompTarget.UnitsPerTick : double.NaN,
            naiveErrorTicks = hit ? (measured - LagCompTarget.PositionX(tick)) / LagCompTarget.UnitsPerTick : double.NaN,
            nearTurn = LagCompTarget.TicksToNearestTurn(viewTick) < 1.5,
            renderSkew = renderSkew,
            authoritative = authoritative,
            liveX = target ? target.transform.position.x - LagCompTarget.ArenaX : double.NaN,
            rollbackTick = rollbackTick,
            sampleBefore = sampleBefore,
            sampleAt = sampleAt,
            sampleAfter = sampleAfter
        });
    }

    public string Digest() => $"{id.objectId.instanceId.value}:{currentState.shots}:{currentState.hits}";
}

/// <summary>
/// Lag-compensated hitscan lands where the shooter saw the target. A target moves on a tick-linear
/// path under ColliderRollback; every peer's shots are resolved at the shooter's view tick and the
/// hit point is converted back into ticks. The server (authoritative) and each controller's own
/// prediction must both land within a fraction of a tick, and the final shot/hit counts converge.
/// </summary>
public class LagCompensationScenario : Scenario
{
    private const int DigestChannel = 1700;
    private const float WarmupSeconds = 1f;
    private const float FireSeconds = 8f;
    private const float SettleSeconds = 1f;
    private const float Timeout = 90f;
    private const int MinShots = 20;
    private const double MaxErrorTicks = 0.2d;

    private GameObject _targetPrefab;
    private GameObject _shooterPrefab;
    private int _targetPrefabId;
    private int _shooterPrefabId;
    private ulong _fireEndTick;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _targetPrefab = new GameObject("LagCompTarget");
        _targetPrefab.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_targetPrefab);
        _targetPrefab.AddComponent<BoxCollider>().size = Vector3.one * (LagCompTarget.HalfExtent * 2f);
        var pt = _targetPrefab.AddComponent<PredictedTransform>();
        _targetPrefab.AddComponent<LagCompTarget>();
        _targetPrefab.AddComponent<ColliderRollback>();

        var settings = ScriptableObject.CreateInstance<TransformInterpolationSettings>();
        typeof(PredictedTransform).GetField("_interpolationSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(pt, settings);

        PredictionTestUtils.RegisterPrefab(ctx, _targetPrefab);

        _shooterPrefab = PredictionTestUtils.CreatePrefab<LagCompShooter>("LagCompShooter");
        PredictionTestUtils.RegisterPrefab(ctx, _shooterPrefab);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        var tickRate = ctx.predictionManager.tickRate;
        LagCompTarget.baseTick = startTick;
        LagCompShooter.fireStartTick = startTick + (ulong)Mathf.CeilToInt(WarmupSeconds * tickRate);
        _fireEndTick = LagCompShooter.fireStartTick + (ulong)Mathf.CeilToInt(FireSeconds * tickRate);
        LagCompShooter.fireEndTick = _fireEndTick;
        LagCompShooter.ResetAll();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(_targetPrefab, out _targetPrefabId);
        pm.TryGetPrefab(_shooterPrefab, out _shooterPrefabId);

        int expectedShooters = ctx.expectedConnections;

        if (ctx.isServer)
        {
            var targetPosition = new Vector3(LagCompTarget.ArenaX, LagCompTarget.ArenaY, LagCompTarget.LaneZ);
            if (!pm.hierarchy.Create(_targetPrefab, targetPosition, Quaternion.identity).HasValue)
                return ScenarioResult.Fail("failed to create the target");

            var owners = new List<PlayerID>(pm.players.players);
            owners.Sort((a, b) => a.id.value.CompareTo(b.id.value));
            if (owners.Count != expectedShooters)
                return ScenarioResult.Fail($"expected {expectedShooters} players, saw {owners.Count}");

            for (var i = 0; i < owners.Count; i++)
            {
                var position = new Vector3(LagCompTarget.ArenaX + LagCompShooter.ShooterX, LagCompTarget.ArenaY, LagCompTarget.LaneZ);
                if (!pm.hierarchy.Create(_shooterPrefab, position, Quaternion.identity, owners[i]).HasValue)
                    return ScenarioResult.Fail($"failed to create shooter {i}");
            }
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => PredictionTestUtils.CountInstances(pm, _targetPrefabId) == 1 &&
                      PredictionTestUtils.CountInstances(pm, _shooterPrefabId) == expectedShooters &&
                      (ctx.isServer || HasOwnedShooter(ctx)),
                Timeout,
                ctx.cancellationToken);

            await UniTaskUtils.WaitWithTimeout(
                () => pm.time.tick >= LagCompShooter.fireStartTick - 2,
                Timeout,
                ctx.cancellationToken);

            var registration = CheckRollbackRegistration(ctx);
            if (registration != null)
                return ScenarioResult.Fail(registration);

            await UniTaskUtils.WaitWithTimeout(
                () => pm.time.tick >= _fireEndTick + (ulong)Mathf.CeilToInt(SettleSeconds * pm.tickRate),
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException e)
        {
            return ScenarioResult.Fail(e.Message);
        }

        var report = BuildReport(ctx, out var failure);
        if (failure != null)
            return ScenarioResult.Fail($"{failure} | {report}");

        await UniTask.WaitForSeconds(1f, cancellationToken: ctx.cancellationToken);

        var digest = await DigestExchange.Compare(ctx, DigestChannel, BuildDigest(ctx), 30f);
        return digest.success ? ScenarioResult.Ok(report) : digest;
    }

    private static bool HasOwnedShooter(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.instanceId.TryGetComponent<LagCompShooter>(pm, out var shooter) && shooter.isOwner)
                return true;
        }

        return false;
    }

    private string CheckRollbackRegistration(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var module = pm.lagCompensation.module;
        if (module == null)
            return "no collider rollback module for the prediction scene";

        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (!details.instanceId.TryGetComponent<LagCompTarget>(pm, out var target))
                continue;

            var collider = target.GetComponent<Collider>();
            if (!collider)
                return "target has no collider";

            if (!pm.TryGetColliderRollbackTick(pm.localTick, out var rollbackTick))
                return "no prediction-to-rollback tick mapping yet";

            if (!module.TryGetColliderState(rollbackTick, collider, out _))
            {
                return $"target collider is not tracked by collider rollback (instance scene '{target.gameObject.scene.name}', " +
                       $"manager scene '{pm.gameObject.scene.name}')";
            }

            return null;
        }

        return "target instance not found";
    }

    private static string BuildReport(ScenarioContext ctx, out string failure)
    {
        var pm = ctx.predictionManager;
        var shots = LagCompShooter.shots;
        failure = null;

        int total = shots.Count;
        int hits = 0, misses = 0, skipped = 0, skewed = 0, measured = 0;
        double maxError = 0d, sumError = 0d, sumRewind = 0d, sumNaive = 0d;
        string worst = null;

        for (var i = 0; i < shots.Count; i++)
        {
            var shot = shots[i];
            sumRewind += shot.tick - shot.viewTick;
            if (shot.nearTurn)
            {
                skipped++;
                continue;
            }
            if (shot.renderSkew)
            {
                skewed++;
                continue;
            }

            if (!shot.hit)
            {
                misses++;
                worst ??= $"miss tick={shot.tick} view={shot.viewTick:F3} expectedX={shot.expectedX:F3}";
                continue;
            }

            hits++;
            measured++;
            double error = Math.Abs(shot.errorTicks);
            sumError += error;
            sumNaive += Math.Abs(shot.naiveErrorTicks);
            if (error > maxError)
            {
                maxError = error;
                if (error > MaxErrorTicks)
                    worst = Describe(shot);
            }
        }

        var report = new StringBuilder();
        report.Append($"role={ctx.role} shots={total} hits={hits} misses={misses} nearTurnSkipped={skipped} renderSkewSkipped={skewed}");
        if (measured > 0)
        {
            report.Append($" maxErrorTicks={maxError:F4} meanErrorTicks={sumError / measured:F4}");
            report.Append($" meanNaiveErrorTicks={sumNaive / measured:F3}");
        }
        if (total > 0)
            report.Append($" meanRewindTicks={sumRewind / total:F3}");
        report.Append($" lead={pm.lead} verified={pm.verifiedServerTick} local={pm.localTick}");
        if (worst != null)
            report.Append($" | worst: {worst}");
        if (shots.Count > 0)
            report.Append($" | first: {Describe(shots[0])}");
        if (skewed > 0)
            report.Insert(0, $"NOTE: {skewed} client shot(s) skipped because the rendered collider history around the rewind was frozen or phase-shifted | ");

        bool measures = ctx.isServer || ctx.isClient;
        if (!measures)
            return report.ToString();

        if (total < MinShots)
            failure = $"only {total} shots were measured, expected at least {MinShots}";
        else if (misses > 0)
            failure = $"{misses} lag-compensated shots missed a target that was always in the line of fire";
        else if (maxError > MaxErrorTicks)
            failure = $"worst rewind error {maxError:F3} ticks exceeds {MaxErrorTicks} ticks";

        return report.ToString();
    }

    private static string Describe(in LagCompShooter.Shot shot)
    {
        double floor = Math.Floor(shot.viewTick);
        return $"tick={shot.tick} view={shot.viewTick:F3} rollbackTick={shot.rollbackTick:F3} hit={shot.hit} " +
               $"measuredX={shot.measuredX:F3} expectedX={shot.expectedX:F3} errorTicks={shot.errorTicks:F3} " +
               $"authoritative={shot.authoritative} liveX={shot.liveX:F3} " +
               $"P({floor - 1})={LagCompTarget.PositionX(floor - 1):F3} P({floor})={LagCompTarget.PositionX(floor):F3} " +
               $"P({floor + 1})={LagCompTarget.PositionX(floor + 1):F3} " +
               $"sample[{floor - 1}]={shot.sampleBefore:F3} sample[{floor}]={shot.sampleAt:F3} sample[{floor + 1}]={shot.sampleAfter:F3}";
    }

    private string BuildDigest(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var sb = new StringBuilder();
        PredictionTestUtils.AppendIdentities<LagCompShooter>(pm, _shooterPrefabId, sb, shooter => shooter.Digest());
        return sb.ToString();
    }
}
