using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class BounceScenario : Scenario
{
    [SerializeField] private BounceRig _rig;
    [SerializeField] private int _minBounces = 3;
    [SerializeField] private float _restSeconds = 4f;
    [SerializeField] private float _timeout = 60f;

    private const int DigestChannel = 400;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        var floor = new GameObject("BounceFloor");
        var floorCollider = floor.AddComponent<BoxCollider>();
        floorCollider.size = new Vector3(50f, 1f, 50f);
        floor.transform.position = new Vector3(0f, -0.5f, 0f);
        floor.AddComponent<PredictedTransform>();

        var ball = new GameObject("BouncyBall");
        ball.SetActive(false);
        var rb = ball.AddComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var sphere = ball.AddComponent<SphereCollider>();
        sphere.material = new PhysicsMaterial("Bouncy")
        {
            bounciness = 0.7f,
            bounceCombine = PhysicsMaterialCombine.Maximum
        };

        ball.AddComponent<PredictedTransform>();
        ball.AddComponent<PredictedRigidbody>();
        ball.AddComponent<BounceProbe>();
        DontDestroyOnLoad(ball);

        PredictionTestUtils.RegisterPrefab(ctx, ball);
        _rig.ballPrefab = ball;
        _rig.requiredPlayers = ctx.expectedConnections;
        BounceProbe.ResetCounters();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        int lastCount = -1;
        double lastChange = Time.realtimeSinceStartupAsDouble;

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () =>
                {
                    var now = Time.realtimeSinceStartupAsDouble;
                    if (BounceProbe.totalFires != lastCount)
                    {
                        lastCount = BounceProbe.totalFires;
                        lastChange = now;
                    }
                    return BounceProbe.distinctTicks >= _minBounces && now - lastChange >= _restSeconds;
                },
                _timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail(
                $"ball never settled: spawned={_rig.hasSpawned} bounces={BounceProbe.distinctTicks}/{_minBounces}");
        }

        if (BounceProbe.hasDuplicate)
        {
            return ScenarioResult.Fail(
                $"verified bounce fired more than once for tick {BounceProbe.duplicateTick}: {BounceProbe.Digest()}");
        }

        return await DigestExchange.Compare(ctx, DigestChannel, BounceProbe.Digest(), 30f);
    }
}
