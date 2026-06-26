using System;
using PurrNet.Prediction;
using UnityEngine;
using UnityEngine.Profiling;

public sealed class ScenarioPerformanceSampler : IDisposable
{
    private static readonly string[] DefaultMarkerNames =
    {
        "PredictionManager.SaveHistory",
        "PredictionManager.Simulate",
        "PredictionManager.PrepareSimulationInputs",
        "PredictionManager.LateSimulate",
        "PredictionManager.WriteFrameOnServer"
    };

    private readonly Recorder[] _recorders;
    private readonly ScenarioMarkerAccumulator[] _accumulators;
    private int _lastSampledFrame = -1;
    private int _sampledFrames;
    private int _maxSpawnedIdentities;
    private int _finalSpawnedIdentities;
    private long _spawnedIdentitySamples;
    private bool _running;

    private ScenarioPerformanceSampler(string[] markerNames)
    {
        _recorders = new Recorder[markerNames.Length];
        _accumulators = new ScenarioMarkerAccumulator[markerNames.Length];

        for (var i = 0; i < markerNames.Length; i++)
        {
            var markerName = markerNames[i];
            _recorders[i] = Recorder.Get(markerName);
            _accumulators[i] = new ScenarioMarkerAccumulator
            {
                name = markerName
            };
        }
    }

    public static ScenarioPerformanceSampler StartDefault()
    {
        var sampler = new ScenarioPerformanceSampler(DefaultMarkerNames);
        sampler.Start();
        return sampler;
    }

    private void Start()
    {
        _running = true;
        _lastSampledFrame = -1;

        for (var i = 0; i < _recorders.Length; i++)
        {
            var recorder = _recorders[i];
            if (!recorder.isValid)
                continue;

            recorder.enabled = false;
            recorder.enabled = true;
        }

        PredictionHistoryTelemetry.Begin();
    }

    public void SampleFrame(PredictionManager predictionManager)
    {
        if (!_running)
            return;

        var frame = Time.frameCount;
        if (_lastSampledFrame == frame)
            return;

        _lastSampledFrame = frame;

        for (var i = 0; i < _recorders.Length; i++)
        {
            var recorder = _recorders[i];
            if (!recorder.isValid)
                continue;

            _accumulators[i].elapsedNanoseconds += recorder.elapsedNanoseconds;
            _accumulators[i].sampleBlockCount += recorder.sampleBlockCount;
        }

        var hierarchy = predictionManager ? predictionManager.hierarchy : null;
        if (hierarchy)
        {
            ref var state = ref hierarchy.currentState;
            if (!state.spawnedPrefabs.isDisposed)
            {
                var spawned = state.spawnedPrefabs.Count;
                _finalSpawnedIdentities = spawned;
                _spawnedIdentitySamples += spawned;
                if (spawned > _maxSpawnedIdentities)
                    _maxSpawnedIdentities = spawned;
            }
        }

        _sampledFrames++;
    }

    public ScenarioPerformanceDetails Stop(PredictionManager predictionManager)
    {
        SampleFrame(predictionManager);
        _running = false;
        var historySnapshot = PredictionHistoryTelemetry.End();

        var markers = new ScenarioMarkerDetails[_accumulators.Length];
        for (var i = 0; i < _recorders.Length; i++)
        {
            var recorder = _recorders[i];
            if (recorder.isValid)
                recorder.enabled = false;

            markers[i] = _accumulators[i].ToDetails();
        }

        return new ScenarioPerformanceDetails
        {
            markers = markers,
            history = new ScenarioHistoryDetails
            {
                saveCalls = historySnapshot.saveCalls,
                nonEventSaveCalls = historySnapshot.nonEventSaveCalls,
                eventHandlerSaveCalls = historySnapshot.eventHandlerSaveCalls
            },
            world = new ScenarioWorldDetails
            {
                sampledFrames = _sampledFrames,
                maxSpawnedIdentities = _maxSpawnedIdentities,
                finalSpawnedIdentities = _finalSpawnedIdentities,
                averageSpawnedIdentities = _sampledFrames > 0
                    ? _spawnedIdentitySamples / (double)_sampledFrames
                    : 0
            }
        };
    }

    public void Dispose()
    {
        _running = false;
        if (PredictionHistoryTelemetry.enabled)
            PredictionHistoryTelemetry.End();

        for (var i = 0; i < _recorders.Length; i++)
        {
            var recorder = _recorders[i];
            if (recorder.isValid)
                recorder.enabled = false;
        }
    }

    private struct ScenarioMarkerAccumulator
    {
        public string name;
        public long elapsedNanoseconds;
        public long sampleBlockCount;

        public ScenarioMarkerDetails ToDetails()
        {
            return new ScenarioMarkerDetails
            {
                name = name,
                elapsedNanoseconds = elapsedNanoseconds,
                sampleBlockCount = sampleBlockCount,
                elapsedMilliseconds = elapsedNanoseconds / 1_000_000.0,
                averageNanoseconds = sampleBlockCount > 0 ? elapsedNanoseconds / (double)sampleBlockCount : 0
            };
        }
    }
}
