using System.Collections.Generic;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class TrailGunner : PredictedIdentity<TrailGunner.GunnerInput, TrailGunner.GunnerState>
{
    public const float ProjectileSpeed = 20f;
    public const uint FireEveryTicks = 4;

    public static GameObject projectilePrefab;
    public static ulong fireStartTick = ulong.MaxValue;
    public static ulong fireEndTick;

    public struct GunnerInput : IPredictedData
    {
        public bool fire;

        public void Dispose() { }
    }

    public struct GunnerState : IPredictedData<GunnerState>
    {
        public int shots;

        public void Dispose() { }
    }

    protected override void GetFinalInput(ref GunnerInput input)
    {
        var tick = predictionManager.localTick;
        input.fire = projectilePrefab &&
                     tick >= fireStartTick &&
                     tick < fireEndTick &&
                     tick % FireEveryTicks == 0;
    }

    protected override void Simulate(GunnerInput input, ref GunnerState state, float delta)
    {
        if (!input.fire || !projectilePrefab)
            return;

        hierarchy.Create(projectilePrefab, transform.position, Quaternion.identity, owner);
        state.shots += 1;
    }

    public string Digest()
    {
        return $"{id.objectId.instanceId.value}:{currentState.shots}";
    }
}

public class TrailProjectile : PredictedIdentity<TrailProjectile.ProjState>
{
    public const uint LifetimeTicks = 30;

    public struct ProjState : IPredictedData<ProjState>
    {
        public uint age;
        public bool deleteRequested;

        public void Dispose() { }
    }

    protected override void Simulate(ref ProjState state, float delta)
    {
        if (state.deleteRequested)
            return;

        state.age += 1;
        transform.position += Vector3.right * (TrailGunner.ProjectileSpeed * delta);

        if (state.age < LifetimeTicks)
            return;

        state.deleteRequested = true;
        hierarchy.Delete(id.objectId);
    }
}

public class TrailViewTracker : MonoBehaviour
{
    public const float BackwardEps = 0.05f;
    public const float LateralEps = 0.05f;

    public struct Sample
    {
        public string kind;
        public string channel;
        public int frame;
        public int prevFrame;
        public ulong tick;
        public uint instanceId;
        public int segment;
        public bool owned;
        public Vector3 prev;
        public Vector3 cur;
        public bool hasPreviousContext;
        public Observation previousContext;
        public Observation currentContext;
        public bool isRegisteredInstance;
        public bool hasSpawnRecord;
        public Vector3 spawnPosition;
        public PlayerID? spawnOwner;
        // Latest verified pose stored for this logical id, read only when a sample is recorded.
        public bool hasVerified;
        public ulong verifiedTick;
        public Vector3 verifiedPose;
        // Last observation of this physical instance before it was disabled (pooled), and the
        // frames at which it was disabled and re-enabled. Shows pooled reuse between samples.
        public bool hasPooledContext;
        public Observation pooledContext;
        public int pooledFrame;
        public int enabledFrame;

        public override string ToString()
        {
            var verified = hasVerified ? $"{verifiedTick}:({verifiedPose.x:F3},{verifiedPose.y:F3},{verifiedPose.z:F3})" : "none";
            var pooled = hasPooledContext ? $"{pooledContext}@{pooledFrame}" : "none";
            var idChanged = hasPooledContext && pooledContext.id != currentContext.id;
            var ownerChanged = hasPooledContext && pooledContext.owner != currentContext.owner;
            return $"{kind}/{channel} id={instanceId} owned={owned} seg={segment} " +
                   $"frames={prevFrame}->{frame} tick={tick} " +
                   $"prev=({prev.x:F3},{prev.y:F3},{prev.z:F3}) cur=({cur.x:F3},{cur.y:F3},{cur.z:F3}) " +
                   $"contextPrev={hasPreviousContext}:{previousContext} contextNow={currentContext} " +
                   $"samePhysical={hasPreviousContext && previousContext.physicalInstance == currentContext.physicalInstance} " +
                   $"ageRewind={hasPreviousContext && currentContext.age < previousContext.age} registered={isRegisteredInstance} " +
                   $"spawnRecord={hasSpawnRecord}:({spawnPosition.x:F3},{spawnPosition.y:F3},{spawnPosition.z:F3}) owner={spawnOwner} " +
                   $"latestVerified={verified} enabledFrame={enabledFrame} pooledPrev={pooled} " +
                   $"idChangedSincePool={idChanged} ownerChangedSincePool={ownerChanged}";
        }
    }

    public struct Observation
    {
        public uint id;
        public int physicalInstance;
        public PlayerID? owner;
        public ulong localTick;
        public uint age;
        public bool deleteRequested;
        public ulong? projectileVerifiedTick;
        public ulong? transformVerifiedTick;
        // Rendered view pose and live predicted pose at this frame.
        public Vector3 view;
        public Vector3 live;
        // View interpolation buffer depth and whether a tick sample is waiting to be consumed.
        public int viewBuffer;
        public bool pendingLatch;
        // Server-frame batches applied so far; a change between frames marks a rollback batch.
        public ulong frameApplies;

        public override string ToString()
            => $"[id={id},physical={physicalInstance},owner={owner},tick={localTick},age={age},delete={deleteRequested}," +
               $"verified={projectileVerifiedTick},transformVerified={transformVerifiedTick}," +
               $"view=({view.x:F3},{view.y:F3},{view.z:F3}),live=({live.x:F3},{live.y:F3},{live.z:F3})," +
               $"buffer={viewBuffer},latch={pendingLatch},applies={frameApplies}]";
    }

    // Rendered motion of owned projectiles against their constant speed, split by whether the server has
    // confirmed the projectile yet. A frame whose travel is off by more than half the expected step is a
    // step: the view stood still, jumped, or restarted instead of gliding.
    public struct Smoothness
    {
        public long frames;
        public long steps;
        public double deviation;

        public double stepPercent => frames == 0 ? 0 : 100d * steps / frames;

        public void Add(float ratio)
        {
            frames++;
            var off = Mathf.Abs(ratio - 1f);
            deviation += off;
            if (off > 0.5f)
                steps++;
        }

        public override string ToString()
            => frames == 0 ? "n=0" : $"n={frames} steps={stepPercent:F1}% dev={deviation / frames:F2}";
    }

    public static Smoothness unconfirmedSmoothness;
    public static Smoothness confirmedSmoothness;

    public static readonly List<Sample> failures = new();
    public static readonly List<Sample> diagnostics = new();
    public static readonly HashSet<uint> deadIds = new();
    // Unbounded count per kind/channel so the bounded sample lists do not hide the mix.
    public static readonly Dictionary<string, int> kindCounts = new();
    public static long totalSamples;
    public static int segmentsStarted;
    public static int resurrections;
    public static float maxBackward;

    private const int MaxRecorded = 64;

    private PredictedTransform _pt;
    private TrailProjectile _proj;

    private int _segment;
    private bool _hasPrev;
    private Vector3 _prevView;
    private Vector3 _prevSim;
    private int _prevFrame;
    private uint _lastId;
    private bool _hasDisabledSample;
    private Vector3 _disabledView;
    private int _disabledFrame;
    private bool _checkedResurrection;
    private int _physicalInstance;
    private bool _hasPreviousContext;
    private Observation _previousContext;
    private Observation _currentContext;
    private bool _hasPooledContext;
    private Observation _pooledContext;
    private int _pooledFrame;
    private int _enabledFrame;

    public static void ResetAll()
    {
        failures.Clear();
        diagnostics.Clear();
        deadIds.Clear();
        kindCounts.Clear();
        totalSamples = 0;
        segmentsStarted = 0;
        resurrections = 0;
        maxBackward = 0f;
        unconfirmedSmoothness = default;
        confirmedSmoothness = default;
    }

    public static string DescribeKindCounts()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var pair in kindCounts)
        {
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(pair.Key).Append(':').Append(pair.Value);
        }

        return sb.Length > 0 ? sb.ToString() : "none";
    }

    private void Awake()
    {
        _pt = GetComponent<PredictedTransform>();
        _proj = GetComponent<TrailProjectile>();
        _physicalInstance = gameObject.GetInstanceID();
    }

    private void OnEnable()
    {
        _segment++;
        segmentsStarted++;
        _hasPrev = false;
        _checkedResurrection = false;
        _enabledFrame = Time.frameCount;
    }

    private void OnDisable()
    {
        if (_hasPrev)
        {
            _hasDisabledSample = true;
            _disabledView = _prevView;
            _disabledFrame = _prevFrame;
            if (_lastId != 0)
                deadIds.Add(_lastId);
        }

        if (_hasPreviousContext)
        {
            _hasPooledContext = true;
            _pooledContext = _previousContext;
            _pooledFrame = Time.frameCount;
        }

        _hasPrev = false;
    }

    private void LateUpdate()
    {
        if (!_pt || _pt.predictionManager == null)
            return;

        totalSamples++;

        var pm = _pt.predictionManager;
        _pt.GetViewWorldPose(out var viewPos, out _);
        var simPos = transform.position;
        var frame = Time.frameCount;
        var tick = pm.localTick;
        bool owned = _proj && _proj.isOwner;
        uint instanceId = _proj ? _proj.id.objectId.instanceId.value : 0;
        _lastId = instanceId;
        // Only scalar context is retained between frames. Detailed record lookup and
        // formatting happen for the existing bounded failures/diagnostics below.
        _currentContext = new Observation
        {
            id = instanceId,
            physicalInstance = _physicalInstance,
            owner = _proj ? _proj.owner : null,
            localTick = tick,
            age = _proj ? _proj.currentState.age : 0,
            deleteRequested = _proj && _proj.currentState.deleteRequested,
            projectileVerifiedTick = _proj ? _proj.lastVerifiedTick : null,
            transformVerifiedTick = _pt.lastVerifiedTick,
            view = viewPos,
            live = _pt.currentState.unityPosition,
            viewBuffer = _pt.viewInterpolationBufferSize,
            pendingLatch = _pt.hasPendingViewLatch,
            frameApplies = pm.renderPhaseFrameAppliesTotal + pm.tickPhaseFrameAppliesTotal
        };

        if (!_checkedResurrection && instanceId != 0)
        {
            _checkedResurrection = true;
            if (deadIds.Contains(instanceId))
            {
                resurrections++;
                Record(diagnostics, "resurrectedId", "id", frame, _disabledFrame, tick, instanceId, owned, _disabledView, viewPos);
            }
        }

        if (_hasPrev)
        {
            CheckChannel("view", _prevView, viewPos, frame, tick, instanceId, owned);
            CheckChannel("sim", _prevSim, simPos, frame, tick, instanceId, owned);
            SampleSmoothness(viewPos.x - _prevView.x, owned);
        }
        else if (_hasDisabledSample)
        {
            var dx = viewPos.x - _disabledView.x;
            if (dx < -BackwardEps)
            {
                Record(diagnostics, "segmentJumpBackward", "view", frame, _disabledFrame, tick, instanceId, owned,
                    _disabledView, viewPos);
            }

            _hasDisabledSample = false;
        }

        _hasPrev = true;
        _prevView = viewPos;
        _prevSim = simPos;
        _prevFrame = frame;
        _previousContext = _currentContext;
        _hasPreviousContext = true;
    }

    private void SampleSmoothness(float travel, bool owned)
    {
        if (!owned || !_hasPreviousContext || _currentContext.deleteRequested || _previousContext.deleteRequested ||
            _previousContext.age == 0 || _currentContext.age >= TrailProjectile.LifetimeTicks)
            return;

        var expected = TrailGunner.ProjectileSpeed * Time.deltaTime;
        if (expected <= 1e-5f)
            return;

        // Until the server confirms it, every reconcile re-creates the projectile, and other players'
        // spawns can shift its id; its view must glide through both.
        if (_currentContext.projectileVerifiedTick.HasValue)
            confirmedSmoothness.Add(travel / expected);
        else
            unconfirmedSmoothness.Add(travel / expected);
    }

    private void CheckChannel(string channel, Vector3 prev, Vector3 cur, int frame, ulong tick, uint instanceId, bool owned)
    {
        var dx = cur.x - prev.x;
        var lateral = Mathf.Max(Mathf.Abs(cur.y - prev.y), Mathf.Abs(cur.z - prev.z));

        // The view channel legitimately renders corrections: a fire input arriving one tick
        // late server-side lands the authoritative projectile one tick behind the prediction,
        // and a catch-up burst can compress that whole correction into one rendered frame.
        // Allow up to one tick of travel backward on the view; sim and lateral stay strict,
        // so identity swaps and multi-tick jumps still fail.
        var backwardEps = BackwardEps;
        if (channel == "view" && _pt.predictionManager != null)
            backwardEps += TrailGunner.ProjectileSpeed * _pt.predictionManager.tickDelta;

        if (dx < -backwardEps)
        {
            if (-dx > maxBackward)
                maxBackward = -dx;
            Record(owned ? failures : diagnostics, owned ? "backward" : "backwardRemote", channel, frame, _prevFrame,
                tick, instanceId, owned, prev, cur);
        }
        else if (lateral > LateralEps)
        {
            Record(owned ? failures : diagnostics, owned ? "lateral" : "lateralRemote", channel, frame, _prevFrame,
                tick, instanceId, owned, prev, cur);
        }
    }

    private void Record(List<Sample> target, string kind, string channel, int frame, int prevFrame, ulong tick,
        uint instanceId, bool owned, Vector3 prev, Vector3 cur)
    {
        var key = kind + "/" + channel;
        kindCounts.TryGetValue(key, out var count);
        kindCounts[key] = count + 1;

        if (target.Count >= MaxRecorded)
            return;

        var sample = new Sample
        {
            kind = kind,
            channel = channel,
            frame = frame,
            prevFrame = prevFrame,
            tick = tick,
            instanceId = instanceId,
            segment = _segment,
            owned = owned,
            prev = prev,
            cur = cur,
            hasPreviousContext = _hasPreviousContext,
            previousContext = _previousContext,
            currentContext = _currentContext,
            hasPooledContext = _hasPooledContext,
            pooledContext = _pooledContext,
            pooledFrame = _pooledFrame,
            enabledFrame = _enabledFrame
        };

        if (_pt && _pt.TryGetLatestVerifiedState(out var verifiedTick, out var verifiedState))
        {
            sample.hasVerified = true;
            sample.verifiedTick = verifiedTick;
            sample.verifiedPose = verifiedState.unityPosition;
        }

        if (_proj && _pt.predictionManager)
        {
            var pm = _pt.predictionManager;
            sample.isRegisteredInstance = pm.TryGetIdentity(_proj.id, out var registered) && registered == _proj;
            if (pm.hierarchy)
            {
                var records = pm.hierarchy.currentState.spawnedPrefabs;
                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (!record.instanceId.Equals(_proj.id.objectId))
                        continue;
                    sample.hasSpawnRecord = true;
                    sample.spawnPosition = record.spawnPosition;
                    sample.spawnOwner = record.owner;
                    break;
                }
            }
        }

        target.Add(sample);
    }
}
