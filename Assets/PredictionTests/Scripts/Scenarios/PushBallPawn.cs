using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// Mirror of the packet-loss report's PredictedPlayerInput: a rigidbody ball driven by a
/// keyboard-style direction through velocity-change forces. Only the driver's owner steers;
/// its input cycles through waypoints across a pen full of boxes, so the ball keeps smashing
/// through them. The server records the driver's authoritative position per tick so the
/// remote view can be compared against the truth offline.
/// </summary>
public class PushBallPawn : PredictedIdentity<PushBallPawn.PushInput, PushBallPawn.PushState>
{
    public static ulong driverOwnerId;
    public static float moveForce = 30f;
    public static Vector3 arenaCenter;
    public static float waypointRadius = 13f;
    public static readonly List<(ulong tick, Vector3 position)> serverTrace = new();

    private static readonly Vector2[] Waypoints =
    {
        new(-1f, 0f), new(1f, 0f), new(0f, -1f), new(0f, 1f),
        new(-1f, -1f), new(1f, 1f), new(-1f, 1f), new(1f, -1f)
    };

    public struct PushInput : IPredictedData
    {
        public bool jump;
        public Vector2 direction;

        public void Dispose() { }
    }

    public struct PushState : IPredictedData<PushState>
    {
        public int waypoint;

        public void Dispose() { }
    }

    private PredictedRigidbody _body;

    private void Awake()
    {
        _body = GetComponent<PredictedRigidbody>();
    }

    private bool isDriver => owner.HasValue && owner.Value.id.value == driverOwnerId;

    protected override void GetFinalInput(ref PushInput input)
    {
        if (driverOwnerId == 0 || !isDriver)
            return;

        var target = Waypoints[currentState.waypoint % Waypoints.Length] * waypointRadius;
        var position = transform.position - arenaCenter;
        var toTarget = new Vector2(target.x - position.x, target.y - position.z);

        // Keyboard-style: each axis is -1, 0 or 1, like WASD.
        input.direction = new Vector2(Axis(toTarget.x), Axis(toTarget.y));
    }

    private static float Axis(float value) => Mathf.Abs(value) < 1f ? 0f : Mathf.Sign(value);

    protected override void ModifyExtrapolatedInput(ref PushInput input)
    {
        input.jump = false;
    }

    protected override void Simulate(PushInput input, ref PushState state, float delta)
    {
        _body.AddForce(new Vector3(input.direction.x, 0f, input.direction.y) * moveForce * delta,
            ForceMode.VelocityChange);

        var target = Waypoints[state.waypoint % Waypoints.Length] * waypointRadius;
        var position = transform.position - arenaCenter;
        if (new Vector2(target.x - position.x, target.y - position.z).sqrMagnitude < 9f)
            state.waypoint++;
    }

    protected override void LateSimulate(PushInput input, ref PushState state, float delta)
    {
        if (predictionManager.cachedIsServer && isDriver)
            serverTrace.Add((predictionManager.localTickInContext, transform.position));
    }
}
