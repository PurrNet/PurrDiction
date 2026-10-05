using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// What a user would write to keep collision data without PurrDiction's built-in physics
/// events: Unity collision messages fill this body's own predicted state with the same public
/// PhysicsEvent records the built-in history carries, so the bytes per event match and only the
/// delivery path differs (ordinary state deltas, no per-tick history).
/// </summary>
public class UserContactLog : PredictedIdentity<UserContactLog.ContactState>
{
    public struct ContactState : IPredictedData<ContactState>
    {
        public DisposableList<PhysicsEvent> events;

        public void Dispose()
        {
            if (events.isDisposed)
                return;
            for (int i = 0; i < events.Count; i++)
                events[i].Dispose();
            events.Dispose();
        }
    }

    protected override ContactState GetInitialState() => new() { events = DisposableList<PhysicsEvent>.Create() };

    // Runs before the tick's physics step, which refills the list through collision messages.
    protected override void Simulate(ref ContactState state, float delta)
    {
        if (state.events.isDisposed)
        {
            state.events = DisposableList<PhysicsEvent>.Create();
            return;
        }

        for (int i = 0; i < state.events.Count; i++)
            state.events[i].Dispose();
        state.events.Clear();
    }

    private void OnCollisionEnter(Collision collision) => Record(PhysicsEventType.Enter, collision);
    private void OnCollisionStay(Collision collision) => Record(PhysicsEventType.Stay, collision);
    private void OnCollisionExit(Collision collision) => Record(PhysicsEventType.Exit, collision);

    private void Record(PhysicsEventType type, Collision collision)
    {
        if (predictionManager == null || !predictionManager.isSimulating)
            return;

        ref var events = ref currentState.events;
        if (events.isDisposed)
            events = DisposableList<PhysicsEvent>.Create();

        var contacts = DisposableList<PhysicsContactPoint>.Create(collision.contactCount);
        for (int i = 0; i < collision.contactCount; i++)
            contacts.Add(new PhysicsContactPoint(collision.GetContact(i)));

        PredictionManager.TryGetClosestPredictedID(collision.gameObject, out var other);
        events.Add(new PhysicsEvent
        {
            type = type,
            me = id,
            other = other,
            collision = new PhysicsCollision
            {
                contacts = contacts,
                impulse = collision.impulse,
                relativeVelocity = collision.relativeVelocity
            }
        });
    }
}
