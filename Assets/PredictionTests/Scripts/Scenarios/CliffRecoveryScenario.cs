using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using PurrNet.Transports;
using UnityEngine;

public class CliffRecoveryScenario : Scenario
{
    private const int DigestChannel = 210;
    private const int ReadyBarrierId = 22_001;
    private const int RecoveredBarrierId = 22_002;
    private const float BlackoutSeconds = 2.5f;
    private const float SettleSeconds = 3f;
    private const float RecoveryTimeout = 30f;
    private const float Timeout = 90f;

    public override void Setup(ScenarioContext ctx, NetworkManager manager) { }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;

        await UniTask.WaitForSeconds(SettleSeconds, cancellationToken: ctx.cancellationToken);

        try
        {
            await ScenarioBarrier.Wait(ctx, ReadyBarrierId, Timeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out synchronizing blackout start");
        }

        bool blackoutEnforced = false;
        ulong framesDuringBlackout = 0;
        ulong fullFramesBefore = 0;

#if DEBUG || SIMULATE_NETWORK
        if (ctx.role == NetworkRole.Client &&
            ctx.networkManager.transport is UDPTransport udp)
        {
            var original = udp.networkSimulation;
            var framesBefore = pm.framesReceivedTotal;
            fullFramesBefore = pm.fullFramesReceivedTotal;
            blackoutEnforced = true;

            udp.networkSimulation = new NetworkSimulation
            {
                includeInBuild = true,
                simulateLatency = original.simulateLatency,
                minLatency = original.minLatency,
                maxLatency = original.maxLatency,
                simulatePacketLoss = true,
                packetLossChance = 100
            };

            try
            {
                await UniTask.WaitForSeconds(BlackoutSeconds, cancellationToken: ctx.cancellationToken);
            }
            finally
            {
                udp.networkSimulation = original;
            }

            framesDuringBlackout = pm.framesReceivedTotal - framesBefore;

            // The ordered stream redelivers every frame sent during the outage, so verified progress
            // must pass the blackout and keep going without falling back to a full frame.
            ulong verifiedAtRestore = pm.verifiedServerTick;
            ulong requiredTicks = (ulong)Mathf.CeilToInt((BlackoutSeconds + 1f) * pm.tickRate);
            var recoveryDeadline = Time.realtimeSinceStartup + RecoveryTimeout;
            while (pm.verifiedServerTick < verifiedAtRestore + requiredTicks)
            {
                if (Time.realtimeSinceStartup > recoveryDeadline)
                {
                    return ScenarioResult.Fail(
                        "verified progress did not resume after the blackout: " +
                        $"framesDuringBlackout={framesDuringBlackout} " +
                        $"verified={pm.verifiedServerTick - verifiedAtRestore}/{requiredTicks} ticks");
                }

                await UniTask.NextFrame(ctx.cancellationToken);
            }

            if (pm.fullFramesReceivedTotal != fullFramesBefore)
            {
                return ScenarioResult.Fail(
                    "the ordered frame stream needed a full frame to heal a transport outage: " +
                    $"fullFrames={pm.fullFramesReceivedTotal - fullFramesBefore}");
            }
        }
#endif

        if (ctx.role == NetworkRole.Client && !blackoutEnforced &&
            !CommandLineUtils.HasFlag("-allowDigestOnly"))
        {
            return ScenarioResult.Fail(
                "blackout could not be enforced (network simulation unavailable or non-UDP " +
                "transport); outage recovery was not exercised. Pass -allowDigestOnly " +
                "to accept a digest-only run.");
        }

        await UniTask.WaitForSeconds(SettleSeconds + 2f, cancellationToken: ctx.cancellationToken);

        try
        {
            await ScenarioBarrier.Wait(ctx, RecoveredBarrierId, Timeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out synchronizing recovery");
        }

        var counter = UnityEngine.Object.FindFirstObjectByType<DeterministicTickCounter>();
        if (!counter)
            return ScenarioResult.Fail("no DeterministicTickCounter in scene");

        try
        {
            await PredictionTestUtils.AlignDigestTick(ctx, DigestChannel, Timeout);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail("timed out aligning digest tick");
        }

        var digest = PredictionTestUtils.WorldDigest(ctx, counter);
        var result = await DigestExchange.Compare(ctx, DigestChannel, digest, 30f);
        if (!result.success)
            return result;

        string note = ctx.role == NetworkRole.Client
            ? (blackoutEnforced
                ? $"blackout enforced (frames during={framesDuringBlackout}), stream caught up without a full frame, deterministic state reconverged"
                : "network simulation unavailable; digest-only check")
            : "server digest consistent";
        return ScenarioResult.Ok(note);
    }
}
