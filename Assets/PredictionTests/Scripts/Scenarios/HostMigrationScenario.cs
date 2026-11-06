using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;

public static class HostMigrationSignals
{
    public static ulong promotedId;
    public static ushort newPort;
    // Relay mode: the old host's authorization for a graceful handoff, empty when the host is lost instead.
    public static string relayAuthorization;
    public static bool planReceived;
    public static int planAcks;
    public static bool goReceived;

    public static void Reset()
    {
        promotedId = 0;
        newPort = 0;
        relayAuthorization = null;
        planReceived = false;
        planAcks = 0;
        goReceived = false;
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastRelayPlan(ulong playerId, string authorization)
    {
        relayAuthorization = authorization;
        promotedId = playerId;
        planReceived = true;
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastPlan(ulong playerId, ushort port)
    {
        promotedId = playerId;
        newPort = port;
        planReceived = true;
    }

    [ServerRpc(requireOwnership: false)]
    public static void AckPlan()
    {
        planAcks++;
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastGo()
    {
        goReceived = true;
    }
}

/// <summary>
/// Run with -role server: the old server picks the lowest client, which promotes itself to host with
/// NetworkManager.PromoteToServer while every other client follows with TransferToNewServer. The promoted
/// host must continue the match: every object keeps its id and owner, the pawns of players that are still
/// reconnecting survive (the scene spawner destroys pawns on disconnect), owner input keeps flowing to the
/// new server, deterministic systems keep counting and every peer ends with the same world.
/// </summary>
public class HostMigrationScenario : Scenario
{
    private const int PrePlanBarrier = 470;
    private const int DigestChannel = 520;
    private const ushort PortOffset = 91;
    private const long MinPostMigrationSumGain = 20;

    [SerializeField] private float _timeout = 120f;
    [SerializeField] private float _migrationTimeout = 60f;
    [SerializeField] private float _settleSeconds = 3f;

    // -hostMigrationLostHost: the old server drops everyone instead of letting the clients leave; the clients only
    // move once NetworkManager.onClientDisconnected reports a disconnect they did not ask for.
    private bool _lostHost;
    // -hostMigrationRetry: the highest transferring client first aims at a port nobody listens on. With
    // keepClientStateOnFailedHostMigration the failed attempt must keep its world for the second attempt.
    private bool _retry;
    // -hostMigrationSceneGap: before the migration the server loads MigrationGapScene (a second PredictionManager)
    // through the scenes module. A player rejoining the promoted host only observes that PredictionManager once its
    // ClientFinishedLoadingScene arrives, so the promoted host sees every returning player before it observes. Its
    // PredictedPlayers must keep them through that gap.
    private bool _sceneGap;

    private const string GapSceneName = "MigrationGapScene";

    private DeterministicTickCounter _counter;
    private DeterministicTimedSpawner _spawner;
    private static GameObject _pawnPrefab;
    private static GameObject _markerPrefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        HostMigrationSignals.Reset();
        _lostHost = CommandLineUtils.HasFlag("-hostMigrationLostHost");
        _retry = CommandLineUtils.HasFlag("-hostMigrationRetry");
        _sceneGap = CommandLineUtils.HasFlag("-hostMigrationSceneGap");

        _counter = FindFirstObjectByType<DeterministicTickCounter>();
        _spawner = FindFirstObjectByType<DeterministicTimedSpawner>();

        if (manager.networkRules)
        {
            JsonUtility.FromJsonOverwrite(
                "{\"_hostMigrationRules\":{\"migrateAsHost\":true,\"sharePlayerCookiesWithPeers\":true}}",
                manager.networkRules);
        }

        if (!_markerPrefab)
            _markerPrefab = PredictionTestUtils.CreatePrefab<PredictedMarker>("MigrationMarker");
        PredictionTestUtils.RegisterPrefab(ctx, _markerPrefab);
        if (_spawner)
            _spawner.markerPrefab = _markerPrefab;

        if (!_pawnPrefab)
            _pawnPrefab = PredictionTestUtils.CreatePrefab<MigrationPawn>("MigrationPawn");
        PredictionTestUtils.RegisterPrefab(ctx, _pawnPrefab);

        var playerSpawner = FindAnyObjectByType<PredictedPlayerSpawner>();
        if (playerSpawner)
            playerSpawner.playerPrefab = _pawnPrefab;
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        if (ctx.role == NetworkRole.Host)
            return ScenarioResult.Fail("host mode is not supported; run with -role server");

        if (!_counter || !_spawner)
            return ScenarioResult.Fail("scene is missing DeterministicTickCounter or DeterministicTimedSpawner");

        if (!FindAnyObjectByType<PredictedPlayerSpawner>())
            return ScenarioResult.Fail("scene is missing PredictedPlayerSpawner");

        return ctx.isServer ? await RunAsOldServer(ctx) : await RunAsClient(ctx);
    }

    private bool WorldSettled(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        return _spawner.spawnedCount >= _spawner.spawnsPerWave &&
               _spawner.spawnedCount < _spawner.totalSpawns &&
               PawnOwners(pm).Count >= ctx.expectedConnections;
    }

    private async UniTask<ScenarioResult> RunAsOldServer(ScenarioContext ctx)
    {
        var manager = ctx.networkManager;

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => WorldSettled(ctx), _timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"pre-migration world never settled: spawned={_spawner.spawnedCount}/{_spawner.totalSpawns} pawns={PawnOwners(ctx.predictionManager).Count}/{ctx.expectedConnections}");
        }

        if (_sceneGap)
        {
            manager.sceneModule.LoadSceneAsync(GapSceneName, new PurrSceneSettings
            {
                mode = UnityEngine.SceneManagement.LoadSceneMode.Additive,
                physicsMode = UnityEngine.SceneManagement.LocalPhysicsMode.Physics3D,
                isPublic = true
            });

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => GapManager() is { isSpawned: true } gap &&
                          gap.observers.Count >= ctx.expectedConnections,
                    _timeout,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                var gap = GapManager();
                return ScenarioResult.Fail(
                    $"clients never observed the {GapSceneName} PredictionManager: found={gap != null} observers={(gap ? gap.observers.Count : 0)}/{ctx.expectedConnections}");
            }
        }

        await ScenarioBarrier.Wait(ctx, PrePlanBarrier, _timeout);

        var promoted = PickPromoted(ctx);
        if (!promoted.HasValue)
            return ScenarioResult.Fail("no eligible client to promote");

        if (manager.transport is PurrTransport relay)
        {
            // A planned handoff over the relay needs the host to authorize the next host; a lost host does not.
            string authorization = "";
            if (!_lostHost)
            {
                try
                {
                    authorization = await relay.AuthorizeHostMigrationAsync(promoted.Value);
                }
                catch (Exception e)
                {
                    return ScenarioResult.Fail($"the relay refused to authorize the handoff: {e.Message}");
                }
            }

            HostMigrationSignals.BroadcastRelayPlan(promoted.Value.id.value, authorization);
        }
        else if (manager.transport is UDPTransport udp)
            HostMigrationSignals.BroadcastPlan(promoted.Value.id.value, (ushort)(udp.serverPort + PortOffset));
        else
            return ScenarioResult.Fail("scenario requires UDPTransport or PurrTransport");

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => HostMigrationSignals.planAcks >= ctx.expectedConnections,
                _migrationTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"only {HostMigrationSignals.planAcks}/{ctx.expectedConnections} clients acked the migration plan");
        }

        HostMigrationSignals.BroadcastGo();

        if (_lostHost)
        {
            // Give the go time to land, then vanish like a crashed host.
            await UniTask.WaitForSeconds(1f, cancellationToken: ctx.cancellationToken);
            manager.StopServer();
            return ScenarioResult.Ok();
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => manager.playerCount == 0, _migrationTimeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"{manager.playerCount} clients never left the old server");
        }

        return ScenarioResult.Ok();
    }

    private async UniTask<ScenarioResult> RunAsClient(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var manager = ctx.networkManager;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => WorldSettled(ctx) && LocalPawnSum(ctx).HasValue,
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"pre-migration world never settled: spawned={_spawner.spawnedCount}/{_spawner.totalSpawns} pawns={PawnOwners(pm).Count}/{ctx.expectedConnections} ownPawn={LocalPawnSum(ctx).HasValue}");
        }

        PredictionManager gapManager = null;
        if (_sceneGap)
        {
            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => GapManager() is { isSpawned: true, players: not null } gap &&
                          Predicts(gap, manager.localPlayer),
                    _timeout,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail($"the {GapSceneName} PredictionManager never predicted this player");
            }

            gapManager = GapManager();
        }

        await ScenarioBarrier.Wait(ctx, PrePlanBarrier, _timeout);

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => HostMigrationSignals.planReceived, _migrationTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("migration plan never arrived");
        }

        bool isPromoted = manager.isLocalPlayerReady && manager.localPlayer.id.value == HostMigrationSignals.promotedId;
        bool retries = _retry && !isPromoted && IsHighestPlayer(manager);

        // Keep the world through the coming disconnect, as a migration system does while the host is healthy.
        manager.PreserveClientStateForHostMigration();
        ClientDisconnectInfo? lostConnection = null;
        void OnDisconnected(ClientDisconnectInfo info) => lostConnection ??= info;
        manager.onClientDisconnected += OnDisconnected;

        HostMigrationSignals.AckPlan();

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => HostMigrationSignals.goReceived, _migrationTimeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("migration go never arrived");
        }

        // Snapshot after go: the old server keeps simulating until everyone has left.
        var preOwners = PawnOwners(pm);
        var preCounter = _counter.currentState.count;
        var preSum = LocalPawnSum(ctx) ?? 0;

        if (_lostHost)
        {
            try
            {
                await UniTaskUtils.WaitWithTimeout(() => lostConnection.HasValue, _migrationTimeout,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                manager.onClientDisconnected -= OnDisconnected;
                return ScenarioResult.Fail("the old host never dropped this client");
            }

            if (lostConnection.Value.wasLocalRequest)
            {
                manager.onClientDisconnected -= OnDisconnected;
                return ScenarioResult.Fail(
                    $"losing the host was reported as a local disconnect (reason {lostConnection.Value.reason})");
            }

            if (PawnOwners(pm).Count != preOwners.Count)
            {
                manager.onClientDisconnected -= OnDisconnected;
                return ScenarioResult.Fail("the preserved client state did not survive losing the host");
            }
        }
        manager.onClientDisconnected -= OnDisconnected;
        bool promotedEventRaised = false;
        void OnPromoted() => promotedEventRaised = true;
        pm.onPromotedToServer += OnPromoted;

        // Every player comes back, so the promoted host must never drop one from PredictedPlayers, including in
        // the gap between a player rejoining and observing the PredictionManager.
        var removedPlayers = new List<PlayerID>();
        void OnPlayerRemoved(PlayerID player) => removedPlayers.Add(player);
        var predictedPlayers = pm.players;
        if (isPromoted && predictedPlayers)
            predictedPlayers.onPlayerRemoved += OnPlayerRemoved;

        // Scene gap: count the players that rejoin before they observe the scene-loaded PredictionManager, so a
        // pass proves the gap was crossed rather than skipped.
        var gapRemovedPlayers = new List<PlayerID>();
        void OnGapPlayerRemoved(PlayerID player) => gapRemovedPlayers.Add(player);
        int returnedBeforeObserving = 0;
        void OnPlayerReturned(PlayerID player)
        {
            if (gapManager && !gapManager.IsObserver(player))
                returnedBeforeObserving++;
        }

        var gapPlayers = gapManager ? gapManager.players : null;
        if (isPromoted && gapPlayers)
        {
            gapPlayers.onPlayerRemoved += OnGapPlayerRemoved;
            manager.onHostMigrationPlayerReturned += OnPlayerReturned;
        }

        manager.hostMigrationTimeout = _migrationTimeout;

        if (manager.transport is PurrTransport relayTransport)
        {
            var relayed = await MigrateOverRelay(ctx, relayTransport, isPromoted);
            if (relayed != null)
                return ScenarioResult.Fail(relayed);
        }
        else if (manager.transport is not UDPTransport udp)
            return ScenarioResult.Fail("scenario requires UDPTransport or PurrTransport");
        else if (isPromoted)
        {
            udp.serverPort = HostMigrationSignals.newPort;
            udp.maxConnections = Mathf.Max(udp.maxConnections, ctx.expectedConnections + 8);
            manager.PromoteToServer();
        }
        else
        {
            udp.serverPort = HostMigrationSignals.newPort;
            await UniTask.WaitForSeconds(0.5f, cancellationToken: ctx.cancellationToken);

            if (retries)
            {
                var failed = await FailOneTransfer(ctx, udp, preOwners);
                if (failed != null)
                    return ScenarioResult.Fail(failed);
                udp.serverPort = HostMigrationSignals.newPort;
                manager.hostMigrationTimeout = _migrationTimeout;
            }

            manager.TransferToNewServer();
        }

        var post = ctx;
        post.role = isPromoted ? NetworkRole.Host : NetworkRole.Client;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => !manager.isPromotingToServer && !manager.isTranferingToNewServer &&
                      manager.isClient && manager.isLocalPlayerReady && (!isPromoted || manager.isServer),
                _migrationTimeout,
                post.cancellationToken);

            if (isPromoted)
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => manager.playerCount >= ctx.expectedConnections,
                    _migrationTimeout,
                    post.cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            pm.onPromotedToServer -= OnPromoted;
            return ScenarioResult.Fail(
                $"{(isPromoted ? "promotion" : "transfer")} never completed: server={manager.isServer} client={manager.isClient} ready={manager.isLocalPlayerReady} players={manager.playerCount}/{ctx.expectedConnections} promoting={manager.isPromotingToServer} transferring={manager.isTranferingToNewServer}");
        }

        pm.onPromotedToServer -= OnPromoted;
        if (isPromoted && !promotedEventRaised)
            return ScenarioResult.Fail("PredictionManager.onPromotedToServer was not raised on the promoted host");

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => LocalPawnSum(post) >= preSum + MinPostMigrationSumGain,
                _timeout,
                post.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"own pawn stopped advancing after migration: sum={LocalPawnSum(post)?.ToString() ?? "missing"} pre={preSum} owners={Describe(PawnOwners(pm))}");
        }

        var postOwners = PawnOwners(pm);
        if (!SameOwners(preOwners, postOwners))
        {
            return ScenarioResult.Fail(
                $"pawns changed across migration: before={Describe(preOwners)} after={Describe(postOwners)}");
        }

        if (_counter.currentState.count < preCounter)
        {
            return ScenarioResult.Fail(
                $"deterministic counter regressed across migration: {_counter.currentState.count} < {preCounter}");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => _spawner.spawnedCount >= _spawner.totalSpawns, _timeout,
                post.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"post-migration wave never completed: spawned={_spawner.spawnedCount}/{_spawner.totalSpawns}");
        }

        if (isPromoted && predictedPlayers)
        {
            predictedPlayers.onPlayerRemoved -= OnPlayerRemoved;
            if (removedPlayers.Count > 0)
            {
                return ScenarioResult.Fail(
                    $"the promoted host removed returning players from PredictedPlayers: {string.Join(", ", removedPlayers)}");
            }
        }

        if (_sceneGap)
        {
            if (!gapManager)
                return ScenarioResult.Fail($"the {GapSceneName} PredictionManager did not survive the migration");

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => isPromoted
                        ? gapManager.observers.Count >= ctx.expectedConnections
                        : Predicts(gapManager, manager.localPlayer),
                    _timeout,
                    post.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail(
                    $"returning players never observed the {GapSceneName} PredictionManager again: observers={gapManager.observers.Count}/{ctx.expectedConnections} predicted={(gapPlayers ? gapPlayers.players.Count : 0)}");
            }

            if (isPromoted && gapPlayers)
            {
                gapPlayers.onPlayerRemoved -= OnGapPlayerRemoved;
                manager.onHostMigrationPlayerReturned -= OnPlayerReturned;

                if (gapRemovedPlayers.Count > 0)
                {
                    return ScenarioResult.Fail(
                        $"the promoted host removed returning players from the {GapSceneName} PredictedPlayers during the rejoin gap: {string.Join(", ", gapRemovedPlayers)} (returned before observing: {returnedBeforeObserving})");
                }

                // Every other peer comes back through that scene load.
                if (returnedBeforeObserving < ctx.expectedConnections - 1)
                {
                    return ScenarioResult.Fail(
                        $"only {returnedBeforeObserving}/{ctx.expectedConnections - 1} returning players rejoined before observing the {GapSceneName} PredictionManager; the scenario did not exercise the rejoin gap");
                }

                Debug.Log(
                    $"[HostMigrationScenario] scene gap: {returnedBeforeObserving} players rejoined before observing {GapSceneName}, none removed; predicted={gapPlayers.players.Count}");
            }
        }

        await UniTask.WaitForSeconds(_settleSeconds, cancellationToken: post.cancellationToken);
        await PredictionTestUtils.AlignDigestTick(post, DigestChannel, _timeout);

        var digest = PredictionTestUtils.WorldDigest(post, _counter);
        var compare = await DigestExchange.Compare(post, DigestChannel, digest, 30f);

        if (isPromoted)
        {
            // The new host ends the run. Stay up until the other peers have it, or the simulated latency
            // swallows the broadcast when this process quits.
            ScenarioSequencer.IssueSequenceComplete();
            try
            {
                await UniTaskUtils.WaitWithTimeout(() => manager.playerCount <= 1, 30f, post.cancellationToken);
            }
            catch (TimeoutException)
            {
            }
        }

        return compare;
    }

    // The elected client claims the relay room (with the host's authorization for a handoff, without one for a
    // lost host) and promotes; the others reconnect and pick up the rotated secret from fresh join details.
    private async UniTask<string> MigrateOverRelay(ScenarioContext ctx, PurrTransport relay, bool isPromoted)
    {
        var manager = ctx.networkManager;
        if (!isPromoted)
        {
            await UniTask.WaitForSeconds(0.5f, cancellationToken: ctx.cancellationToken);
            manager.TransferToNewServer();
            return null;
        }

        var authorization = string.IsNullOrEmpty(HostMigrationSignals.relayAuthorization)
            ? null
            : HostMigrationSignals.relayAuthorization;

        // A lost host may not have reached the relay yet; the relay says so with host_connected.
        var giveUpAt = Time.realtimeSinceStartupAsDouble + 15d;
        while (true)
        {
            try
            {
                var claimed = await relay.ClaimHostMigrationAsync(manager.localPlayer, null, authorization);
                Debug.Log($"[HostMigrationScenario] claimed the relay room, generation {claimed.generation}");
                break;
            }
            catch (RelayMigrationRejectedException rejected) when (rejected.code == "host_connected" &&
                                                                    authorization == null &&
                                                                    Time.realtimeSinceStartupAsDouble < giveUpAt)
            {
                await UniTask.WaitForSeconds(0.5f, cancellationToken: ctx.cancellationToken);
            }
            catch (Exception e)
            {
                return $"the relay claim failed: {e.Message}";
            }
        }

        manager.PromoteToServer();
        return null;
    }

    // Transfers to a port nobody listens on. The attempt must give up and keep the old world for the next one.
    private async UniTask<string> FailOneTransfer(ScenarioContext ctx, UDPTransport udp, Dictionary<ulong, ulong> preOwners)
    {
        var manager = ctx.networkManager;
        var pm = ctx.predictionManager;

        udp.serverPort = (ushort)(HostMigrationSignals.newPort + 1);
        manager.hostMigrationTimeout = 3f;
        manager.keepClientStateOnFailedHostMigration = true;
        manager.TransferToNewServer();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => manager.isTranferingToNewServer, 5f, ctx.cancellationToken);
            await UniTaskUtils.WaitWithTimeout(
                () => !manager.isTranferingToNewServer && manager.clientState == ConnectionState.Disconnected,
                _migrationTimeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return "the transfer to a dead port never gave up";
        }

        if (!manager.isClientStatePreservedForHostMigration)
            return "a failed transfer did not keep the client state preserved";

        var kept = PawnOwners(pm);
        if (!SameOwners(preOwners, kept))
            return $"a failed transfer lost the world: before={Describe(preOwners)} after={Describe(kept)}";

        return null;
    }

    private static PredictionManager GapManager()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(GapSceneName);
        if (!scene.IsValid() || !scene.isLoaded)
            return null;

        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.TryGetComponent<PredictionManager>(out var gap))
                return gap;
        }

        return null;
    }

    private static bool Predicts(PredictionManager gap, PlayerID player)
    {
        var players = gap.players.players;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] == player)
                return true;
        }
        return false;
    }

    private static bool IsHighestPlayer(NetworkManager manager)
    {
        var local = manager.localPlayer.id.value;
        var players = manager.players;
        for (int i = 0; i < players.Count; i++)
        {
            if (!players[i].isBot && players[i].id.value > local)
                return false;
        }
        return true;
    }

    private static Dictionary<ulong, ulong> PawnOwners(PredictionManager pm)
    {
        var owners = new Dictionary<ulong, ulong>();
        if (!pm.hierarchy || !pm.TryGetPrefab(_pawnPrefab, out var pawnPrefabId))
            return owners;

        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != pawnPrefabId)
                continue;
            owners[details.instanceId.instanceId.value] = details.owner.HasValue ? details.owner.Value.id.value : 0;
        }

        return owners;
    }

    private static long? LocalPawnSum(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        var manager = ctx.networkManager;
        if (!manager.isLocalPlayerReady || !pm.hierarchy || !pm.TryGetPrefab(_pawnPrefab, out var pawnPrefabId))
            return null;

        var local = manager.localPlayer.id.value;
        ref var state = ref pm.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != pawnPrefabId || !details.owner.HasValue || details.owner.Value.id.value != local)
                continue;

            PredictedObjectID? id = details.instanceId;
            if (id.TryGetComponent<MigrationPawn>(pm, out var pawn))
                return pawn.currentState.sum;
        }

        return null;
    }

    private static bool SameOwners(Dictionary<ulong, ulong> a, Dictionary<ulong, ulong> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (instance, owner) in a)
        {
            if (!b.TryGetValue(instance, out var other) || other != owner)
                return false;
        }
        return true;
    }

    private static string Describe(Dictionary<ulong, ulong> owners)
    {
        var sb = new StringBuilder();
        foreach (var (instance, owner) in owners)
            sb.Append('[').Append(instance).Append("->").Append(owner).Append(']');
        return sb.ToString();
    }

    private static PlayerID? PickPromoted(ScenarioContext ctx)
    {
        PlayerID? best = null;
        var players = ctx.networkManager.players;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.isBot)
                continue;
            if (!best.HasValue || p.id.value < best.Value.id.value)
                best = p;
        }

        return best;
    }
}
