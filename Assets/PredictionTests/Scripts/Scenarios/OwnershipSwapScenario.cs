using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>A body the player possesses. An input click hands control to the partner body inside Simulate.</summary>
public sealed class OwnershipSwapPawn : PredictedIdentity<OwnershipSwapPawn.SwapInput, OwnershipSwapPawn.SwapState>
{
    public static readonly Dictionary<uint, uint> partners = new();

    // Bumped by the test outside the simulation; only input carries it into Simulate.
    public uint swapClicks;

    // Diagnostics, not state: ticks the server simulated with the owner's live input.
    public int serverInputTicks;

    public struct SwapInput : IPredictedData
    {
        public uint swaps;
        public int step;
        public void Dispose() { }
    }

    public struct SwapState : IPredictedData<SwapState>
    {
        public uint handledSwaps;
        public long sum;
        public void Dispose() { }
    }

    public long sum => currentState.sum;
    public uint handledSwaps => currentState.handledSwaps;

    protected override void UpdateInput(ref SwapInput input) => input.swaps = swapClicks;

    protected override void GetFinalInput(ref SwapInput input) => input.step = 1;

    protected override void Simulate(SwapInput input, ref SwapState state, float delta)
    {
        state.sum += input.step;
        if (input.step != 0 && predictionManager.isServer)
            serverInputTicks++;

        if (state.handledSwaps >= input.swaps)
            return;

        state.handledSwaps = input.swaps;

        if (!owner.HasValue || !partners.TryGetValue(rootObjectId.instanceId.value, out var partner))
            return;

        // The report's possession swap: take the other body, release this one.
        predictionManager.SetOwnership(new PredictedObjectID(partner), owner);
        predictionManager.SetOwnership(rootObjectId, null);
    }
}

public static class OwnershipSwapSignals
{
    private static readonly ScenarioStateLedger<Dictionary<ulong, (uint a, uint b)>> Pairs = new();
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, string>> Reports = new();

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void AssignPairs(int scenarioIndex, ulong[] players, uint[] first, uint[] second)
    {
        var map = new Dictionary<ulong, (uint, uint)>();
        for (var i = 0; i < players.Length; i++)
        {
            map[players[i]] = (first[i], second[i]);
            OwnershipSwapPawn.partners[first[i]] = second[i];
            OwnershipSwapPawn.partners[second[i]] = first[i];
        }
        Pairs.TryAdd(scenarioIndex, 0, map);
    }

    public static bool TryGetPair(int scenarioIndex, PlayerID player, out (uint a, uint b) pair)
    {
        pair = default;
        return Pairs.TryGet(scenarioIndex, 0, out var map) && map.TryGetValue(player.id.value, out pair);
    }

    [ServerRpc(requireOwnership: false)]
    public static void Report(int scenarioIndex, string message, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex)) return;
        if (!Reports.TryGet(scenarioIndex, 0, out var reports))
        {
            reports = new Dictionary<PlayerID, string>();
            Reports.TryAdd(scenarioIndex, 0, reports);
        }
        reports[info.sender] = message;
    }

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void AssignShips(int scenarioIndex, ulong[] players, uint[] roots)
    {
        var map = new Dictionary<ulong, (uint, uint)>();
        for (var i = 0; i < players.Length; i++)
            map[players[i]] = (roots[i], roots[i]);
        Pairs.TryAdd(scenarioIndex, 0, map);
    }

    public static Action<int, PlayerID, int> onTurretRequest;

    [ServerRpc(requireOwnership: false)]
    public static void RequestTurret(int scenarioIndex, int slot, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex)) return;
        onTurretRequest?.Invoke(scenarioIndex, info.sender, slot);
    }

    public static bool TryGetReports(int scenarioIndex, out Dictionary<PlayerID, string> reports)
        => Reports.TryGet(scenarioIndex, 0, out reports);
}

/// <summary>
/// Discord report (Tekla, 2026-11-12): a predicted player swaps bodies from Simulate with
/// SetOwnership(other, owner) + SetOwnership(self, null). The server follows, the client ends up
/// controlling nothing and the server rejects its input. Every player swaps back and forth between two
/// bodies and must keep control of exactly one, with its input reaching the server each time.
/// </summary>
public sealed class OwnershipSwapScenario : Scenario
{
    private const float Timeout = 20f;
    private const float WatchSeconds = 2f;
    private const int Rounds = 3;

    private GameObject _pawnPrefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _pawnPrefab = PredictionTestUtils.CreatePrefab<OwnershipSwapPawn>("OwnershipSwapPawn");
        PredictionTestUtils.RegisterPrefab(ctx, _pawnPrefab);
    }

    public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
        => RunSplit(ctx, RunClient, RunServer);

    private async UniTask<ScenarioResult> RunServer(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;

        var warnings = new List<string>();
        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning && message.Contains("Ignoring input from"))
                warnings.Add(message);
        }

        var players = ctx.networkManager.players;
        var playerIds = new ulong[players.Count];
        var first = new uint[players.Count];
        var second = new uint[players.Count];
        for (var i = 0; i < players.Count; i++)
        {
            var a = world.hierarchy.Create(_pawnPrefab, new Vector3(160, 0, i * 2f), Quaternion.identity, players[i]);
            var b = world.hierarchy.Create(_pawnPrefab, new Vector3(162, 0, i * 2f), Quaternion.identity);
            if (!a.HasValue || !b.HasValue)
                return ScenarioResult.Fail("pawn creation failed");
            playerIds[i] = players[i].id.value;
            first[i] = a.Value.instanceId.value;
            second[i] = b.Value.instanceId.value;
        }

        var failures = new List<string>();
        var lines = new List<string>();

        Application.logMessageReceived += OnLog;
        try
        {
            OwnershipSwapSignals.AssignPairs(ctx.scenarioIndex, playerIds, first, second);

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => OwnershipSwapSignals.TryGetReports(ctx.scenarioIndex, out var reports) &&
                          reports.Count >= ctx.expectedConnections,
                    Timeout * (Rounds + 2), ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail("not every client reported");
            }

            OwnershipSwapSignals.TryGetReports(ctx.scenarioIndex, out var all);

            // An odd number of swaps leaves every player in its second body.
            var before = new int[players.Count];
            for (var i = 0; i < players.Count; i++)
                before[i] = new PredictedObjectID(second[i]).TryGetComponent<OwnershipSwapPawn>(world, out var pawn) ? pawn.serverInputTicks : -1;

            await UniTask.Delay(TimeSpan.FromSeconds(1f), cancellationToken: ctx.cancellationToken);

            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                all.TryGetValue(player, out var report);
                new PredictedObjectID(first[i]).TryGetComponent<OwnershipSwapPawn>(world, out var a);
                new PredictedObjectID(second[i]).TryGetComponent<OwnershipSwapPawn>(world, out var b);

                int inputTicks = b ? b.serverInputTicks - before[i] : 0;
                string serverView = $"server a.owner={Owner(a)} b.owner={Owner(b)} b input ticks in 1s={inputTicks}";
                lines.Add($"{player}: {report}; {serverView}");

                if (report == null || !report.StartsWith("ok"))
                    failures.Add($"{player}: {report}");
                if (!a || !b || a.owner.HasValue || b.owner != player || inputTicks <= 0)
                    failures.Add($"{player}: {serverView}");
            }
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
        }

        if (warnings.Count > 0)
            failures.Add($"{warnings.Count} input warnings, first='{warnings[0]}'");

        string summary = $"{string.Join(" | ", lines)} | server input warnings={warnings.Count}";
        Debug.Log($"[OwnershipSwapScenario] server: {summary}");

        return failures.Count == 0
            ? ScenarioResult.Ok(summary)
            : ScenarioResult.Fail($"{string.Join(" | ", failures)} || {summary}");
    }

    private static string Owner(OwnershipSwapPawn pawn)
        => !pawn ? "missing" : pawn.owner?.ToString() ?? "none";

    private async UniTask<ScenarioResult> RunClient(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        var me = ctx.networkManager.localPlayer;
        (uint a, uint b) pair = default;
        OwnershipSwapPawn a = null, b = null;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => OwnershipSwapSignals.TryGetPair(ctx.scenarioIndex, me, out pair) &&
                      new PredictedObjectID(pair.a).TryGetComponent(world, out a) && a.isOwner &&
                      new PredictedObjectID(pair.b).TryGetComponent(world, out b),
                Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return Finish(ctx, "no owned pawn pair arrived");
        }

        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: ctx.cancellationToken);

        var notes = new List<string>();
        for (var round = 0; round < Rounds; round++)
        {
            var from = round % 2 == 0 ? a : b;
            var to = round % 2 == 0 ? b : a;

            from.swapClicks++;

            try
            {
                await UniTaskUtils.WaitWithTimeout(() => to.isOwner && !from.isOwner, 5f, ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return Finish(ctx, $"round {round}: the swap never showed: from.owner={Owner(from)} to.owner={Owner(to)}");
            }

            long sumAtStart = to.sum;
            int lostFrames = 0;
            int frames = 0;
            double until = Time.realtimeSinceStartupAsDouble + WatchSeconds;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                frames++;
                if (!to.isOwner || from.isOwner)
                    lostFrames++;
                await UniTask.Yield(ctx.cancellationToken);
            }

            long advanced = to.sum - sumAtStart;
            notes.Add($"r{round} lost={lostFrames}/{frames} advanced={advanced}");

            if (!to.isOwner || from.isOwner)
                return Finish(ctx, $"round {round}: control lost after the swap: from.owner={Owner(from)} to.owner={Owner(to)} ({string.Join(", ", notes)})");
            if (advanced <= 0)
                return Finish(ctx, $"round {round}: the new body does not take input ({string.Join(", ", notes)})");
        }

        return Finish(ctx, $"ok {string.Join(", ", notes)}");
    }

    private static ScenarioResult Finish(ScenarioContext ctx, string message)
    {
        Debug.Log($"[OwnershipSwapScenario] client: {message}");
        OwnershipSwapSignals.Report(ctx.scenarioIndex, message);
        return message.StartsWith("ok") ? ScenarioResult.Ok(message) : ScenarioResult.Fail(message);
    }
}

/// <summary>
/// Discord report (Prakkus, 2026-11-13): a ship with two nested turret identities, all unowned. The
/// server alone hands a turret to a client by setting <c>owner</c> from an RPC, outside the simulation.
/// The first turret works; after moving to the second the client believes it owns it, but keeps
/// uploading input for the first and the server rejects it. Every player hops between its two turrets
/// and the server must simulate the input the player typed into each one.
/// </summary>
public sealed class ServerOwnershipSwapScenario : Scenario
{
    private const float Timeout = 20f;
    private const float WatchSeconds = 2f;
    private const int Rounds = 4;

    private GameObject _shipPrefab;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _shipPrefab = PredictionTestUtils.CreatePrefab<OwnershipSwapPawn>("OwnershipSwapShip");
        for (var i = 0; i < 2; i++)
        {
            var turret = new GameObject(i == 0 ? "Left Turret" : "Right Turret");
            turret.transform.SetParent(_shipPrefab.transform, false);
            turret.AddComponent<OwnershipSwapPawn>();
        }
        PredictionTestUtils.RegisterPrefab(ctx, _shipPrefab);
    }

    public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
        => RunSplit(ctx, RunClient, RunServer);

    private static bool TryGetTurrets(PredictionManager world, uint root, out OwnershipSwapPawn[] pawns)
    {
        pawns = null;
        if (!new PredictedObjectID(root).TryGetComponent<OwnershipSwapPawn>(world, out var ship))
            return false;
        pawns = ship.transform.root.GetComponentsInChildren<OwnershipSwapPawn>(true);
        return pawns.Length == 3;
    }

    private async UniTask<ScenarioResult> RunServer(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;

        var warnings = new List<string>();
        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Warning && message.Contains("Ignoring input from"))
                warnings.Add(message);
        }

        var players = ctx.networkManager.players;
        var playerIds = new ulong[players.Count];
        var roots = new uint[players.Count];
        var ships = new Dictionary<PlayerID, OwnershipSwapPawn[]>();
        for (var i = 0; i < players.Count; i++)
        {
            var ship = world.hierarchy.Create(_shipPrefab, new Vector3(170, 0, i * 2f), Quaternion.identity);
            if (!ship.HasValue || !TryGetTurrets(world, ship.Value.instanceId.value, out var pawns))
                return ScenarioResult.Fail("ship creation failed");
            playerIds[i] = players[i].id.value;
            roots[i] = ship.Value.instanceId.value;
            ships[players[i]] = pawns;
        }

        int index = ctx.scenarioIndex;
        void OnRequest(int scenarioIndex, PlayerID sender, int slot)
        {
            if (scenarioIndex != index || !ships.TryGetValue(sender, out var pawns))
                return;
            // Server only, outside the simulation, exactly like the report.
            pawns[slot == 0 ? 2 : 1].owner = null;
            pawns[slot == 0 ? 1 : 2].owner = sender;
        }

        var failures = new List<string>();
        var lines = new List<string>();

        Application.logMessageReceived += OnLog;
        OwnershipSwapSignals.onTurretRequest += OnRequest;
        try
        {
            OwnershipSwapSignals.AssignShips(index, playerIds, roots);

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => OwnershipSwapSignals.TryGetReports(index, out var reports) &&
                          reports.Count >= ctx.expectedConnections,
                    Timeout * (Rounds + 2), ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail("not every client reported");
            }

            OwnershipSwapSignals.TryGetReports(index, out var all);

            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                all.TryGetValue(player, out var report);
                var pawns = ships[player];
                var left = pawns[1];
                var right = pawns[2];

                string serverView = $"server left={left.id} owner={Owner(left)} typed={left.handledSwaps} right={right.id} owner={Owner(right)} typed={right.handledSwaps}";
                lines.Add($"{player}: {report}; {serverView}");

                if (report == null || !report.StartsWith("ok"))
                    failures.Add($"{player}: {report}");
                // The even rounds type into the left turret, the odd ones into the right.
                if (left.owner.HasValue || right.owner != player ||
                    left.handledSwaps != Rounds - 1 || right.handledSwaps != Rounds)
                    failures.Add($"{player}: {serverView}");
            }
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
            OwnershipSwapSignals.onTurretRequest -= OnRequest;
        }

        if (warnings.Count > 0)
            failures.Add($"{warnings.Count} input warnings, first='{warnings[0]}'");

        string summary = $"{string.Join(" | ", lines)} | server input warnings={warnings.Count}";
        Debug.Log($"[ServerOwnershipSwapScenario] server: {summary}");

        return failures.Count == 0
            ? ScenarioResult.Ok(summary)
            : ScenarioResult.Fail($"{string.Join(" | ", failures)} || {summary}");
    }

    private static string Owner(OwnershipSwapPawn pawn)
        => !pawn ? "missing" : pawn.owner?.ToString() ?? "none";

    private async UniTask<ScenarioResult> RunClient(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        var me = ctx.networkManager.localPlayer;
        (uint a, uint b) pair = default;
        OwnershipSwapPawn[] pawns = null;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => OwnershipSwapSignals.TryGetPair(ctx.scenarioIndex, me, out pair) &&
                      TryGetTurrets(world, pair.a, out pawns),
                Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return Finish(ctx, "no ship arrived");
        }

        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: ctx.cancellationToken);

        var notes = new List<string>();
        for (var round = 0; round < Rounds; round++)
        {
            var to = pawns[round % 2 == 0 ? 1 : 2];
            var from = pawns[round % 2 == 0 ? 2 : 1];

            OwnershipSwapSignals.RequestTurret(ctx.scenarioIndex, round % 2);

            try
            {
                await UniTaskUtils.WaitWithTimeout(() => to.isOwner && !from.isOwner, 5f, ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return Finish(ctx, $"round {round}: the turret never arrived: from.owner={Owner(from)} to.owner={Owner(to)}");
            }

            to.swapClicks = (uint)(round + 1);

            int lostFrames = 0;
            int frames = 0;
            double until = Time.realtimeSinceStartupAsDouble + WatchSeconds;
            while (Time.realtimeSinceStartupAsDouble < until)
            {
                frames++;
                if (!to.isOwner || from.isOwner)
                    lostFrames++;
                await UniTask.Yield(ctx.cancellationToken);
            }

            notes.Add($"r{round} lost={lostFrames}/{frames} typed={to.handledSwaps}");

            if (!to.isOwner || from.isOwner)
                return Finish(ctx, $"round {round}: control lost: from.owner={Owner(from)} to.owner={Owner(to)} ({string.Join(", ", notes)})");
        }

        return Finish(ctx, $"ok {string.Join(", ", notes)}");
    }

    private static ScenarioResult Finish(ScenarioContext ctx, string message)
    {
        Debug.Log($"[ServerOwnershipSwapScenario] client: {message}");
        OwnershipSwapSignals.Report(ctx.scenarioIndex, message);
        return message.StartsWith("ok") ? ScenarioResult.Ok(message) : ScenarioResult.Fail(message);
    }
}
