using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class TickAgreementShooter : PredictedIdentity<TickAgreementShooter.ShotInput, TickAgreementShooter.ShooterState>
{
    public const int TargetShots = 3;
    public const int ShotSpacing = 40;

    public static GameObject bulletPrefab;

    public struct ShotInput : IPredictedData
    {
        public bool shoot;
        // Diagnostic identity for an input attempt; shooting still depends only on shoot.
        public uint requestedTick;
        public int requestedOrdinal;

        public void Dispose() { }
    }

    public struct ShooterState : IPredictedData<ShooterState>
    {
        public DisposableList<uint> shotTicks;
        public int shots;
        public uint cooldown;

        public void Dispose()
        {
            shotTicks.Dispose();
        }
    }

    private bool _armed;

    public readonly List<uint> firstPredictedTicks = new ();

    private const int MaximumObservations = 128;
    private readonly List<ShotObservation> _observations = new();
    private int _omittedObservations;

    private struct ShotObservation
    {
        public string phase;
        public uint simulationTick;
        public uint requestedTick;
        public int requestedOrdinal;
        public int shotsBefore;
        public uint cooldown;
        public bool accepted;
        public int visits;
    }

    public bool isDone => currentState.shots >= TargetShots;

    public void Arm() => _armed = true;

    protected override void GetFinalInput(ref ShotInput input)
    {
        input.shoot = _armed && currentState.shots < TargetShots &&
                      predictionManager.localTick % ShotSpacing == 0;
        input.requestedTick = input.shoot ? (uint)predictionManager.localTick : 0;
        input.requestedOrdinal = input.shoot ? currentState.shots + 1 : 0;
        if (input.shoot)
            Observe("generated", input.requestedTick, input, currentState.shots, currentState.cooldown, false);
    }

    protected override void Simulate(ShotInput input, ref ShooterState state, float delta)
    {
        if (state.cooldown > 0)
            state.cooldown--;

        if (input.shoot && (!predictionManager.isReplaying || predictionManager.isVerified))
        {
            var phase = predictionManager.cachedIsServer ? "server" :
                predictionManager.isVerified ? "verified-replay" : "forward";
            Observe(phase, (uint)predictionManager.time.tick, input, state.shots, state.cooldown,
                state.cooldown == 0 && state.shots < TargetShots);
        }

        if (!input.shoot || state.cooldown > 0 || state.shots >= TargetShots)
            return;

        uint tick = (uint)predictionManager.time.tick;

        if (state.shotTicks.isDisposed)
            state.shotTicks = DisposableList<uint>.Create(TargetShots);

        state.shotTicks.Add(tick);
        state.shots++;
        state.cooldown = ShotSpacing / 2;

        if (bulletPrefab)
            predictionManager.hierarchy.Create(bulletPrefab, transform.position + new Vector3(state.shots, 0f, 0f), Quaternion.identity);

        if (!predictionManager.isReplaying && !predictionManager.isCatchingUpFrames &&
            firstPredictedTicks.Count < state.shots)
        {
            firstPredictedTicks.Add(tick);
        }
    }

    public string TickDigest()
    {
        var sb = new StringBuilder();
        sb.Append("shots=").Append(currentState.shots).Append(":ticks=");

        if (!currentState.shotTicks.isDisposed)
        {
            for (var i = 0; i < currentState.shotTicks.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(currentState.shotTicks[i]);
            }
        }

        sb.Append(":requests=");
        if (!currentState.shotTicks.isDisposed)
        {
            for (var i = 0; i < currentState.shotTicks.Count; i++)
            {
                if (i > 0) sb.Append(',');
                if (TryGetAuthoritativeShot(i, currentState.shotTicks[i], out var accepted, out _))
                    sb.Append(accepted.requestedTick).Append('/').Append(accepted.requestedOrdinal);
                else
                    sb.Append("missing");
            }
        }
        return sb.ToString();
    }

    // A rollback can discard an attempted shot and reuse its speculative ordinal.
    // Match the final authoritative shot to the same input attempt, never to the
    // first shot that happened to occupy that ordinal before correction.
    internal ScenarioResult ValidateTickAgreement(bool locallyOwned, bool asServer)
    {
        if (_omittedObservations != 0)
            return ScenarioResult.Fail($"shot observation log truncated: omitted {_omittedObservations}");
        var finalTicks = currentState.shotTicks;
        int finalCount = finalTicks.isDisposed ? 0 : finalTicks.Count;
        if (currentState.shots != TargetShots || finalCount != TargetShots)
            return ScenarioResult.Fail($"authoritative shot count mismatch: state {currentState.shots}, ticks {finalCount}");

        for (var i = 0; i < finalCount; i++)
        {
            uint tick = finalTicks[i];
            if (!TryGetAuthoritativeShot(i, tick, out var authoritative, out var failure))
                return ScenarioResult.Fail(failure);
            if (!locallyOwned)
                continue;

            bool generated = false;
            bool predicted = false;
            string forwardPhase = asServer ? "server" : "forward";
            for (var j = 0; j < _observations.Count; j++)
            {
                var sample = _observations[j];
                if (sample.requestedTick != authoritative.requestedTick ||
                    sample.requestedOrdinal != authoritative.requestedOrdinal)
                    continue;
                if (sample.phase == "generated" && sample.simulationTick == authoritative.requestedTick)
                    generated = true;
                if (sample.phase != forwardPhase || !sample.accepted)
                    continue;
                if (sample.simulationTick != tick)
                    return ScenarioResult.Fail(
                        $"tick disagreement for request {sample.requestedTick}/{sample.requestedOrdinal}: " +
                        $"owner predicted {sample.simulationTick}, authoritative {tick}");
                predicted = true;
            }
            if (!generated || !predicted)
                return ScenarioResult.Fail(
                    $"missing {(generated ? "forward acceptance" : "generated input")} for authoritative " +
                    $"request {authoritative.requestedTick}/{authoritative.requestedOrdinal} at tick {tick}");
        }
        return ScenarioResult.Ok();
    }

    private bool TryGetAuthoritativeShot(int shotIndex, uint tick, out ShotObservation accepted, out string failure)
    {
        accepted = default;
        bool found = false;
        for (var i = 0; i < _observations.Count; i++)
        {
            var sample = _observations[i];
            if (!sample.accepted || sample.simulationTick != tick || sample.shotsBefore != shotIndex ||
                (sample.phase != "server" && sample.phase != "verified-replay"))
                continue;
            if (sample.requestedTick == 0 || sample.requestedOrdinal <= 0)
            {
                failure = $"authoritative shot {shotIndex} at tick {tick} has no input request identity";
                return false;
            }
            if (found && (accepted.requestedTick != sample.requestedTick ||
                          accepted.requestedOrdinal != sample.requestedOrdinal))
            {
                failure = $"ambiguous authoritative input requests for shot {shotIndex} at tick {tick}";
                return false;
            }
            accepted = sample;
            found = true;
        }
        failure = found ? null : $"missing authoritative acceptance for shot {shotIndex} at tick {tick}";
        return found;
    }

    internal void Observe(string phase, uint simulationTick, ShotInput input, int shotsBefore,
        uint cooldown, bool accepted)
    {
        for (var i = 0; i < _observations.Count; i++)
        {
            var existing = _observations[i];
            if (existing.phase != phase || existing.simulationTick != simulationTick ||
                existing.requestedTick != input.requestedTick || existing.requestedOrdinal != input.requestedOrdinal ||
                existing.shotsBefore != shotsBefore || existing.cooldown != cooldown || existing.accepted != accepted)
                continue;
            existing.visits++;
            _observations[i] = existing;
            return;
        }

        if (_observations.Count >= MaximumObservations)
        {
            _omittedObservations++;
            return;
        }

        _observations.Add(new ShotObservation
        {
            phase = phase,
            simulationTick = simulationTick,
            requestedTick = input.requestedTick,
            requestedOrdinal = input.requestedOrdinal,
            shotsBefore = shotsBefore,
            cooldown = cooldown,
            accepted = accepted,
            visits = 1
        });
    }

    public string ObservationDigest()
    {
        var sb = new StringBuilder();
        sb.Append("id=").Append(id.objectId.instanceId.value).Append(" owner=").Append(owner)
            .Append(" firstPredicted=[").Append(string.Join(",", firstPredictedTicks))
            .Append("] omitted=").Append(_omittedObservations);
        for (var i = 0; i < _observations.Count; i++)
        {
            var sample = _observations[i];
            sb.Append(" [").Append(sample.phase).Append(" sim=").Append(sample.simulationTick)
                .Append(" request=").Append(sample.requestedTick).Append('/').Append(sample.requestedOrdinal)
                .Append(" before=").Append(sample.shotsBefore).Append(" cooldown=").Append(sample.cooldown)
                .Append(" accepted=").Append(sample.accepted).Append(" visits=").Append(sample.visits).Append(']');
        }
        return sb.ToString();
    }
}

public class TickAgreementScenario : Scenario
{
    private const int DigestChannel = 1500;
    private const float Timeout = 120f;
    private const float SettleSeconds = 3f;

    private static GameObject shooterPrefab;
    private static GameObject bulletPrefab;
    private int _shooterPrefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        if (shooterPrefab)
            return;

        bulletPrefab = PredictionTestUtils.CreatePrefab<PredictedMarker>("TickAgreementBullet");
        PredictionTestUtils.RegisterPrefab(ctx, bulletPrefab);
        TickAgreementShooter.bulletPrefab = bulletPrefab;

        shooterPrefab = PredictionTestUtils.CreatePrefab<TickAgreementShooter>("TickAgreementShooter");
        PredictionTestUtils.RegisterPrefab(ctx, shooterPrefab);
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        pm.TryGetPrefab(shooterPrefab, out _shooterPrefabId);

        if (ctx.isServer)
        {
            var players = ctx.networkManager.players;

            for (var i = 0; i < players.Count; i++)
            {
                if (players[i].isServer)
                    continue;

                pm.hierarchy.Create(shooterPrefab, new Vector3(200f + i * 5f, 0f, 0f), Quaternion.identity, players[i]);
            }
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => CountShooters(pm) >= ctx.externalClientCount + (ctx.role == NetworkRole.Host ? 1 : 0),
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"shooters never spawned: {CountShooters(pm)}");
        }

        var mine = FindOwnShooter(ctx);
        mine?.Arm();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => AllShootersDone(pm),
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            LogObservations(ctx);
            return ScenarioResult.Fail($"shooters never finished: {DescribeShooters(pm)}");
        }

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);
        LogObservations(ctx);

        var localFailures = new List<string>();
        var settled = pm.hierarchy.currentState;
        for (var i = 0; i < settled.spawnedPrefabs.Count; i++)
        {
            var record = settled.spawnedPrefabs[i];
            if (record.prefabId != _shooterPrefabId || !record.isRootRecord)
                continue;
            if (!record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter))
            {
                localFailures.Add($"missing shooter {record.instanceId}");
                continue;
            }
            var agreement = shooter.ValidateTickAgreement(shooter == mine, ctx.isServer);
            if (!agreement.success)
                localFailures.Add($"{agreement.message} | {shooter.ObservationDigest()}");
        }

        var sb = new StringBuilder();
        var counter = UnityEngine.Object.FindFirstObjectByType<DeterministicTickCounter>();
        sb.Append(PredictionTestUtils.WorldDigest(ctx, counter));
        PredictionTestUtils.AppendIdentities<TickAgreementShooter>(pm, _shooterPrefabId, sb, shooter => shooter.TickDigest());
        // Every peer reports its settled world even when its local observation oracle
        // fails, so one useful failure cannot turn the other peers into digest timeouts.
        var shared = await DigestExchange.Compare(ctx, DigestChannel, sb.ToString(), 30f);
        if (localFailures.Count == 0)
            return shared;
        if (!shared.success)
            localFailures.Add(shared.message);
        return ScenarioResult.Fail(string.Join(" | ", localFailures));
    }

    private void LogObservations(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId == _shooterPrefabId && record.isRootRecord &&
                record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter))
                Debug.Log($"[TickAgreementInput] {ctx.role} {shooter.ObservationDigest()}");
        }
    }

    private int CountShooters(PredictionManager pm)
    {
        int count = 0;
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            if (state.spawnedPrefabs[i].prefabId == _shooterPrefabId && state.spawnedPrefabs[i].isRootRecord)
                count++;
        }

        return count;
    }

    private bool AllShootersDone(PredictionManager pm)
    {
        bool any = false;
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != _shooterPrefabId || !record.isRootRecord)
                continue;

            any = true;
            if (!record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter) || !shooter.isDone)
                return false;
        }

        return any;
    }

    private string DescribeShooters(PredictionManager pm)
    {
        var sb = new StringBuilder();
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != _shooterPrefabId || !record.isRootRecord)
                continue;

            if (record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter))
                sb.Append('[').Append(shooter.TickDigest()).Append(']');
        }

        return sb.ToString();
    }

    private TickAgreementShooter FindOwnShooter(ScenarioContext ctx)
    {
        var manager = ctx.networkManager;

        if (!manager.isLocalPlayerReady)
            return null;

        var localPlayer = manager.localPlayer;
        var pm = ctx.predictionManager;
        ref var state = ref pm.hierarchy.currentState;

        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var record = state.spawnedPrefabs[i];
            if (record.prefabId != _shooterPrefabId || !record.isRootRecord)
                continue;

            if (record.owner.HasValue && record.owner.Value == localPlayer &&
                record.instanceId.TryGetComponent<TickAgreementShooter>(pm, out var shooter))
            {
                return shooter;
            }
        }

        return null;
    }
}
