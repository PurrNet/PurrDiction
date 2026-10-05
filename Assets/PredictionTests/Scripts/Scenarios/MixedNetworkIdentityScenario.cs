using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public struct MixedNetworkProbeState : IPredictedData<MixedNetworkProbeState>
{
    public int ticks;
    public void Dispose() { }
}

/// <summary>Marks the predicted root of the mixed prefab; the prediction side of the object.</summary>
public sealed class MixedNetworkProbe : PredictedIdentity<MixedNetworkProbeState>
{
    protected override void Simulate(ref MixedNetworkProbeState state, float delta) => state.ticks++;
}

/// <summary>Plain PurrNet behaviour living inside a predicted prefab: SyncVar plus a buffered RPC.</summary>
public sealed class MixedNetworkBehaviour : NetworkBehaviour
{
    public readonly SyncVar<int> token = new();
    public int lastAnnounced;
    public int announcements;

    [ObserversRpc(bufferLast: true)]
    public void Announce(int value)
    {
        lastAnnounced = value;
        announcements++;
    }
}

public static class MixedNetworkSignals
{
    [Serializable]
    public struct Expectation
    {
        public uint instanceId;
        public ulong networkId;
        public ulong ownerId;
        public bool ownerIsBot;
        public ulong hiddenId;
        public bool hiddenIsBot;
        public int token;
    }

    private static readonly ScenarioStateLedger<Expectation> Expectations = new();
    private static readonly ScenarioStateLedger<int> Phases = new();
    private static readonly ScenarioStateLedger<Dictionary<PlayerID, string>> Reports = new();

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void Announce(int scenarioIndex, uint instanceId, ulong networkId, ulong ownerId, bool ownerIsBot,
        ulong hiddenId, bool hiddenIsBot, int token)
        => Expectations.TryAdd(scenarioIndex, 0, new Expectation
        {
            instanceId = instanceId, networkId = networkId, ownerId = ownerId, ownerIsBot = ownerIsBot,
            hiddenId = hiddenId, hiddenIsBot = hiddenIsBot, token = token
        });

    public static bool TryGetExpectation(int scenarioIndex, out Expectation expectation)
        => Expectations.TryGet(scenarioIndex, 0, out expectation);

    [ObserversRpc(runLocally: true, bufferLast: true)]
    public static void SetPhase(int scenarioIndex, int phase)
        => Phases.TryAdd(scenarioIndex, phase, phase);

    public static bool HasPhase(int scenarioIndex, int phase)
        => Phases.TryGet(scenarioIndex, phase, out _);

    [ServerRpc(requireOwnership: false)]
    public static void Report(int scenarioIndex, int phase, string message, RPCInfo info = default)
    {
        if (!ScenarioSynchronization.IsOpen(scenarioIndex)) return;
        if (!Reports.TryGet(scenarioIndex, phase, out var reports))
        {
            reports = new Dictionary<PlayerID, string>();
            Reports.TryAdd(scenarioIndex, phase, reports);
        }
        reports[info.sender] = message;
    }

    public static bool TryGetReports(int scenarioIndex, int phase, out Dictionary<PlayerID, string> reports)
        => Reports.TryGet(scenarioIndex, phase, out reports);
}

/// <summary>
/// NetworkIdentity components inside a predicted prefab need no spawner component: the server
/// spawns them when the predicted instance is created, clients spawn them from the verified
/// topology with the server's ids, ownership follows the predicted owner, observers follow
/// predicted visibility, and deleting the predicted instance despawns them everywhere.
/// </summary>
public sealed class MixedNetworkIdentityScenario : Scenario
{
    private const float Timeout = 30f;
    private const int PhaseSpawned = 1;
    private const int PhaseHidden = 2;
    private const int PhaseShown = 3;
    private const int PhaseDeleted = 4;
    private const int Token = 4242;

    private GameObject _prefab;
    private int _prefabId;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefab = PredictionTestUtils.CreatePrefab<MixedNetworkProbe>("MixedNetworkPrefab");
        _prefab.AddComponent<MixedNetworkBehaviour>();
        var child = new GameObject("MixedNetworkChild");
        child.transform.SetParent(_prefab.transform, false);
        child.AddComponent<MixedNetworkBehaviour>();
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);
    }

    public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        ctx.predictionManager.TryGetPrefab(_prefab, out _prefabId);
        return RunSplit(ctx, RunClient, RunServer);
    }

    private async UniTask<ScenarioResult> RunServer(ScenarioContext ctx)
    {
        var world = ctx.predictionManager;
        // The owner always sees its own root, so visibility is exercised on a second client.
        var players = ctx.networkManager.players;
        PlayerID? owner = null, hidden = null;
        for (var i = 0; i < players.Count; i++)
        {
            if (ctx.role == NetworkRole.Host && players[i] == ctx.networkManager.localPlayer)
                continue;
            if (!owner.HasValue) owner = players[i];
            else if (!hidden.HasValue) hidden = players[i];
        }

        if (!owner.HasValue || !hidden.HasValue)
            return ScenarioResult.Fail("the mixed scenario needs at least two external clients");

        var created = world.hierarchy.Create(_prefab, new Vector3(3, 0, 0), Quaternion.identity, owner);
        if (!created.HasValue || !world.hierarchy.TryGetGameObject(created, out var instance))
            return ScenarioResult.Fail("mixed instance creation failed");

        var behaviours = instance.GetComponentsInChildren<MixedNetworkBehaviour>(true);
        if (behaviours.Length != 2)
            return ScenarioResult.Fail($"expected 2 mixed behaviours on the server instance, found {behaviours.Length}");

        var state = world.hierarchy.currentState;
        NetworkID? networkId = null;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            if (state.spawnedPrefabs[i].instanceId.Equals(created.Value))
                networkId = state.spawnedPrefabs[i].networkId;
        }

        if (!networkId.HasValue)
            return ScenarioResult.Fail("server record carries no network id block");

        foreach (var behaviour in behaviours)
        {
            if (!behaviour.isSpawned || !behaviour.id.HasValue)
                return ScenarioResult.Fail($"{behaviour.name} is not spawned on the server after predicted creation");
            if (behaviour.owner != owner)
                return ScenarioResult.Fail($"{behaviour.name} owner is {behaviour.owner}, expected {owner}");
        }

        if (behaviours[0].id != networkId.Value)
            return ScenarioResult.Fail($"root behaviour id {behaviours[0].id} does not match the record block {networkId.Value}");

        behaviours[0].token.value = Token;
        behaviours[1].token.value = Token + 1;
        behaviours[0].Announce(Token);
        behaviours[1].Announce(Token + 1);

        MixedNetworkSignals.Announce(ctx.scenarioIndex, created.Value.instanceId.value, networkId.Value.id.value,
            owner.Value.id.value, owner.Value.isBot, hidden.Value.id.value, hidden.Value.isBot, Token);

        var failure = await AwaitReports(ctx, PhaseSpawned);
        if (failure != null) return ScenarioResult.Fail(failure);

        // Hiding the predicted root must remove the client's network identities too.
        if (!world.HideFrom(hidden.Value, created.Value))
            return ScenarioResult.Fail("HideFrom rejected the mixed root");
        MixedNetworkSignals.SetPhase(ctx.scenarioIndex, PhaseHidden);
        failure = await AwaitReports(ctx, PhaseHidden);
        if (failure != null) return ScenarioResult.Fail(failure);

        if (!world.ShowTo(hidden.Value, created.Value))
            return ScenarioResult.Fail("ShowTo rejected the mixed root");
        MixedNetworkSignals.SetPhase(ctx.scenarioIndex, PhaseShown);
        failure = await AwaitReports(ctx, PhaseShown);
        if (failure != null) return ScenarioResult.Fail(failure);

        world.hierarchy.Delete(created);
        MixedNetworkSignals.SetPhase(ctx.scenarioIndex, PhaseDeleted);
        await UniTaskUtils.WaitWithTimeout(() => behaviours[0] == null || !behaviours[0].isSpawned, Timeout, ctx.cancellationToken);
        failure = await AwaitReports(ctx, PhaseDeleted);
        if (failure != null) return ScenarioResult.Fail(failure);

        return ScenarioResult.Ok($"mixed instance {created.Value} used block {networkId.Value} across {ctx.externalClientCount} clients");
    }

    private static async UniTask<string> AwaitReports(ScenarioContext ctx, int phase)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => MixedNetworkSignals.TryGetReports(ctx.scenarioIndex, phase, out var reports) &&
                      reports.Count >= ctx.externalClientCount,
                Timeout, ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return $"phase {phase}: not every client reported";
        }

        MixedNetworkSignals.TryGetReports(ctx.scenarioIndex, phase, out var all);
        foreach (var pair in all)
        {
            if (pair.Value != "ok")
                return $"phase {phase}: client {pair.Key} failed: {pair.Value}";
        }

        return null;
    }

    private async UniTask<ScenarioResult> RunClient(ScenarioContext ctx)
    {
        if (ctx.role == NetworkRole.Host)
            return ScenarioResult.Ok("host client shares the server mirror");

        var world = ctx.predictionManager;
        MixedNetworkSignals.Expectation expected = default;
        string failure = null;

        try
        {
            await UniTaskUtils.WaitWithTimeout(() => MixedNetworkSignals.TryGetExpectation(ctx.scenarioIndex, out expected), Timeout, ctx.cancellationToken);
            var instanceId = new PredictedObjectID(expected.instanceId);
            var owner = new PlayerID(expected.ownerId, expected.ownerIsBot);
            var hiddenPlayer = new PlayerID(expected.hiddenId, expected.hiddenIsBot);
            bool isOwner = ctx.networkManager.localPlayer == owner;
            bool isHidden = ctx.networkManager.localPlayer == hiddenPlayer;
            MixedNetworkBehaviour[] behaviours = null;

            await UniTaskUtils.WaitWithTimeout(() =>
            {
                if (!world.hierarchy.TryGetGameObject(instanceId, out var go) || !go)
                    return false;
                behaviours = go.GetComponentsInChildren<MixedNetworkBehaviour>(true);
                return behaviours.Length == 2 && behaviours[0].isSpawned && behaviours[1].isSpawned;
            }, Timeout, ctx.cancellationToken);

            if (!behaviours[0].id.HasValue || behaviours[0].id.Value.id.value != expected.networkId)
                failure = $"root behaviour id {behaviours[0].id} does not match the server block {expected.networkId}";
            else if (!behaviours[1].id.HasValue || behaviours[1].id.Value.id.value != expected.networkId + 1)
                failure = $"child behaviour id {behaviours[1].id} does not continue the block {expected.networkId}";
            else
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => behaviours[0].token.value == expected.token && behaviours[1].token.value == expected.token + 1 &&
                          behaviours[0].lastAnnounced == expected.token && behaviours[1].lastAnnounced == expected.token + 1,
                    Timeout, ctx.cancellationToken);
                if (behaviours[0].owner != owner)
                    failure = $"root behaviour owner {behaviours[0].owner}, expected {owner}";
                else if (isOwner && !behaviours[0].isOwner)
                    failure = "owning client does not own the mirrored behaviour";
                else if (!isOwner && behaviours[0].isOwner)
                    failure = "non-owning client owns the mirrored behaviour";
            }

            MixedNetworkSignals.Report(ctx.scenarioIndex, PhaseSpawned, failure ?? "ok");
            if (failure != null) return ScenarioResult.Fail(failure);

            // Hidden roots leave the client's verified topology; the mirror must follow.
            await UniTaskUtils.WaitWithTimeout(() => MixedNetworkSignals.HasPhase(ctx.scenarioIndex, PhaseHidden), Timeout, ctx.cancellationToken);
            if (isHidden)
            {
                await UniTaskUtils.WaitWithTimeout(() => behaviours[0] == null || !behaviours[0].isSpawned, Timeout, ctx.cancellationToken);
            }
            else if (!behaviours[0].isSpawned)
            {
                failure = "a non-hidden client lost its mirrored behaviour";
            }
            MixedNetworkSignals.Report(ctx.scenarioIndex, PhaseHidden, failure ?? "ok");
            if (failure != null) return ScenarioResult.Fail(failure);

            await UniTaskUtils.WaitWithTimeout(() => MixedNetworkSignals.HasPhase(ctx.scenarioIndex, PhaseShown), Timeout, ctx.cancellationToken);
            await UniTaskUtils.WaitWithTimeout(() =>
            {
                if (!world.hierarchy.TryGetGameObject(instanceId, out var go) || !go)
                    return false;
                behaviours = go.GetComponentsInChildren<MixedNetworkBehaviour>(true);
                return behaviours.Length == 2 && behaviours[0].isSpawned && behaviours[1].isSpawned &&
                       behaviours[0].lastAnnounced == expected.token && behaviours[0].token.value == expected.token;
            }, Timeout, ctx.cancellationToken);
            if (behaviours[0].id.Value.id.value != expected.networkId)
                failure = $"re-shown root behaviour id {behaviours[0].id} does not match the server block {expected.networkId}";
            MixedNetworkSignals.Report(ctx.scenarioIndex, PhaseShown, failure ?? "ok");
            if (failure != null) return ScenarioResult.Fail(failure);

            await UniTaskUtils.WaitWithTimeout(() => MixedNetworkSignals.HasPhase(ctx.scenarioIndex, PhaseDeleted), Timeout, ctx.cancellationToken);
            await UniTaskUtils.WaitWithTimeout(
                () => (behaviours[0] == null || !behaviours[0].isSpawned) && (behaviours[1] == null || !behaviours[1].isSpawned),
                Timeout, ctx.cancellationToken);
            MixedNetworkSignals.Report(ctx.scenarioIndex, PhaseDeleted, "ok");
        }
        catch (TimeoutException)
        {
            failure ??= "timed out waiting for the mirrored network identities";
            MixedNetworkSignals.Report(ctx.scenarioIndex, PhaseSpawned, failure);
            return ScenarioResult.Fail(failure);
        }

        return ScenarioResult.Ok();
    }
}
