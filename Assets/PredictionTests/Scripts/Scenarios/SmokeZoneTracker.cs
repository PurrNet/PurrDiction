using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

/// <summary>
/// User-report-faithful component: a DisposableList lives in the identity state, created in
/// GetInitialState and disposed only in the state's Dispose. Predicted trigger events mutate
/// the list through currentState from callbacks subscribed in OnEnable, exactly like the
/// reported smoke-zone script. Intentionally implements no IDuplicate and no defensive
/// isDisposed re-creation.
/// </summary>
public class SmokeZoneTracker : PredictedIdentity<SmokeZoneTracker.ZoneState>
{
    public static readonly System.Collections.Generic.List<SmokeZoneTracker> instances = new();

    public static int enterFires;
    public static int exitFires;

    public static void ResetCounters()
    {
        enterFires = 0;
        exitFires = 0;
    }

    public struct ZoneState : IPredictedData<ZoneState>
    {
        public DisposableList<PredictedObjectID> insideIds;

        public void Dispose()
        {
            insideIds.Dispose();
        }
    }

    protected override ZoneState GetInitialState()
    {
        return new ZoneState
        {
            insideIds = DisposableList<PredictedObjectID>.Create()
        };
    }

    private PredictedRigidbody _predictedRigidbody;

    private void OnEnable()
    {
        _predictedRigidbody = transform.GetComponent<PredictedRigidbody>();

        _predictedRigidbody.onTriggerEnter += OnPredictedTriggerEnter;
        _predictedRigidbody.onTriggerExit += OnPredictedTriggerExit;
        instances.Add(this);
    }

    private void OnDisable()
    {
        if (_predictedRigidbody)
        {
            _predictedRigidbody.onTriggerEnter -= OnPredictedTriggerEnter;
            _predictedRigidbody.onTriggerExit -= OnPredictedTriggerExit;
        }

        instances.Remove(this);
    }

    private void OnPredictedTriggerEnter(GameObject other, PredictedComponentID otherId)
    {
        enterFires++;
        var id = otherId.objectId;
        if (!currentState.insideIds.Contains(id))
            currentState.insideIds.Add(id);
    }

    private void OnPredictedTriggerExit(GameObject other, PredictedComponentID otherId)
    {
        exitFires++;
        var id = otherId.objectId;
        if (currentState.insideIds.Contains(id))
            currentState.insideIds.Remove(id);
    }
}
