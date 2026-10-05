using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;
using NetworkChannel = PurrNet.Transports.Channel;
using RpcCompressionLevel = PurrNet.CompressionLevel;

/// <summary>
/// RPC-shaped probe for the immediate lane used by PredictionManager. The data RPCs deliberately
/// match PredictionManager's input and frame signatures so PurrNet codegen, delta-packed batch
/// headers, nested packers, compression and MTU auto-flushes are all exercised together.
/// </summary>
public static class ImmediatePredictionRpcRegressionProbe
{
    private const ulong InputTickBase = 0x1A2B_3C4D_5000_0000UL;
    private const ulong InputAckBase = 0x2B3C_4D5E_6000_0000UL;
    private const ulong FrameTickBase = 0x3C4D_5E6F_7000_0000UL;
    private const ulong FrameAckBase = 0x4D5E_6F70_8000_0000UL;
    private const uint InputPayloadSalt = 0xA511_E9B3u;
    private const uint FramePayloadSalt = 0x63D8_35C7u;

    public static readonly HashSet<PlayerID> readyPlayers = new();
    public static readonly HashSet<PlayerID> donePlayers = new();
    public static readonly Dictionary<ulong, HashSet<uint>> serverSequences = new();
    public static readonly HashSet<uint> clientSequences = new();

    public static bool started;
    public static bool complete;
    public static string serverCorruption;
    public static string clientCorruption;
    private static int _expectedMessages;

    public static void Reset(int expectedMessages)
    {
        readyPlayers.Clear();
        donePlayers.Clear();
        serverSequences.Clear();
        clientSequences.Clear();
        started = false;
        complete = false;
        serverCorruption = null;
        clientCorruption = null;
        _expectedMessages = expectedMessages;
    }

    public static BitPacker CreatePayload(uint sequence, uint salt)
    {
        int length = PayloadLength(sequence);
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = PayloadByte(sequence, i, salt);

        var payload = BitPackerPool.Get();
        payload.WriteBytes(bytes);
        return payload;
    }

    public static ulong InputFirstTick(uint sequence) => InputTickBase + sequence * 7UL;
    public static uint InputTickCount(uint sequence) => sequence % 6u + 1u;
    public static ulong InputFrameAck(uint sequence) => InputAckBase + sequence * 3UL;

    public static ulong FrameServerTick(uint sequence) => FrameTickBase + sequence * 5UL;
    public static ulong FrameBaselineTick(uint sequence) => FrameServerTick(sequence) - (sequence % 11u + 1u);
    public static ulong FrameInputAck(uint sequence) => FrameAckBase + sequence * 2UL;
    public static bool FrameIsFull(uint sequence) => sequence % 17u == 0;
    public static bool FrameHasMargin(uint sequence) => (sequence & 1u) != 0;
    public static int FrameMargin(uint sequence) => (int)(sequence % 129u) - 64;
    public static bool FrameHasSlack(uint sequence) => (sequence & 2u) != 0;
    public static int FrameSlack(uint sequence) => (int)(sequence % 401u) - 200;

    [ServerRpc(requireOwnership: false)]
    public static void SignalReady(RPCInfo info = default)
    {
        readyPlayers.Add(info.sender);
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastStarted()
    {
        started = true;
    }

    [ServerRpc(requireOwnership: false)]
    public static void SignalDone(RPCInfo info = default)
    {
        donePlayers.Add(info.sender);
    }

    [ObserversRpc(runLocally: true)]
    public static void BroadcastComplete()
    {
        complete = true;
    }

    [ServerRpc(requireOwnership: false, channel: NetworkChannel.Unreliable, immediate: true)]
    public static void SendInputToServer(
        ulong firstTick,
        uint tickCount,
        ulong frameAck,
        BitPacker payload,
        RPCInfo info = default)
    {
        ReceiveInput(firstTick, tickCount, frameAck, payload, info);
    }

    [ServerRpc(requireOwnership: false, channel: NetworkChannel.Unreliable,
        mtuExceeded: MTUBehaviour.Fragment, immediate: true)]
    public static void SendInputToServerFragmented(
        ulong firstTick,
        uint tickCount,
        ulong frameAck,
        BitPacker payload,
        RPCInfo info = default)
    {
        ReceiveInput(firstTick, tickCount, frameAck, payload, info);
    }

    [TargetRpc(channel: NetworkChannel.Unreliable, compressionLevel: RpcCompressionLevel.Fast,
        mtuExceeded: MTUBehaviour.Fragment, immediate: true)]
    public static void SendFrameToRemote(
        PlayerID target,
        ulong serverTick,
        ulong baselineTick,
        ulong inputAck,
        bool fullFrame,
        bool hasInputMargin,
        PackedInt inputMargin,
        bool hasInputSlack,
        PackedInt inputSlackMs,
        BitPackerWithLength delta)
    {
        using (delta)
        {
            if (!TryDecodeSequence(serverTick, FrameTickBase, 5UL, out var sequence))
            {
                RecordClientCorruption($"invalid serverTick {serverTick}");
                return;
            }

            if (baselineTick != FrameBaselineTick(sequence) ||
                inputAck != FrameInputAck(sequence) ||
                fullFrame != FrameIsFull(sequence) ||
                hasInputMargin != FrameHasMargin(sequence) ||
                inputMargin.value != FrameMargin(sequence) ||
                hasInputSlack != FrameHasSlack(sequence) ||
                inputSlackMs.value != FrameSlack(sequence))
            {
                RecordClientCorruption($"fixed frame fields changed at sequence {sequence}");
                return;
            }

            int expectedLength = PayloadLength(sequence);
            if (delta.originalLength != expectedLength)
            {
                RecordClientCorruption(
                    $"frame payload length changed at sequence {sequence}: {delta.originalLength} != {expectedLength}");
                return;
            }

            if (!PayloadMatches(delta.packer, sequence, FramePayloadSalt, expectedLength, out var error))
            {
                RecordClientCorruption($"frame payload changed at sequence {sequence}: {error}");
                return;
            }

            clientSequences.Add(sequence);
        }
    }

    private static void ReceiveInput(
        ulong firstTick,
        uint tickCount,
        ulong frameAck,
        BitPacker payload,
        RPCInfo info)
    {
        using (payload)
        {
            if (!TryDecodeSequence(firstTick, InputTickBase, 7UL, out var sequence))
            {
                RecordServerCorruption(info.sender, $"invalid firstTick {firstTick}");
                return;
            }

            if (tickCount != InputTickCount(sequence) || frameAck != InputFrameAck(sequence))
            {
                RecordServerCorruption(info.sender, $"fixed input fields changed at sequence {sequence}");
                return;
            }

            int expectedLength = PayloadLength(sequence);
            if (!PayloadMatches(payload, sequence, InputPayloadSalt, expectedLength, out var error))
            {
                RecordServerCorruption(info.sender, $"input payload changed at sequence {sequence}: {error}");
                return;
            }

            if (!serverSequences.TryGetValue(info.sender.id.value, out var sequences))
            {
                sequences = new HashSet<uint>();
                serverSequences.Add(info.sender.id.value, sequences);
            }

            sequences.Add(sequence);
        }
    }

    private static bool TryDecodeSequence(ulong value, ulong baseValue, ulong stride, out uint sequence)
    {
        sequence = 0;
        if (value < baseValue)
            return false;

        ulong difference = value - baseValue;
        if (difference % stride != 0)
            return false;

        ulong decoded = difference / stride;
        if (decoded >= (ulong)_expectedMessages || decoded > uint.MaxValue)
            return false;

        sequence = (uint)decoded;
        return true;
    }

    private static int PayloadLength(uint sequence)
    {
        uint frame = sequence / ImmediatePredictionRpcRegressionScenario.MessagesPerFrame;
        return (sequence % ImmediatePredictionRpcRegressionScenario.MessagesPerFrame) switch
        {
            0 => 13 + (int)(frame % 37u),
            1 => 193 + (int)(frame % 97u),
            2 => 557 + (int)(frame % 211u),
            _ => 1097 + (int)(frame % 349u)
        };
    }

    private static bool PayloadMatches(
        BitPacker payload,
        uint sequence,
        uint salt,
        int expectedLength,
        out string error)
    {
        try
        {
            var bytes = new byte[expectedLength];
            payload.ReadBytes(bytes);
            for (var i = 0; i < bytes.Length; i++)
            {
                byte expected = PayloadByte(sequence, i, salt);
                if (bytes[i] == expected)
                    continue;

                error = $"byte {i} was {bytes[i]}, expected {expected}";
                return false;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        error = null;
        return true;
    }

    private static byte PayloadByte(uint sequence, int index, uint salt)
    {
        uint value = salt ^ sequence * 0x9E37_79B9u ^ (uint)index * 0x85EB_CA6Bu;
        value ^= value >> 16;
        value *= 0x7FEB_352Du;
        value ^= value >> 15;
        value *= 0x846C_A68Bu;
        value ^= value >> 16;
        return (byte)value;
    }

    private static void RecordServerCorruption(PlayerID sender, string detail)
    {
        serverCorruption ??= $"sender {sender.id.value}: {detail}";
    }

    private static void RecordClientCorruption(string detail)
    {
        clientCorruption ??= detail;
    }
}

/// <summary>
/// Reproduces the serialized component shape used by PurrLeague's player prefab: a predicted
/// transform, predicted rigidbody, and an owned input identity with the same input/state fields.
/// The simulation is intentionally simple; this probe exists to drive the real PredictionManager
/// input and frame RPCs with the same per-tick payload shape as the project that exposed the bug.
/// </summary>
public sealed class ImmediatePredictionWorkloadIdentity :
    PredictedIdentity<ImmediatePredictionWorkloadIdentity.WorkloadInput,
        ImmediatePredictionWorkloadIdentity.WorkloadState>
{
    public struct WorkloadInput : IPredictedData
    {
        public float throttle;
        public float steering;
        public bool powerslide;
        public bool jumpPressed;
        public uint jumpPressSequence;
        public bool jumpHeld;
        public bool boost;

        public void Dispose() { }
    }

    public struct WheelState : IPackedAuto
    {
        public bool isGrounded;
        public float offset;
    }

    public struct WheelsState : IPackedAuto
    {
        public WheelState wheel0;
        public WheelState wheel1;
        public WheelState wheel2;
        public WheelState wheel3;
    }

    public struct WorkloadState : IPredictedData<WorkloadState>
    {
        public WheelsState wheels;
        public float steering;
        public float timeSinceLastJump;
        public float timeSinceGrounded;
        public float timeSinceFirstJump;
        public float boostAmount;
        public byte jumpsUsed;
        public uint lastConsumedJumpPressSequence;
        public bool jumpHoldActive;
        public bool wasAirborneSinceJump;
        public bool isBoosting;

        public void Dispose() { }
    }

    protected override void GetFinalInput(ref WorkloadInput input)
    {
        uint tick = unchecked((uint)predictionManager.localTick);
        input.throttle = ((int)(tick % 201u) - 100) * 0.01f;
        input.steering = ((int)((tick * 17u) % 201u) - 100) * 0.01f;
        input.powerslide = (tick & 7u) == 0;
        input.jumpPressed = tick % 29u == 0;
        input.jumpPressSequence = tick / 29u;
        input.jumpHeld = tick % 29u < 5u;
        input.boost = (tick & 3u) != 0;
    }

    protected override void Simulate(WorkloadInput input, ref WorkloadState state, float delta)
    {
        state.wheels.wheel0 = MakeWheel(input, 0.00f, true);
        state.wheels.wheel1 = MakeWheel(input, 0.25f, true);
        state.wheels.wheel2 = MakeWheel(input, 0.50f, false);
        state.wheels.wheel3 = MakeWheel(input, 0.75f, false);
        state.steering = input.steering * 40f;
        state.timeSinceLastJump = Mathf.Repeat(state.timeSinceLastJump + delta, 10f);
        state.timeSinceGrounded = Mathf.Repeat(state.timeSinceGrounded + delta * 0.5f, 10f);
        state.timeSinceFirstJump = Mathf.Repeat(state.timeSinceFirstJump + delta * 0.25f, 10f);
        state.boostAmount = Mathf.Repeat(state.boostAmount + (input.boost ? 0.75f : 0.125f), 100f);
        state.jumpsUsed = (byte)(input.jumpPressSequence % 3u);
        state.lastConsumedJumpPressSequence = input.jumpPressSequence;
        state.jumpHoldActive = input.jumpHeld;
        state.wasAirborneSinceJump = !state.wheels.wheel0.isGrounded;
        state.isBoosting = input.boost;

        transform.localPosition = new Vector3(
            state.steering * 0.001f,
            state.boostAmount * 0.001f,
            input.throttle * 0.01f);
    }

    private static WheelState MakeWheel(WorkloadInput input, float bias, bool grounded)
    {
        return new WheelState
        {
            isGrounded = grounded ^ input.powerslide,
            offset = input.throttle * 0.3f + input.steering * 0.1f + bias
        };
    }
}

public sealed class ImmediatePredictionRpcRegressionScenario : Scenario
{
    public const uint MessagesPerFrame = 4;

    private const int DefaultFrameCount = 180;
    private const float TimeoutSeconds = 45f;
    private const float DrainSeconds = 1.5f;

    private static GameObject _workloadPrefab;
    private int _frameCount;
    private int _workloadPrefabId;
    private PredictedPlayerSpawner _playerSpawner;
    private bool _withoutPredictedPlayer;
    private int _playerSpawnerDesyncs;
    private string _lastPlayerSpawnerDesync;
    private int TotalMessages => _frameCount * (int)MessagesPerFrame;
    private int MinimumDeliveries => Math.Max(1, TotalMessages / 4);

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _withoutPredictedPlayer = CommandLineUtils.HasFlag("-immediateRpcRegressionWithoutPlayer");
        _frameCount = DefaultFrameCount;
        if (CommandLineUtils.TryGetArgument("-immediateRpcRegressionFrames", out var value) &&
            int.TryParse(value, out var parsed))
            _frameCount = Math.Max(1, parsed);

        if (!_workloadPrefab)
        {
            _workloadPrefab = new GameObject("ImmediatePredictionWorkload");
            _workloadPrefab.SetActive(false);
            var predictedTransform = _workloadPrefab.AddComponent<PredictedTransform>();
            JsonUtility.FromJsonOverwrite(
                "{\"_floatAccuracy\":0,\"_characterControllerPatch\":false}",
                predictedTransform);
            var body = _workloadPrefab.AddComponent<Rigidbody>();
            body.useGravity = false;
            var predictedBody = _workloadPrefab.AddComponent<PredictedRigidbody>();
            JsonUtility.FromJsonOverwrite(
                "{\"_floatAccuracy\":0,\"_eventMask\":0,\"_ignoreTriggerOnTrigger\":false}",
                predictedBody);
            _workloadPrefab.AddComponent<ImmediatePredictionWorkloadIdentity>();
            UnityEngine.Object.DontDestroyOnLoad(_workloadPrefab);
        }

        PredictionTestUtils.RegisterPrefab(ctx, _workloadPrefab);
        _workloadPrefabId = ctx.predictionManager.predictedPrefabs.prefabs.Count - 1;

        _playerSpawner = UnityEngine.Object.FindAnyObjectByType<PredictedPlayerSpawner>();
        if (_withoutPredictedPlayer)
        {
            // Exercise the join seam with only the built-in prediction systems. This mirrors the
            // minimal PurrLeague reproduction where PredictedPlayers remains empty even after the
            // gameplay player prefab and its owned input identity are removed.
            if (_playerSpawner)
                _playerSpawner.enabled = false;
        }
        // The full PurrLeague-shaped profile also gives the scene-authored deterministic spawner
        // real state while the inherited Report policy and immediate RPC lane are under stress.
        else if (CommandLineUtils.TryGetArgument("-desyncPolicy", out var desyncPolicy) &&
            Enum.TryParse(desyncPolicy, true, out DesyncPolicy parsedDesyncPolicy) &&
            parsedDesyncPolicy != DesyncPolicy.Ignore)
        {
            if (_playerSpawner)
            {
                _playerSpawner.playerPrefab = _workloadPrefab;
                ctx.predictionManager.onDesyncDetected += OnSpawnerDesyncDetected;
                ctx.predictionManager.onLocalDesync += OnLocalSpawnerDesync;
            }
        }

        PlayerSpawnerTrace.AttachIfRequested(gameObject, ctx.predictionManager, _playerSpawner);
    }

    public override void PrepareRun(ScenarioContext ctx, ulong startTick)
    {
        ImmediatePredictionRpcRegressionProbe.Reset(TotalMessages);
    }

    public override UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        return RunSplit(ctx, RunAsClient, RunAsServer);
    }

    private async UniTask<ScenarioResult> RunAsClient(ScenarioContext ctx)
    {
        ImmediatePredictionRpcRegressionProbe.SignalReady();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ImmediatePredictionRpcRegressionProbe.started,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("immediate RPC regression never received the start signal");
        }

        if (!_withoutPredictedPlayer)
        {
            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => HasOwnedWorkload(ctx.predictionManager),
                    TimeoutSeconds,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail("PurrLeague-shaped owned prediction workload never appeared");
            }
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => HasExpectedPredictedPlayers(ctx),
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(DescribePredictedPlayersMismatch(ctx));
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _withoutPredictedPlayer || !_playerSpawner || HasExpectedSpawnerEntries(ctx),
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"PredictedPlayerSpawner state only contains {_playerSpawner.currentState.values.Count}/{ctx.expectedConnections} players");
        }

        ulong lastPredictionTick = ctx.predictionManager.localTick;
        for (var frame = 0; frame < _frameCount; frame++)
        {
            for (uint lane = 0; lane < MessagesPerFrame; lane++)
            {
                uint sequence = (uint)frame * MessagesPerFrame + lane;
                using var payload = ImmediatePredictionRpcRegressionProbe.CreatePayload(sequence, 0xA511_E9B3u);

                if (lane == MessagesPerFrame - 1)
                {
                    ImmediatePredictionRpcRegressionProbe.SendInputToServerFragmented(
                        ImmediatePredictionRpcRegressionProbe.InputFirstTick(sequence),
                        ImmediatePredictionRpcRegressionProbe.InputTickCount(sequence),
                        ImmediatePredictionRpcRegressionProbe.InputFrameAck(sequence),
                        payload);
                }
                else
                {
                    ImmediatePredictionRpcRegressionProbe.SendInputToServer(
                        ImmediatePredictionRpcRegressionProbe.InputFirstTick(sequence),
                        ImmediatePredictionRpcRegressionProbe.InputTickCount(sequence),
                        ImmediatePredictionRpcRegressionProbe.InputFrameAck(sequence),
                        payload);
                }
            }

            await UniTask.WaitUntil(
                () => ctx.predictionManager.localTick > lastPredictionTick,
                cancellationToken: ctx.cancellationToken);
            lastPredictionTick = ctx.predictionManager.localTick;
        }

        ImmediatePredictionRpcRegressionProbe.SignalDone();

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ImmediatePredictionRpcRegressionProbe.complete,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"immediate RPC regression never completed; frames received={ImmediatePredictionRpcRegressionProbe.clientSequences.Count}");
        }

        await UniTask.WaitForSeconds(0.5f, cancellationToken: ctx.cancellationToken);

        if (ImmediatePredictionRpcRegressionProbe.clientCorruption != null)
            return ScenarioResult.Fail($"server-to-client immediate RPC corruption: {ImmediatePredictionRpcRegressionProbe.clientCorruption}");

        if (_playerSpawnerDesyncs > 0)
            return ScenarioResult.Fail(
                $"PredictedPlayerSpawner produced {_playerSpawnerDesyncs} unforced deterministic desyncs; last={_lastPlayerSpawnerDesync}");

        if (!HasExpectedPredictedPlayers(ctx))
            return ScenarioResult.Fail(DescribePredictedPlayersMismatch(ctx));

        int received = ImmediatePredictionRpcRegressionProbe.clientSequences.Count;
        if (received < MinimumDeliveries)
            return ScenarioResult.Fail($"too few server frames survived: {received}/{TotalMessages}, minimum {MinimumDeliveries}");

        return ScenarioResult.Ok(
            $"validated {received}/{TotalMessages} compressed immediate server frames");
    }

    private async UniTask<ScenarioResult> RunAsServer(ScenarioContext ctx)
    {
        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ImmediatePredictionRpcRegressionProbe.readyPlayers.Count >= ctx.expectedConnections,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"only {ImmediatePredictionRpcRegressionProbe.readyPlayers.Count}/{ctx.expectedConnections} clients became ready");
        }

        var targets = new List<PlayerID>(ImmediatePredictionRpcRegressionProbe.readyPlayers);

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => HasExpectedPredictedPlayers(ctx),
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(DescribePredictedPlayersMismatch(ctx));
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => _withoutPredictedPlayer || !_playerSpawner || HasExpectedSpawnerEntries(ctx),
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"PredictedPlayerSpawner state only contains {_playerSpawner.currentState.values.Count}/{ctx.expectedConnections} players");
        }

        if (!_withoutPredictedPlayer)
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (!ctx.predictionManager.hierarchy.Create(
                        _workloadPrefab,
                        new Vector3(i * 3f, 20f, 0f),
                        Quaternion.identity,
                        targets[i]).HasValue)
                    return ScenarioResult.Fail($"failed to create PurrLeague-shaped workload for {targets[i]}");
            }

            try
            {
                await UniTaskUtils.WaitWithTimeout(
                    () => PredictionTestUtils.CountInstances(ctx.predictionManager, _workloadPrefabId) >= targets.Count,
                    TimeoutSeconds,
                    ctx.cancellationToken);
            }
            catch (TimeoutException)
            {
                return ScenarioResult.Fail(
                    $"only {PredictionTestUtils.CountInstances(ctx.predictionManager, _workloadPrefabId)}/{targets.Count} prediction workloads spawned");
            }
        }

        await UniTask.WaitForSeconds(0.5f, cancellationToken: ctx.cancellationToken);
        ImmediatePredictionRpcRegressionProbe.BroadcastStarted();

        ulong lastPredictionTick = ctx.predictionManager.localTick;
        for (var frame = 0; frame < _frameCount; frame++)
        {
            for (uint lane = 0; lane < MessagesPerFrame; lane++)
            {
                uint sequence = (uint)frame * MessagesPerFrame + lane;
                for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    using var payload = ImmediatePredictionRpcRegressionProbe.CreatePayload(sequence, 0x63D8_35C7u);
                    ImmediatePredictionRpcRegressionProbe.SendFrameToRemote(
                        targets[targetIndex],
                        ImmediatePredictionRpcRegressionProbe.FrameServerTick(sequence),
                        ImmediatePredictionRpcRegressionProbe.FrameBaselineTick(sequence),
                        ImmediatePredictionRpcRegressionProbe.FrameInputAck(sequence),
                        ImmediatePredictionRpcRegressionProbe.FrameIsFull(sequence),
                        ImmediatePredictionRpcRegressionProbe.FrameHasMargin(sequence),
                        ImmediatePredictionRpcRegressionProbe.FrameMargin(sequence),
                        ImmediatePredictionRpcRegressionProbe.FrameHasSlack(sequence),
                        ImmediatePredictionRpcRegressionProbe.FrameSlack(sequence),
                        new BitPackerWithLength(payload.length, payload));
                }
            }

            await UniTask.WaitUntil(
                () => ctx.predictionManager.localTick > lastPredictionTick,
                cancellationToken: ctx.cancellationToken);
            lastPredictionTick = ctx.predictionManager.localTick;
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => ImmediatePredictionRpcRegressionProbe.donePlayers.Count >= ctx.expectedConnections,
                TimeoutSeconds,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            ImmediatePredictionRpcRegressionProbe.BroadcastComplete();
            return ScenarioResult.Fail(
                $"only {ImmediatePredictionRpcRegressionProbe.donePlayers.Count}/{ctx.expectedConnections} clients finished sending");
        }

        await UniTask.WaitForSeconds(DrainSeconds, cancellationToken: ctx.cancellationToken);

        ScenarioResult result = ValidateServer(ctx, targets);
        ImmediatePredictionRpcRegressionProbe.BroadcastComplete();
        return result;
    }

    private ScenarioResult ValidateServer(ScenarioContext ctx, List<PlayerID> targets)
    {
        if (ImmediatePredictionRpcRegressionProbe.serverCorruption != null)
            return ScenarioResult.Fail($"client-to-server immediate RPC corruption: {ImmediatePredictionRpcRegressionProbe.serverCorruption}");

        if (_playerSpawnerDesyncs > 0)
            return ScenarioResult.Fail(
                $"PredictedPlayerSpawner produced {_playerSpawnerDesyncs} unforced deterministic desyncs; last={_lastPlayerSpawnerDesync}");

        if (!HasExpectedPredictedPlayers(ctx))
            return ScenarioResult.Fail(DescribePredictedPlayersMismatch(ctx));

        var counts = new List<string>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            ulong playerId = targets[i].id.value;
            ImmediatePredictionRpcRegressionProbe.serverSequences.TryGetValue(playerId, out var sequences);
            int received = sequences?.Count ?? 0;
            counts.Add($"{playerId}:{received}");

            if (received < MinimumDeliveries)
            {
                return ScenarioResult.Fail(
                    $"too few client inputs survived for player {playerId}: {received}/{TotalMessages}, minimum {MinimumDeliveries}");
            }
        }

        return ScenarioResult.Ok(
            $"validated {TotalMessages} attempted immediate inputs per client ({string.Join(", ", counts)})");
    }

    private bool HasExpectedSpawnerEntries(ScenarioContext ctx)
    {
        if (!_playerSpawner || _playerSpawner.currentState.values.Count < ctx.expectedConnections)
            return false;

        var players = ctx.networkManager.players;
        for (var i = 0; i < players.Count; i++)
        {
            if (!_playerSpawner.currentState.ContainsKey(players[i]))
                return false;
        }

        return true;
    }

    private static bool HasExpectedPredictedPlayers(ScenarioContext ctx)
    {
        var predictedPlayers = ctx.predictionManager.players;
        if (!predictedPlayers || predictedPlayers.currentState.players.isDisposed ||
            predictedPlayers.currentState.players.Count != ctx.expectedConnections)
            return false;

        var connectedPlayers = ctx.networkManager.players;
        for (var i = 0; i < connectedPlayers.Count; i++)
        {
            if (!predictedPlayers.currentState.players.Contains(connectedPlayers[i]))
                return false;
        }

        return true;
    }

    private static string DescribePredictedPlayersMismatch(ScenarioContext ctx)
    {
        var predictedPlayers = ctx.predictionManager.players;
        if (!predictedPlayers)
            return "PredictionManager did not register its built-in PredictedPlayers identity";

        ref var state = ref predictedPlayers.currentState;
        if (state.players.isDisposed)
            return $"PredictedPlayers state is disposed; expected {ctx.expectedConnections} connected players";

        return $"PredictedPlayers state contains {state.players.Count}/{ctx.expectedConnections} connected players: {state}";
    }

    private void OnSpawnerDesyncDetected(PredictedIdentity identity, PlayerID player, ulong tick, DesyncPolicy policy)
    {
        if (identity != _playerSpawner)
            return;

        _playerSpawnerDesyncs++;
        _lastPlayerSpawnerDesync = $"player={player} tick={tick} policy={policy}";
    }

    private void OnLocalSpawnerDesync(PredictedIdentity identity, ulong tick, DesyncPolicy policy)
    {
        if (identity != _playerSpawner)
            return;

        _playerSpawnerDesyncs++;
        _lastPlayerSpawnerDesync = $"local tick={tick} policy={policy}";
    }

    private bool HasOwnedWorkload(PredictionManager manager)
    {
        ref var state = ref manager.hierarchy.currentState;
        for (var i = 0; i < state.spawnedPrefabs.Count; i++)
        {
            var details = state.spawnedPrefabs[i];
            if (details.prefabId != _workloadPrefabId)
                continue;

            if (details.instanceId.TryGetComponent<ImmediatePredictionWorkloadIdentity>(manager, out var workload) &&
                workload.isOwner)
                return true;
        }

        return false;
    }
}
