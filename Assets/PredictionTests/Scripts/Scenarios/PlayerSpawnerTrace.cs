using System;
using System.Reflection;
using System.Text;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>Opt-in join diagnostics. Reads borrowed state; never copies, disposes or changes it.</summary>
public sealed class PlayerSpawnerTrace : MonoBehaviour
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo SpawnerHistory =
        typeof(DeterministicIdentity<PlayerSpawnerState>).GetField("_stateHistory", Fields);
    private static readonly FieldInfo PlayersInputHistory =
        typeof(PredictedIdentity<PredictedPlayersInput, PredictedPlayersState>).GetField("_inputHistory", Fields);
    private static readonly FieldInfo VerifiedTick = typeof(PredictionManager).GetField("_verifiedServerTick", Fields);

    private PredictionManager _world;
    private PredictedPlayerSpawner _spawner;
    private PredictedPlayers _players;
    private ulong _firstTick;
    private ulong _tickWindow = 600;
    private float _startedAt;
    private int _lines;
    private bool _attached;
    private string _lastState;

    public static void AttachIfRequested(GameObject host, PredictionManager world, PredictedPlayerSpawner spawner)
    {
        if (!CommandLineUtils.HasFlag("-tracePlayerSpawner"))
            return;
        var trace = host.AddComponent<PlayerSpawnerTrace>();
        trace._world = world;
        trace._spawner = spawner;
        trace._startedAt = Time.realtimeSinceStartup;
        if (CommandLineUtils.TryGetArgument("-tracePlayerSpawnerTicks", out var value) &&
            ulong.TryParse(value, out var parsed))
            trace._tickWindow = Math.Min(1800UL, Math.Max(60UL, parsed));
        world.onBeforePhysicsPass += trace.AfterSimulation;
        world.onStartingToRollback += trace.BeforeReconcile;
        world.onRollbackFinished += trace.AfterReconcile;
        world.onLocalDesync += trace.OnLocalDesync;
        world.onDesyncDetected += trace.OnServerDesync;
        trace._attached = true;
        trace.Emit("installed", true);
    }

    private void Update()
    {
        if (!_attached || !_world)
            return;
        BindPlayers();
        if ((_firstTick > 0 && _world.localTick > _firstTick + _tickWindow) ||
            Time.realtimeSinceStartup - _startedAt > 30f || _lines >= 2000)
        {
            Emit("trace-ended", true);
            Detach();
            return;
        }
        Emit("update-state-change", false);
    }

    private void BindPlayers()
    {
        if (!_world || _players == _world.players)
            return;
        if (_players)
        {
            _players.onPlayerAdded -= OnPlayerAdded;
            _players.onPlayerRemoved -= OnPlayerRemoved;
        }
        _players = _world.players;
        if (_players)
        {
            _players.onPlayerAdded += OnPlayerAdded;
            _players.onPlayerRemoved += OnPlayerRemoved;
            if (_firstTick == 0)
                _firstTick = Math.Max(1UL, _world.localTickInContext);
        }
    }

    private void AfterSimulation()
    {
        BindPlayers();
        Emit("after-sim-before-physics", _world.isVerified);
    }

    private void BeforeReconcile() => Emit("before-reconcile", true);
    private void AfterReconcile() => Emit("after-reconcile", true);
    private void OnPlayerAdded(PlayerID player) => Emit($"player-added:{player}", true);
    private void OnPlayerRemoved(PlayerID player) => Emit($"player-removed:{player}", true);
    private void OnLocalDesync(PredictedIdentity identity, ulong tick, DesyncPolicy policy)
    {
        if (identity == _spawner)
            Emit($"local-desync:{tick}:{policy}", true);
    }
    private void OnServerDesync(PredictedIdentity identity, PlayerID player, ulong tick, DesyncPolicy policy)
    {
        if (identity == _spawner)
            Emit($"server-desync:{player}:{tick}:{policy}", true);
    }

    private void Emit(string phase, bool force)
    {
        if (!_world || _lines >= 2001)
            return;
        try
        {
            var state = new StringBuilder(256);
            state.Append("roster=");
            if (_world.players && !_world.players.currentState.players.isDisposed)
            {
                foreach (var player in _world.players.currentState.players)
                    state.Append(player).Append(',');
            }
            else state.Append("uninitialized");
            state.Append(" map=");
            if (_spawner) AppendMap(state, _spawner.currentState);
            else state.Append("missing");
            state.Append(" roots=");
            if (_world.hierarchy && !_world.hierarchy.currentState.spawnedPrefabs.isDisposed)
            {
                foreach (var record in _world.hierarchy.currentState.spawnedPrefabs)
                    if (record.isRootRecord)
                        state.Append(record.owner).Append(':').Append(record.instanceId).Append(',');
            }
            string signature = state.ToString();
            if (!force && signature == _lastState)
                return;
            _lastState = signature;

            ulong tick = _world.localTickInContext;
            var line = new StringBuilder(768);
            line.Append("[PlayerSpawnerTrace] phase=").Append(phase)
                .Append(" frame=").Append(Time.frameCount)
                .Append(" wall=").Append(Time.realtimeSinceStartup.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" server=").Append(_world.isServer)
                .Append(" localPlayer=").Append(_world.localPlayer)
                .Append(" tick=").Append(tick).Append(" head=").Append(_world.localTick)
                .Append(" applied=").Append(VerifiedTick.GetValue(_world))
                .Append(" verified=").Append(_world.isVerified)
                .Append(" replay=").Append(_world.isReplaying)
                .Append(" catchup=").Append(_world.isCatchingUpFrames)
                .Append(" frames=").Append(_world.framesReceivedTotal)
                .Append(" fullFrames=").Append(_world.fullFramesReceivedTotal)
                .Append(" spawnerVerified=").Append(_spawner ? _spawner.lastVerifiedTick : null)
                .Append(' ').Append(signature);
            AppendInput(line, tick);
            AppendSnapshot(line, tick);
            AppendSnapshot(line, tick + 1);
            _lines++;
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, this, "{0}", line.ToString());
        }
        catch (Exception error)
        {
            // A diagnostic must not interrupt a prediction pass or mutate the subject.
            Debug.LogWarning($"[PlayerSpawnerTrace] invalid diagnostic: {error}");
            Detach();
        }
    }

    private void AppendInput(StringBuilder line, ulong tick)
    {
        var history = _world.players
            ? PlayersInputHistory.GetValue(_world.players) as History<PredictedPlayersInput>
            : null;
        line.Append(" rosterInput[").Append(tick).Append("]=");
        if (history == null || !history.Read(tick, out var input))
        {
            line.Append("absent");
            return;
        }
        line.Append("add:");
        if (!input.addPlayers.isDisposed)
            foreach (var player in input.addPlayers) line.Append(player).Append(',');
        line.Append("/remove:");
        if (!input.removePlayers.isDisposed)
            foreach (var player in input.removePlayers) line.Append(player).Append(',');
    }

    private void AppendSnapshot(StringBuilder line, ulong tick)
    {
        line.Append(" spawnerHistory[").Append(tick).Append("]=");
        var history = _spawner ? SpawnerHistory.GetValue(_spawner) : null;
        if (history == null)
        {
            line.Append("uninitialized");
            return;
        }
        // FULL_STATE is internal; reflection reads the existing owner without copying or disposal.
        var args = new object[] { tick, null };
        if (!(bool)history.GetType().GetMethod("Read").Invoke(history, args))
        {
            line.Append("absent");
            return;
        }
        var snapshot = args[1];
        var type = snapshot.GetType();
        AppendMap(line, (PlayerSpawnerState)type.GetField("state").GetValue(snapshot));
        var metadata = (PredictedIdentityState)type.GetField("prediction").GetValue(snapshot);
        line.Append("/started:").Append(metadata.wasOnSimulationStartCalled);
    }

    private static void AppendMap(StringBuilder line, PlayerSpawnerState state)
    {
        line.Append('[');
        if (state.values.isDisposed) line.Append("disposed");
        else
            foreach (var pair in state.values)
                line.Append(pair.playerID).Append(':').Append(pair.objectID).Append(',');
        line.Append("]/spawnPoint:").Append(state.spawnPointIndex);
    }

    private void OnDestroy() => Detach();

    private void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        if (_world)
        {
            _world.onBeforePhysicsPass -= AfterSimulation;
            _world.onStartingToRollback -= BeforeReconcile;
            _world.onRollbackFinished -= AfterReconcile;
            _world.onLocalDesync -= OnLocalDesync;
            _world.onDesyncDetected -= OnServerDesync;
        }
        if (_players)
        {
            _players.onPlayerAdded -= OnPlayerAdded;
            _players.onPlayerRemoved -= OnPlayerRemoved;
        }
    }
}
