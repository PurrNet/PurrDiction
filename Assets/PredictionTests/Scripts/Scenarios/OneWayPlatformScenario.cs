using System;
using Cysharp.Threading.Tasks;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// A predicted 2D body jumps up into a one-way platform thicker than itself, peaks inside it below its top
/// and falls back down, over and over. At the apex the server alone holds it still for a moment, which no
/// client predicts (like a starved input or any other server-side effect), so a client's own body has
/// already fallen clear of the platform when the verified frames arrive. Restoring them puts the body back
/// inside the platform with no contact to reuse; Unity rebuilds the PlatformEffector2D contact at that pose
/// and, with the body past the platform's middle, judges it from above and pushes it onto the platform.
/// The server never rolls back, so its body never gets on top, and no client may show it there either.
/// </summary>
public sealed class OneWayPlatformScenario : Scenario
{
    public static readonly Vector2 Origin = new Vector2(800f, 0f);
    public const float PlatformThickness = 1.5f;
    public const float BodySize = 0.9f;
    public const float FloorTop = -3f;
    // Head height at the apex, relative to the platform's centre; its top is at PlatformThickness / 2.
    public const float ApexHead = 0.7f;
    // Feet above this are on the platform: the highest legitimate feet height is ApexHead - BodySize.
    private const float MaxFeet = 0.1f;
    // Strong gravity, as platformers use, lets a mispredicted fall clear the platform within the lead.
    public const float GravityScale = 7.5f;
    // Ticks the server alone holds the body still at the apex.
    public const int ServerHangTicks = 20;
    private const int RequiredJumps = 8;
    private const float Timeout = 90f;

    private GameObject _prefab;
    private GameObject _level;

    public override void Setup(ScenarioContext ctx, NetworkManager manager)
    {
        _prefab = new GameObject("OneWayJumper2D");
        _prefab.SetActive(false);
        var rb = _prefab.AddComponent<Rigidbody2D>();
        rb.freezeRotation = true;
        rb.gravityScale = GravityScale;
        rb.sleepMode = RigidbodySleepMode2D.NeverSleep;
        _prefab.AddComponent<BoxCollider2D>().size = Vector2.one * BodySize;
        _prefab.AddComponent<PredictedTransform>();
        _prefab.AddComponent<PredictedRigidbody2D>().configuredPredictionPolicy = PredictionPolicy.FullPrediction;
        _prefab.AddComponent<OneWayJumper2D>();
        DontDestroyOnLoad(_prefab);
        PredictionTestUtils.RegisterPrefab(ctx, _prefab);

        // Plain scene geometry, built the same way on every peer.
        _level = new GameObject("OneWayPlatformLevel");
        SceneManager.MoveGameObjectToScene(_level, ctx.predictionManager.gameObject.scene);
        _level.transform.position = Origin;

        var platform = new GameObject("OneWayPlatform");
        platform.transform.SetParent(_level.transform, false);
        var platformCollider = platform.AddComponent<BoxCollider2D>();
        platformCollider.size = new Vector2(6f, PlatformThickness);
        platformCollider.usedByEffector = true;
        var effector = platform.AddComponent<PlatformEffector2D>();
        effector.useOneWay = true;
        effector.surfaceArc = 180f;
        effector.rotationalOffset = 0f;

        var floor = new GameObject("OneWayPlatformFloor");
        floor.transform.SetParent(_level.transform, false);
        floor.transform.localPosition = new Vector3(0f, FloorTop - 0.5f, 0f);
        floor.AddComponent<BoxCollider2D>().size = new Vector2(6f, 1f);

        OneWayJumper2D.ResetCounters();
    }

    public override async UniTask<ScenarioResult> RunScenario(ScenarioContext ctx)
    {
        var pm = ctx.predictionManager;
        OneWayJumper2D.ResetCounters();

        if (ctx.isServer)
        {
            var spawn = Origin + new Vector2(0f, FloorTop + BodySize * 0.5f + 0.01f);
            if (!pm.hierarchy.Create(_prefab, spawn, Quaternion.identity).HasValue)
                return ScenarioResult.Fail("failed to create the jumper");
        }

        try
        {
            await UniTaskUtils.WaitWithTimeout(
                () => OneWayJumper2D.LiveJumps >= RequiredJumps || OneWayJumper2D.MaxLiveFeet > MaxFeet,
                Timeout,
                ctx.cancellationToken);
        }
        catch (TimeoutException)
        {
            return ScenarioResult.Fail($"the jumper did not jump {RequiredJumps} times: {Report(pm)}");
        }
        finally
        {
            if (ctx.isServer)
            {
                var jumper = UnityEngine.Object.FindFirstObjectByType<OneWayJumper2D>();
                if (jumper)
                    pm.hierarchy.Delete(jumper.gameObject);
            }
        }

        if (OneWayJumper2D.MaxLiveFeet > MaxFeet)
            return ScenarioResult.Fail($"the body ended up on top of the one-way platform it never crossed: {Report(pm)}");

        // Only a body whose centre passes the platform's middle can be judged from above.
        if (OneWayJumper2D.MaxLiveHead < BodySize * 0.5f + 0.05f)
            return ScenarioResult.Fail($"the body never got past the platform's middle, so nothing was exercised: {Report(pm)}");

        return ScenarioResult.Ok(Report(pm));
    }

    private static string Report(PredictionManager pm)
        => FormattableString.Invariant(
            $"tickRate={pm.tickRate} jumps={OneWayJumper2D.LiveJumps} maxHead={OneWayJumper2D.MaxLiveHead:F3} maxFeet={OneWayJumper2D.MaxLiveFeet:F3} (limit {MaxFeet:F2}, platform top {PlatformThickness * 0.5f:F2}) LiveTicksInside={OneWayJumper2D.LiveTicksInside}");
}

/// <summary>Jumps from the floor so that its head peaks just below the one-way platform's top.</summary>
public sealed class OneWayJumper2D : PredictedIdentity<OneWayJumper2D.JumpState>
{
    public struct JumpState : IPredictedData<JumpState>
    {
        public float Cooldown;
        public int Jumps;
        public int HangLeft;
        public bool HungThisJump;

        public void Dispose() { }
    }

    public static int LiveJumps;
    public static float MaxLiveHead;
    public static float MaxLiveFeet;
    public static int LiveTicksInside;

    public static void ResetCounters()
    {
        LiveJumps = 0;
        MaxLiveHead = float.MinValue;
        MaxLiveFeet = float.MinValue;
        LiveTicksInside = 0;
    }

    private PredictedRigidbody2D _body;

    protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.FullPrediction;

    protected override void Simulate(ref JumpState state, float delta)
    {
        if (!_body)
            _body = GetComponent<PredictedRigidbody2D>();

        const float half = OneWayPlatformScenario.BodySize * 0.5f;
        float y = _body.position.y - OneWayPlatformScenario.Origin.y;

        // Sample the pose each live tick starts from, which is what a client shows after reconciling.
        if (!predictionManager.isReplaying)
        {
            MaxLiveHead = Mathf.Max(MaxLiveHead, y + half);
            MaxLiveFeet = Mathf.Max(MaxLiveFeet, y - half);
            if (y + half > -OneWayPlatformScenario.PlatformThickness * 0.5f && y - half < OneWayPlatformScenario.PlatformThickness * 0.5f)
                LiveTicksInside++;
            LiveJumps = Mathf.Max(LiveJumps, state.Jumps);
        }

        float gravity = -Physics2D.gravity.y * _body.rigidbody.gravityScale;

        // The server alone hangs at the apex: gravity is added before the move, so this velocity cancels it.
        if (isServer && !state.HungThisJump && state.Jumps > 0 && _body.linearVelocity.y <= 0f && y > 0f)
        {
            state.HungThisJump = true;
            state.HangLeft = OneWayPlatformScenario.ServerHangTicks;
        }
        if (isServer && state.HangLeft > 0)
        {
            state.HangLeft--;
            _body.linearVelocity = new Vector2(0f, gravity * delta);
        }

        state.Cooldown -= delta;
        bool grounded = y - half <= OneWayPlatformScenario.FloorTop + 0.05f &&
                        Mathf.Abs(_body.linearVelocity.y) < 0.05f;
        if (state.Cooldown > 0f || !grounded)
            return;

        float rise = OneWayPlatformScenario.ApexHead - (y + half);
        // Box2D adds gravity before moving, which lowers the apex by about v * dt / 2.
        float launch = (gravity * delta + Mathf.Sqrt(gravity * gravity * delta * delta + 8f * gravity * rise)) * 0.5f;
        _body.linearVelocity = new Vector2(0f, launch);
        state.Cooldown = 0.25f;
        state.Jumps++;
        state.HungThisJump = false;
    }
}
