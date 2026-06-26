using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;

public static class ScenarioSequencer
{
    private const float SCENARIO_TIMEOUT_SECONDS = 4f * 60f;
    private const float END_OF_RUN_HANDSHAKE_TIMEOUT_SECONDS = 10f;

    private static readonly Dictionary<int, HashSet<PlayerID>> _acksByIndex = new();
    private static readonly HashSet<PlayerID> _endOfRunAcks = new();

    private static int _latestStartedIndex = -1;
    private static bool _sequenceComplete;

    public static bool SequenceComplete => _sequenceComplete;

    public static void IssueStart(int index)
    {
        BroadcastStart(index);
    }

    public static async UniTask WaitForStart(ScenarioContext ctx, int index)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _latestStartedIndex >= index || _sequenceComplete,
                SCENARIO_TIMEOUT_SECONDS,
                ctx.cancellationToken);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(
                $"Timed out waiting for scenario {index} start " +
                $"(latestStarted={_latestStartedIndex}, sequenceComplete={_sequenceComplete})",
                e);
        }
    }

    public static async UniTask WaitForAllAcks(ScenarioContext ctx, int index)
    {
        var localId = ctx.networkManager.localPlayer;
        bool isHost = ctx.role == NetworkRole.Host;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => AllConnectedClientsAcked(ctx, index, localId, isHost),
                SCENARIO_TIMEOUT_SECONDS,
                ctx.cancellationToken);
        }
        catch (TimeoutException e)
        {
            _acksByIndex.TryGetValue(index, out var acks);
            throw new TimeoutException(
                $"Timed out waiting for scenario {index} client acks " +
                $"({acks?.Count ?? 0}/{ExpectedAckCount(ctx, localId, isHost)} received)",
                e);
        }

        _acksByIndex.Remove(index);
    }

    private static bool AllConnectedClientsAcked(ScenarioContext ctx, int index, PlayerID localId, bool isHost)
    {
        _acksByIndex.TryGetValue(index, out var acks);
        var connected = ctx.networkManager.players;
        for (int i = 0; i < connected.Count; i++)
        {
            var p = connected[i];
            if (isHost && p == localId)
                continue;
            if (acks == null || !acks.Contains(p))
                return false;
        }
        return true;
    }

    private static int ExpectedAckCount(ScenarioContext ctx, PlayerID localId, bool isHost)
    {
        int expected = 0;
        var connected = ctx.networkManager.players;
        for (int i = 0; i < connected.Count; i++)
        {
            var p = connected[i];
            if (isHost && p == localId)
                continue;
            expected++;
        }
        return expected;
    }

    public static void AckLocalDone(ScenarioContext ctx, int index)
    {
        if (ctx.role != NetworkRole.Client)
            return;
        AckDone(index);
    }

    public static void IssueSequenceComplete()
    {
        BroadcastSequenceComplete();
    }

    public static async UniTask WaitForEndOfRunHandshake(ScenarioContext ctx)
    {
        var localId = ctx.networkManager.localPlayer;
        bool isHost = ctx.role == NetworkRole.Host;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => AllConnectedClientsEndOfRunAcked(ctx, localId, isHost),
                END_OF_RUN_HANDSHAKE_TIMEOUT_SECONDS,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    public static void AckEndOfRun(ScenarioContext ctx)
    {
        if (ctx.role != NetworkRole.Client)
            return;
        AckEndOfRunRpc();
    }

    private static bool AllConnectedClientsEndOfRunAcked(ScenarioContext ctx, PlayerID localId, bool isHost)
    {
        var connected = ctx.networkManager.players;
        for (int i = 0; i < connected.Count; i++)
        {
            var p = connected[i];
            if (isHost && p == localId)
                continue;
            if (!_endOfRunAcks.Contains(p))
                return false;
        }
        return true;
    }

    [ObserversRpc(runLocally: true)]
    private static void BroadcastStart(int index)
    {
        if (index > _latestStartedIndex)
            _latestStartedIndex = index;
    }

    [ServerRpc(requireOwnership: false)]
    private static void AckDone(int index, RPCInfo info = default)
    {
        if (!_acksByIndex.TryGetValue(index, out var set))
        {
            set = new HashSet<PlayerID>();
            _acksByIndex[index] = set;
        }
        set.Add(info.sender);
    }

    [ObserversRpc(runLocally: true)]
    private static void BroadcastSequenceComplete()
    {
        _sequenceComplete = true;
    }

    [ServerRpc(requireOwnership: false)]
    private static void AckEndOfRunRpc(RPCInfo info = default)
    {
        _endOfRunAcks.Add(info.sender);
    }
}
