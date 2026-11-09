using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Collections.Generic;
using System.IO;
using PurrNet.Prediction.StateMachine;
using PurrNet.Transports;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Prediction.Recording
{
    /// <summary>
    /// Records a match to a self-describing .purrrec file (see recording/FORMAT.md): every predicted
    /// component's state and input per tick, entity transforms and colliders, players, and each player's
    /// camera. Add it next to the <see cref="PredictionManager"/>; it records on the server or host and, on
    /// clients, forwards their camera (and anything written through <see cref="WriteSample{T}"/>).
    /// Once running it makes no garbage: storage is pooled, metadata is written straight to bytes, and
    /// managed state fields are read in place.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class PredictionRecorder : MonoBehaviour, IPredictedDataVisitor
    {
        [SerializeField] private PredictionManager _predictionManager;
        [Tooltip("Start recording as soon as this machine is the server. Never on CI machines (the CI environment variable and the like).")]
        [SerializeField] private bool _recordOnServer = true;
        [Tooltip("Also record play sessions in the editor (kept on disk, never uploaded). Off: editor sessions are development, not gameplay.")]
        [SerializeField] private bool _recordInEditor;
        [Tooltip("Record the camera each player renders with.")]
        [SerializeField] private bool _recordCamera = true;
        [Tooltip("Folder for recordings. Relative paths are under Application.persistentDataPath.")]
        [SerializeField] private string _directory = "Recordings";
        [SerializeField] private float _chunkSeconds = 10f;
        [Tooltip("How often clients send their samples to the server.")]
        [SerializeField] private float _clientSendInterval = 0.1f;
        [Tooltip("Send finished recordings to the PurrNet recordings service, then delete them. Needs a project key: " +
                 "the one below, or the project linked through PurrServices.")]
        [SerializeField] private bool _upload = true;
        [SerializeField] private string _uploadEndpoint = "https://purrnet.dev/api/services/recordings";
        [Tooltip("Project public key. Empty uses the project linked through PurrServices.")]
        [SerializeField] private string _projectKey = "";
        [Tooltip("Recordings that could not be sent yet are kept up to this size; the oldest are deleted first.")]
        [SerializeField] private int _maxLocalMegabytes = 1024;
        [Tooltip("Most time per tick, in milliseconds, spent looking for collider changes (colliders added, removed, " +
                 "resized or moved on entities and in the scene). Rows are always recorded in full; in very large " +
                 "scenes a change is only noticed a little later.")]
        [SerializeField] private float _colliderCheckBudgetMs = 0.1f;
        [Tooltip("Memory reserved when recording starts for one chunk's rows, in KiB (two such buffers, plus the " +
                 "writer's working memory of about the same size). A chunk ends early when its buffer is full, so " +
                 "recording never allocates during play; bigger buffers make slightly smaller files for big games.")]
        [SerializeField] private int _chunkBufferKilobytes = 2048;

        public static PredictionRecorder instance { get; private set; }

        /// <summary>Free-form labels written into the header (map, mode, build hash...). Set before recording starts.</summary>
        public readonly Dictionary<string, string> labels = new Dictionary<string, string>();

        public bool isRecording => _file != null;
        public string currentPath => _file?.path;
        public long bytesWritten => _file?.bytesWritten ?? 0;

        public event Action<string> onRecordingFinished;

        sealed class Entity
        {
            public int id;
            public GameObject gameObject;
            public Transform root;
            public PredictedObjectID objectId;
            public ulong lastSeenTick;
            public ulong spawnTick;
            public PlayerID? owner;
            public bool isRoot;
            public StreamRows transform;
            public readonly List<Component> components = new List<Component>(4);
            /// <summary>Binary form of the last written collider set; the JSON is only built when this changes.</summary>
            public readonly RecordingBuffer colliderKey = new RecordingBuffer(ByteShelf.shared, 256);
            public bool hasColliderKey;
            public readonly List<TrackedCollider> colliders = new List<TrackedCollider>(8);
            /// <summary>Colliders currently moving relative to the entity (sampled every tick).</summary>
            public int movingColliders;
            /// <summary>Body renderers and their GameObjects (read every tick for visibility).</summary>
            public readonly List<Renderer> renderers = new List<Renderer>(8);
            public readonly List<GameObject> rendererObjects = new List<GameObject>(8);
            // Everything the last collider check found under the entity, kept between checks: Unity creates a C#
            // object the first time a component is handed to C# and drops it once nothing references it, so a check
            // that let go of them would make garbage every time.
            public readonly List<PredictedIdentity> foundIdentities = new List<PredictedIdentity>(4);
            public readonly List<Renderer> foundRenderers = new List<Renderer>(8);
            public readonly List<Renderer> foundGraphics = new List<Renderer>(4);
            public readonly List<PredictedTransform> foundTransforms = new List<PredictedTransform>(2);
            public readonly List<Collider> foundColliders = new List<Collider>(8);
        }

        /// <summary>What an identity index resolved to last tick.</summary>
        struct IdentitySlot
        {
            public PredictedIdentity identity;
            public Entity entity;
            public int entityId;
            public Component component;
        }

        /// <summary>
        /// A collider of the current set. Its pose relative to the entity is checked with the set; once it moves, it
        /// gets a pose stream sampled every tick until it has been still for a few checks.
        /// </summary>
        sealed class TrackedCollider
        {
            public Collider collider;
            public long id;
            public bool streamable;
            /// <summary>Last checked pose, rounded like the poses in COLLIDERS records.</summary>
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
            public StreamRows stream;
            public bool moving;
            public int stillChecks;
        }

        sealed class Component
        {
            public PredictedIdentity identity;
            public StreamRows state;
            public StreamRows input;
        }

        sealed class Player
        {
            public PlayerID id;
            public StreamRows rows;
            public ulong lastSeenTick;
        }

        /// <summary>How a stream is described in its STREAM record.</summary>
        enum StreamKind { State, Input, Transform, Player, Custom, Collider }

        struct StreamInfo
        {
            public StreamKind kind;
            public int entity;
            public ulong component;
            public Type componentType;
            public ulong player;
            public bool hasPlayer;
            public string sample;
            public long collider;
        }

        readonly struct CustomKey : IEquatable<CustomKey>
        {
            readonly string _stream;
            readonly ulong _player;
            readonly bool _hasPlayer;

            public CustomKey(string stream, PlayerID? player)
            {
                _stream = stream;
                _hasPlayer = player.HasValue;
                _player = player.HasValue ? player.Value.id.value : 0;
            }

            public bool Equals(CustomKey other) =>
                _player == other._player && _hasPlayer == other._hasPlayer && string.Equals(_stream, other._stream);

            public override bool Equals(object obj) => obj is CustomKey other && Equals(other);
            public override int GetHashCode() => (_stream?.GetHashCode() ?? 0) * 31 + _player.GetHashCode() + (_hasPlayer ? 1 : 0);
        }

        readonly struct RemoteKey : IEquatable<RemoteKey>
        {
            readonly ulong _player;
            readonly ushort _key;

            public RemoteKey(ulong player, ushort key)
            {
                _player = player;
                _key = key;
            }

            public bool Equals(RemoteKey other) => _player == other._player && _key == other._key;
            public override bool Equals(object obj) => obj is RemoteKey other && Equals(other);
            public override int GetHashCode() => _player.GetHashCode() * 65599 + _key;
        }

        sealed class RemoteStream
        {
            public string name;
            public Type type;
        }

        /// <summary>Samples a client collected for one stream since its last send.</summary>
        sealed class Outgoing
        {
            public ushort key;
            public string stream;
            public string type;
            public int rowSize;
            public readonly RecordingBuffer rows = new RecordingBuffer(256);
            public int count;
            public bool declared;
        }

        /// <summary>A chunk's log on its way to the writer thread, encoded there.</summary>
        sealed class ChunkJob : IStepwiseRecordProducer
        {
            public readonly PredictionRecorder owner;
            public readonly ChunkLog log;
            bool _stepping;

            public ChunkJob(PredictionRecorder owner)
            {
                this.owner = owner;
                log = new ChunkLog(Shelf);
            }

            public void Begin()
            {
                owner._encoder.Begin(log);
                _stepping = true;
            }

            public bool Step(long deadline) => owner._encoder.Step(deadline);

            public RecordingBuffer Produce()
            {
                if (!_stepping)
                    return owner._encoder.Encode(log);
                _stepping = false;
                owner._encoder.Step(long.MaxValue);
                return owner._encoder.result;
            }

            public void Release()
            {
                _stepping = false;
                log.Clear();
                lock (owner._jobPool)
                    owner._jobPool.Push(this);
            }
        }

        /// <summary>A BATCH of metadata records on its way to the writer thread.</summary>
        sealed class MetaBatch : IRecordProducer
        {
            public PredictionRecorder owner;
            public RecordingBuffer buffer = new RecordingBuffer(Shelf, 4096);

            public RecordingBuffer Produce() => buffer;

            public void Release()
            {
                buffer.Clear();
                lock (owner._metaPool)
                    owner._metaPool.Push(this);
            }
        }

        RecordingTypeRegistry _types = new RecordingTypeRegistry();
        readonly List<StreamRows> _streams = new List<StreamRows>();
        readonly Dictionary<long, Entity> _entitiesByGameObject = new Dictionary<long, Entity>();
        readonly List<Entity> _entities = new List<Entity>();
        readonly Dictionary<ulong, Player> _players = new Dictionary<ulong, Player>();
        readonly List<ulong> _playersLeft = new List<ulong>();
        readonly Dictionary<CustomKey, StreamRows> _customStreams = new Dictionary<CustomKey, StreamRows>();
        readonly Dictionary<long, int> _meshIds = new Dictionary<long, int>();
        readonly HashSet<object> _visiting = new HashSet<object>(ReferenceComparer.instance);
        readonly List<string> _pendingTypes = new List<string>();
        readonly RecordingBuffer _scratch = new RecordingBuffer(256);
        readonly RecordingBuffer _check = new RecordingBuffer(256);
        readonly JsonBuilder _json = new JsonBuilder();
        readonly List<PredictedIdentity> _identityList = new List<PredictedIdentity>();
        IdentitySlot[] _identitySlots = Array.Empty<IdentitySlot>();

        // Pools: the recorder reuses everything it creates per spawn, stream and chunk.
        readonly Stack<Entity> _entityPool = new Stack<Entity>(256);
        readonly Stack<Component> _componentPool = new Stack<Component>(256);
        readonly Stack<StreamRows> _streamPool = new Stack<StreamRows>(1024);
        readonly Stack<ChunkJob> _jobPool = new Stack<ChunkJob>();
        // Arrays the recorder's big buffers (chunk logs, metadata batches) grew out of, kept for the next buffer that
        // grows: a new log starts with the largest, so growing never makes garbage.
        static readonly ByteShelf Shelf = ByteShelf.shared;
        readonly Stack<MetaBatch> _metaPool = new Stack<MetaBatch>();
        readonly ChunkEncoder _encoder = new ChunkEncoder();
        // The chunk being collected: its log and the streams that have rows in it (by slot).
        ChunkJob _job;
        readonly List<StreamRows> _slots = new List<StreamRows>();

        // Unity allocates a new string each time a name is read, so names are read once and kept.
        const int MaxCachedNames = 4096;
        readonly Dictionary<long, string> _names = new Dictionary<long, string>(MaxCachedNames);
        readonly Dictionary<int, string> _prefabNames = new Dictionary<int, string>();
        readonly Dictionary<Type, string> _typeNames = new Dictionary<Type, string>();
        readonly Dictionary<RowLayout, int[]> _poseColumns = new Dictionary<RowLayout, int[]>();

        // Client: samples waiting to be sent. Server: what each client's stream keys mean.
        readonly Dictionary<string, Outgoing> _outgoing = new Dictionary<string, Outgoing>();
        readonly Dictionary<RemoteKey, RemoteStream> _remoteStreams = new Dictionary<RemoteKey, RemoteStream>();
        readonly Dictionary<string, Type> _resolvedTypes = new Dictionary<string, Type>();
        bool _wasConnected;
        float _nextSend;

        readonly RecorderStats _stats = new RecorderStats();
        RecordingSummary _summary;
        RecorderStats.Section _statTick, _statPlayers, _statIdentities, _statSpawn, _statManaged, _statColliders, _statChecks, _statFlush;

        RecordingFile _file;
        RecordingBuffer _meta = new RecordingBuffer(Shelf, 4096);
        bool _stoppedThisSession;
        int _nextEntityId = 1;
        int _nextStreamId = 1;
        int _nextMeshId = 1;
        ulong _chunkFirstTick;
        ulong _lastTick;
        ulong _ticksRecorded;
        // The finished recording's entity count, for its report (the live counter starts over when it stops).
        int _lastEntities;
        int _chunkTicks;
        int _chunks;
        int _peakLogBytes;
        float _startTime;
        bool _subscribedNetwork;
        NetworkManager _networkManager;

        // Visitor context for the component being captured.
        Component _visitingComponent;
        Entity _visitingEntity;
        ulong _visitTick;
        bool _declareOnly;

        public PredictionManager predictionManager => _predictionManager;

        /// <summary>Folder for the next recording. Relative paths are under Application.persistentDataPath.</summary>
        public void SetDirectory(string directory)
        {
            _directory = directory;
            ConfigureUploads();
        }

        void Awake()
        {
            instance = this;
            if (!_predictionManager)
                _predictionManager = GetComponent<PredictionManager>();
            if (!_predictionManager)
                _predictionManager = FindFirstObjectByType<PredictionManager>();
            _startTime = Time.realtimeSinceStartup;
            if (_recordCamera && !GetComponent<RecordLocalCamera>())
                gameObject.AddComponent<RecordLocalCamera>();
            ConfigureUploads();
            _summary = new RecordingSummary(TypeName);

            _statTick = _stats.Add("tick");
            _statPlayers = _stats.Add("players");
            _statIdentities = _stats.Add("identities");
            _statSpawn = _stats.Add(" spawn");
            _statManaged = _stats.Add(" managed");
            _statColliders = _stats.Add("colliders");
            _statChecks = _stats.Add(" checks");
            _statFlush = _stats.Add("flush");
        }

        void OnDestroy()
        {
            StopRecording();
            UnsubscribeNetwork();
            if (instance == this)
                instance = null;
        }

        void OnApplicationQuit() => StopRecording();

        void Update()
        {
            if (!_predictionManager)
                return;

            if (!_subscribedNetwork)
                SubscribeNetwork();

            bool server = _predictionManager.isServer;
            if (!server)
                _stoppedThisSession = false;

            if (_recordOnServer && RecordsAutomatically() && server && !isRecording && !_stoppedThisSession && _predictionManager.isSpawned)
                StartRecording();
            else if (isRecording && !server)
                StopRecording();

            if (!server && Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + _clientSendInterval;
                SendOutgoing();
            }

            // WebGL has no writer thread: queued chunks are encoded here, one per frame (a no-op elsewhere).
            _file?.Pump();
        }

        void SubscribeNetwork()
        {
            _networkManager = _predictionManager.networkManager ? _predictionManager.networkManager : NetworkManager.main;
            if (!_networkManager)
                return;
            _networkManager.Subscribe<RecordedStreamDeclare>(OnStreamDeclared, true);
            _networkManager.Subscribe<RecordedSamplesPacket>(OnSamplesReceived, true);
            _subscribedNetwork = true;
        }

        void UnsubscribeNetwork()
        {
            if (_subscribedNetwork && _networkManager)
            {
                _networkManager.Unsubscribe<RecordedStreamDeclare>(OnStreamDeclared, true);
                _networkManager.Unsubscribe<RecordedSamplesPacket>(OnSamplesReceived, true);
            }
            _subscribedNetwork = false;
        }

        /// <summary>Whether matches start recording on their own here: not on CI, and in the editor only when asked.</summary>
        bool RecordsAutomatically() => !RecordingUploader.runningOnCI && (!Application.isEditor || _recordInEditor);

        string RecordingDirectory() => Path.IsPathRooted(_directory)
            ? _directory
            : Path.Combine(Application.persistentDataPath, _directory);

        /// <summary>
        /// Where finished recordings are sent and with which project key (empty uses the project linked through
        /// PurrServices). An empty endpoint turns uploads off.
        /// </summary>
        public void SetUpload(string endpoint, string projectKey)
        {
            _upload = !string.IsNullOrEmpty(endpoint);
            _uploadEndpoint = endpoint;
            _projectKey = projectKey;
            ConfigureUploads();
        }

        void ConfigureUploads()
        {
            string key = string.IsNullOrWhiteSpace(_projectKey) ? PurrNet.Transports.PurrServicesProjectLink.projectKey : _projectKey.Trim();
            // Editor and CI sessions are development and tests, not gameplay: they stay out of the data pool.
            if (_upload && !Application.isEditor && !RecordingUploader.runningOnCI && !string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(_uploadEndpoint))
                RecordingUploader.Configure(_uploadEndpoint, key, RecordingDirectory(), (long)_maxLocalMegabytes * 1024 * 1024);
        }

        /// <summary>
        /// Starts a new recording file. Called automatically on the server when <c>recordOnServer</c> is set.
        /// </summary>
        public void StartRecording(string fileName = null)
        {
            if (isRecording || !_predictionManager || !_predictionManager.isServer)
                return;

            string directory = RecordingDirectory();
            fileName ??= $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Application.productName}.purrrec";
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');

            // Every file is self-contained, so type ids and records start over.
            _types = new RecordingTypeRegistry();
            _poseColumns.Clear();
            _file = new RecordingFile(Path.Combine(directory, fileName), (long)_maxLocalMegabytes * 1024 * 1024);
            RecordingUploader.SetActiveRecording(_file.path);
            _chunkTicks = Mathf.Max(1, Mathf.RoundToInt(_chunkSeconds * _predictionManager.tickRate));
            _chunkFirstTick = _predictionManager.localTick;
            _lastTick = 0;
            _ticksRecorded = 0;
            _chunks = 0;
            _peakLogBytes = 0;
            _maxTickBytes = 0;
            _logCapacity = Mathf.NextPowerOfTwo(Mathf.Max(64, _chunkBufferKilobytes) * 1024);
            ReserveChunkMemory();
            _stats.Reset();
            _stats.file = _file;
            _summary.Begin(_predictionManager.tickRate);
            _slots.Clear();

            WriteHeader();
            _hasStaticColliderKey = false;
            ResetStaticCheck();
            RefreshStaticColliders(_predictionManager.localTick);
            _predictionManager.onServerTickReady += OnServerTickReady;
        }

        /// <summary>Flushes and closes the current recording.</summary>
        public void StopRecording()
        {
            if (_file == null)
                return;

            _stoppedThisSession = true;
            if (_predictionManager)
                _predictionManager.onServerTickReady -= OnServerTickReady;

            FlushChunk();
            foreach (var entity in _entities)
                WriteMeta(RecordKind.Despawn, _json.Clear().BeginObject()
                    .Prop("entity", entity.id).Prop("tick", _lastTick + 1).EndObject());

            FlushPendingTypes();
            FlushMeta();
            _json.Clear().BeginObject()
                .Prop("lastTick", _lastTick).Prop("ticks", _ticksRecorded).Prop("entities", _nextEntityId - 1)
                .Prop("streams", _nextStreamId - 1).Prop("chunks", _chunks);
            _summary.Write(_json, _predictionManager ? _predictionManager.tickRate : 0);
            EnqueueJson(RecordKind.Footer, _json.EndObject(), true);

            var file = _file;
            _file = null;
            file.Dispose();
            _lastEntities = _nextEntityId - 1;
            _stats.file = null;
            _lastFile = file;

            foreach (var entity in _entities)
                ReleaseEntity(entity);
            ReleaseColliders(_staticColliders);
            ResetStaticCheck();
            _staticMovingColliders = 0;
            _streams.Clear();
            _entities.Clear();
            _entitiesByGameObject.Clear();
            Array.Clear(_identitySlots, 0, _identitySlots.Length);
            _players.Clear();
            _customStreams.Clear();
            _meshIds.Clear();
            _names.Clear();
            _prefabNames.Clear();
            _nextEntityId = 1;
            _nextStreamId = 1;
            _nextMeshId = 1;
            RecordingUploader.SetActiveRecording(null);
            RecordingUploader.NotifyFinished();
            onRecordingFinished?.Invoke(file.path);
        }

        // ---- Samples --------------------------------------------------------------------------------

        /// <summary>
        /// Adds a sample to a custom stream. On the server it is written directly; on a client it is sent to the
        /// server. Samples sent from clients must be unmanaged (no classes, strings, arrays or lists) and at most
        /// 512 bytes with their envelope.
        /// </summary>
        public void WriteSample<T>(string stream, in T sample) where T : struct
        {
            if (!_predictionManager)
                return;

            var envelope = new ClientSample<T>
            {
                clientTick = _predictionManager.localTick,
                viewTick = _predictionManager.viewTick,
                clientTime = Time.realtimeSinceStartupAsDouble - _startTime,
                sample = sample
            };

            if (_predictionManager.isServer)
            {
                // A dedicated server has no local player; a host's id is unknown for its first frames.
                if (_predictionManager.localPlayer.HasValue)
                    WriteServerSample(stream, _predictionManager.localPlayer, ref envelope);
                return;
            }

            if (!_outgoing.TryGetValue(stream, out var pending))
            {
                var layout = _types.GetRow(typeof(ClientSample<T>));
                if (layout.hasManaged || layout.rowSize > RecordedSamplesPayload.Size)
                {
                    Debug.LogError($"[PredictionRecorder] Client samples must be unmanaged and at most {RecordedSamplesPayload.Size} bytes; {typeof(T)} is not.");
                    return;
                }
                pending = new Outgoing
                {
                    key = (ushort)(_outgoing.Count + 1),
                    stream = stream,
                    type = typeof(ClientSample<T>).AssemblyQualifiedName,
                    rowSize = layout.rowSize
                };
                _outgoing[stream] = pending;
            }

            _types.GetRow(typeof(ClientSample<T>)).WriteFixed(ref envelope, pending.rows);
            pending.count++;
        }

        /// <summary>
        /// Server only. Adds a sample to a custom stream on behalf of <paramref name="player"/>, for players that have
        /// no client of their own (bots) or data the server observes about a player.
        /// </summary>
        public void WriteSampleFor<T>(string stream, PlayerID player, in T sample) where T : struct
        {
            if (!_predictionManager || !_predictionManager.isServer)
                return;

            var envelope = new ClientSample<T>
            {
                clientTick = _predictionManager.localTick,
                viewTick = _predictionManager.viewTick,
                clientTime = Time.realtimeSinceStartupAsDouble - _startTime,
                sample = sample
            };
            WriteServerSample(stream, player, ref envelope);
        }

        void WriteServerSample<T>(string stream, PlayerID? player, ref ClientSample<T> envelope) where T : struct
        {
            if (!isRecording)
                return;
            var rows = GetCustomStream(stream, typeof(ClientSample<T>), player);
            AppendRow(rows, SampleTick(), ref envelope);
        }

        ulong SampleTick() => Math.Max(_predictionManager.localTick, _chunkFirstTick);

        unsafe void SendOutgoing()
        {
            if (!_networkManager)
                return;

            // A new connection has not heard of any stream key yet.
            bool connected = _networkManager.isClient;
            if (connected != _wasConnected)
            {
                _wasConnected = connected;
                foreach (var pair in _outgoing)
                    pair.Value.declared = false;
            }
            if (!connected)
                return;

            foreach (var pair in _outgoing)
            {
                var o = pair.Value;
                if (o.count == 0)
                    continue;

                if (!o.declared)
                {
                    _networkManager.SendToServer(new RecordedStreamDeclare { key = o.key, stream = o.stream, type = o.type },
                        Channel.ReliableOrdered);
                    o.declared = true;
                }

                int perPacket = RecordedSamplesPayload.Size / o.rowSize;
                for (int sent = 0; sent < o.count; sent += perPacket)
                {
                    int rows = Math.Min(perPacket, o.count - sent);
                    var packet = new RecordedSamplesPacket { key = o.key, rowSize = (ushort)o.rowSize, rowCount = (byte)rows };
                    fixed (byte* src = &o.rows.data[sent * o.rowSize])
                        UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref packet.payload), src, rows * o.rowSize);
                    _networkManager.SendToServer(packet, Channel.ReliableOrdered);
                }
                o.rows.Clear();
                o.count = 0;
            }
        }

        void OnStreamDeclared(PlayerID player, RecordedStreamDeclare declare, bool asServer)
        {
            if (string.IsNullOrEmpty(declare.type) || string.IsNullOrEmpty(declare.stream))
                return;
            if (!_resolvedTypes.TryGetValue(declare.type, out var type))
                _resolvedTypes[declare.type] = type = Type.GetType(declare.type);
            if (type == null)
                return;
            _remoteStreams[new RemoteKey(player.id.value, declare.key)] = new RemoteStream { name = declare.stream, type = type };
        }

        unsafe void OnSamplesReceived(PlayerID player, RecordedSamplesPacket packet, bool asServer)
        {
            if (!isRecording || packet.rowCount == 0)
                return;
            if (!_remoteStreams.TryGetValue(new RemoteKey(player.id.value, packet.key), out var remote))
                return;

            var rows = GetCustomStream(remote.name, remote.type, player);
            int rowSize = rows.layout.rowSize;
            if (rows.layout.hasManaged || rowSize != packet.rowSize || rowSize * packet.rowCount > RecordedSamplesPayload.Size)
                return;

            byte* src = (byte*)UnsafeUtility.AddressOf(ref packet.payload);
            ulong tick = SampleTick();
            var log = _job.log;
            for (int i = 0; i < packet.rowCount; i++)
            {
                BeginRow(rows, tick, log);
                log.bytes.WriteBytes(src + i * rowSize, rowSize);
            }
        }

        StreamRows GetCustomStream(string stream, Type type, PlayerID? player)
        {
            var key = new CustomKey(stream, player);
            if (_customStreams.TryGetValue(key, out var rows))
                return rows;

            rows = CreateStream(type, new StreamInfo
            {
                kind = StreamKind.Custom, sample = stream,
                hasPlayer = player.HasValue, player = player.HasValue ? player.Value.id.value : 0
            });
            _customStreams[key] = rows;
            return rows;
        }

        // ---- Ticks ------------------------------------------------------------------------------------

        // The recorder logs nothing. What the last recording cost is built only when asked for (tests, benchmarks).
        RecordingFile _lastFile;

        /// <summary>The last finished recording's size and the recorder's cost per section, as text.</summary>
        [UnityEngine.Scripting.Preserve]
        internal string DescribeLastRecording()
        {
            var file = _lastFile;
            if (file == null)
                return "";
            return $"[PredictionRecorder] Finished {file.path}: {_ticksRecorded} ticks, {_lastEntities} entities, " +
                   $"{file.bytesWritten / 1024f:0.0} KiB on disk ({file.rawBytes / 1024f:0.0} KiB before compression); " +
                   (RecordingFile.encodesOnMainThread
                       ? $"main thread {file.produceMs:0}ms encoding (longest frame {file.maxRecordMs:0.0}ms), worker {file.compressMs:0}ms compressing, "
                       : $"writer thread {file.produceMs:0}ms encoding, {file.compressMs:0}ms compressing, longest record {file.maxRecordMs:0.0}ms, ") +
                   $"largest chunk log {_peakLogBytes / 1048576f:0.0} MiB{(file.nativeDeflate ? "" : ", DeflateStream fallback")}\n" +
                   _stats.Summary(_ticksRecorded) + _types.DescribeReflection();
        }

        /// <summary>Queues a JSON record from the builder's bytes through a pooled buffer (no string is made).</summary>
        void EnqueueJson(RecordKind kind, JsonBuilder json, bool compress)
        {
            MetaBatch batch;
            lock (_metaPool)
                batch = _metaPool.Count > 0 ? _metaPool.Pop() : null;
            batch ??= new MetaBatch { owner = this };
            batch.buffer.Clear();
            batch.buffer.WriteBytes(json.data, 0, json.length);
            _file.Enqueue(kind, batch, compress);
        }

        void WriteHeader()
        {
            var json = _json.Clear().BeginObject()
                .Prop("format", "purrrec").Prop("version", 1)
                .Prop("game", Application.productName).Prop("gameVersion", Application.version)
                .Prop("unityVersion", Application.unityVersion)
                .Prop("tickRate", _predictionManager.tickRate)
                .Prop("firstTick", _predictionManager.localTick)
                .Prop("startedUtc", DateTime.UtcNow.ToString("o"))
                .Prop("scene", _predictionManager.gameObject.scene.name)
                .Prop("recorder", _predictionManager.isClient ? "host" : "server")
                .Key("labels").BeginObject();
            foreach (var pair in labels)
                json.Prop(pair.Key, pair.Value);
            json.EndObject().EndObject();
            EnqueueJson(RecordKind.Header, json, false);
        }

        void OnServerTickReady(ulong tick)
        {
            if (_file == null)
                return;

            _stats.Begin(_statTick);
            // A chunk also ends early when its log could not take another tick within the reservation (whatever array
            // the log holds): the memory reserved at the start, and the writer's working memory sized for it, is all
            // the recorder uses.
            var log = _job.log.bytes;
            if (tick - _chunkFirstTick >= (ulong)_chunkTicks || (log.length > 0 && log.length + 2 * _maxTickBytes > _logCapacity))
            {
                _stats.Begin(_statFlush);
                FlushChunk();
                _stats.End(_statFlush);
                log = _job.log.bytes;
            }
            int logStart = log.length;
            if (_ticksRecorded == 0)
                _chunkFirstTick = tick;

            _lastTick = tick;
            _ticksRecorded++;
            _stats.Begin(_statPlayers);
            RecordPlayers(tick);
            _stats.End(_statPlayers);
            RecordIdentities(tick);

            // A game bigger than the reservation (one tick fills half of it) gets a bigger one, once.
            _maxTickBytes = Math.Max(_maxTickBytes, _job.log.bytes.length - logStart);
            if (_maxTickBytes * 2 > _logCapacity)
            {
                _logCapacity = Mathf.NextPowerOfTwo(_maxTickBytes * 4);
                ReserveChunkMemory();
            }
            _stats.End(_statTick);
            _stats.CommitTick();
        }

        int _logCapacity;
        int _maxTickBytes;

        /// <summary>
        /// Reserves the log being filled, a spare for when it is handed to the writer, and the writer's working memory.
        /// </summary>
        void ReserveChunkMemory()
        {
            _job ??= TakeJob();
            _job.log.bytes.Reserve(_logCapacity);
            // Metadata (spawns, streams, collider sets) between two chunks.
            _meta.Reserve(_logCapacity / 8);
            lock (_jobPool)
            {
                if (_jobPool.Count == 0)
                    _jobPool.Push(new ChunkJob(this));
                foreach (var job in _jobPool)
                    job.log.bytes.Reserve(_logCapacity);
            }
            // The writer owns the encoder and its output while records are queued; reserve only while none are (it
            // otherwise grows them itself, once).
            if (!_file.busy)
            {
                _encoder.Reserve(_logCapacity);
                _file.ReserveOutput(_logCapacity / 2);
            }
        }

        void RecordPlayers(ulong tick)
        {
            var playersSystem = _predictionManager.players;
            if (playersSystem)
            {
                // The state's list itself: the IReadOnlyList property would box it on every read.
                var list = playersSystem.currentState.players;
                for (int i = 0; i < list.Count; i++)
                {
                    var id = list[i];
                    if (!_players.TryGetValue(id.id.value, out var player))
                    {
                        player = new Player { id = id };
                        player.rows = CreateStream(typeof(RecordedPlayerTick),
                            new StreamInfo { kind = StreamKind.Player, player = id.id.value, hasPlayer = true }, true);
                        _players[id.id.value] = player;
                        FlushPendingTypes();
                        WriteMeta(RecordKind.Player, _json.Clear().BeginObject()
                            .Prop("player", id.id.value).Prop("tick", tick).Prop("event", "joined")
                            .Prop("isBot", id.isBot).Prop("isLocal", _predictionManager.localPlayer == id)
                            .Prop("stream", player.rows.id).EndObject());
                    }

                    player.lastSeenTick = tick;
                    var row = new RecordedPlayerTick();
                    if (_predictionManager.TryGetPlayerViewOffset(id, tick, out var offset))
                    {
                        row.viewOffsetTicks = (float)offset;
                        row.hasViewOffset = true;
                    }
                    AppendRow(player.rows, tick, ref row);
                    _summary.OnPlayerTick(id, _predictionManager.localPlayer == id, tick, row.hasViewOffset, row.viewOffsetTicks);
                }
            }

            _playersLeft.Clear();
            foreach (var pair in _players)
                if (pair.Value.lastSeenTick != tick)
                    _playersLeft.Add(pair.Key);
            foreach (var id in _playersLeft)
            {
                if (_players.TryGetValue(id, out var gone))
                    gone.rows.retired = true;
                _players.Remove(id);
                WriteMeta(RecordKind.Player, _json.Clear().BeginObject()
                    .Prop("player", id).Prop("tick", tick).Prop("event", "left").EndObject());
            }
        }

        void RecordIdentities(ulong tick)
        {
            _stats.Begin(_statIdentities);
            int count = _predictionManager.identityCount;
            _visitTick = tick;
            bool sample = _summary.IsSampleTick(tick);

            if (_identitySlots.Length < count)
                Array.Resize(ref _identitySlots, Math.Max(count, _identitySlots.Length * 2));

            for (int i = 0; i < count; i++)
            {
                var identity = _predictionManager.GetIdentityAt(i);
                if (!identity)
                    continue;

                // Identities keep their index from tick to tick, so the entity found last tick is checked first.
                ref var slot = ref _identitySlots[i];
                Entity entity = slot.entity;
                Component component;
                if (ReferenceEquals(slot.identity, identity) && entity != null && entity.id == slot.entityId &&
                    !ReferenceEquals(entity.gameObject, null) && entity.objectId == identity.id.objectId)
                {
                    component = slot.component;
                }
                else
                {
                    long key = RecordingObjectId.Of(identity.gameObject);
                    if (!_entitiesByGameObject.TryGetValue(key, out entity) || entity.objectId != identity.id.objectId)
                    {
                        _stats.Begin(_statSpawn);
                        if (entity != null)
                            Despawn(entity, tick);
                        entity = Spawn(identity, tick);
                        _stats.End(_statSpawn);
                    }
                    component = FindComponent(entity, identity);
                    slot.identity = identity;
                    slot.entity = entity;
                    slot.entityId = entity.id;
                    slot.component = component;
                }

                if (entity.lastSeenTick != tick)
                {
                    entity.lastSeenTick = tick;
                    var pose = new RecordedTransform();
                    var t = entity.root;
                    t.GetPositionAndRotation(out pose.position, out pose.rotation);
                    pose.scale = t.lossyScale;
                    pose.visible = Visibility(entity);
                    AppendRow(entity.transform, tick, ref pose);
                    if (entity.owner.HasValue && entity.isRoot)
                        _summary.OnOwnedEntity(entity.owner.Value, entity.spawnTick, pose.position, tick, sample);
                }

                _visitingEntity = entity;
                _visitingComponent = component;
                identity.VisitData(this);
            }

            _visitingComponent = null;
            _visitingEntity = null;

            for (int i = _entities.Count - 1; i >= 0; i--)
            {
                if (_entities[i].lastSeenTick != tick)
                {
                    _stats.Begin(_statSpawn);
                    Despawn(_entities[i], tick);
                    _stats.End(_statSpawn);
                }
            }
            _summary.EndTick(tick, sample);
            _stats.End(_statIdentities);

            _stats.Begin(_statColliders);

            // Colliders appear, toggle, and animate after spawn (a map scaling its pieces in, say), so each entity's
            // set is re-checked every few ticks and the scene's every few seconds. Looking for changes is the only
            // recorder work that grows with the scene and is not data, so it gets a fixed slice of time per tick:
            // a bigger scene notices changes later instead of making ticks slower.
            _checkDeadline = Stopwatch.GetTimestamp() + (long)(_colliderCheckBudgetMs * 0.001 * Stopwatch.Frequency);
            _stats.Begin(_statChecks);
            CheckEntityColliders(tick);
            if (_staticCheck == StaticCheck.Idle && tick % StaticColliderCheckTicks == 0)
                BeginStaticCheck();
            if (_staticCheck != StaticCheck.Idle)
                ContinueStaticCheck(tick);
            _stats.End(_statChecks);

            // Colliders moving relative to their entity (a drawbridge tilting, a door) are sampled every tick.
            foreach (var entity in _entities)
            {
                if (entity.movingColliders > 0 && entity.gameObject)
                    SampleMovingColliders(entity.colliders, entity.gameObject.transform.worldToLocalMatrix, tick);
            }
            if (_staticMovingColliders > 0)
                SampleMovingColliders(_staticColliders, Matrix4x4.identity, tick);
            _stats.End(_statColliders);
        }

        long _checkDeadline;
        int _entityCheckCursor;
        float _entityChecksOwed;

        bool OverBudget => Stopwatch.GetTimestamp() > _checkDeadline;

        /// <summary>
        /// Re-checks entities' colliders round robin: a full pass every <see cref="ColliderCheckTicks"/> ticks when the
        /// time budget allows, and at least one entity per tick when it does not.
        /// </summary>
        void CheckEntityColliders(ulong tick)
        {
            int count = _entities.Count;
            if (count == 0)
                return;
            // Owed checks carry over while over budget, but never more than a pass.
            _entityChecksOwed = Math.Min(count, _entityChecksOwed + (float)count / ColliderCheckTicks);
            bool first = true;
            while (_entityChecksOwed >= 1f && (first || !OverBudget))
            {
                first = false;
                if (_entityCheckCursor >= _entities.Count)
                    _entityCheckCursor = 0;
                var entity = _entities[_entityCheckCursor++];
                // Spawned this tick: its colliders were just read.
                if (entity.spawnTick != tick)
                    RefreshEntityColliders(entity, tick);
                _entityChecksOwed -= 1f;
            }
        }

        Component FindComponent(Entity entity, PredictedIdentity identity)
        {
            foreach (var c in entity.components)
                if (ReferenceEquals(c.identity, identity))
                    return c;
            var created = _componentPool.Count > 0 ? _componentPool.Pop() : new Component();
            created.identity = identity;
            created.state = null;
            created.input = null;
            entity.components.Add(created);
            return created;
        }

        Entity Spawn(PredictedIdentity first, ulong tick)
        {
            var go = first.gameObject;
            var entity = _entityPool.Count > 0 ? _entityPool.Pop() : new Entity();
            entity.id = _nextEntityId++;
            entity.gameObject = go;
            entity.root = go.transform;
            entity.objectId = first.id.objectId;
            entity.lastSeenTick = 0;
            entity.hasColliderKey = false;
            _entitiesByGameObject[RecordingObjectId.Of(go)] = entity;
            _entities.Add(entity);

            // Declare every component stream up front so the ENTITY record can list them.
            _visitingEntity = entity;
            _visitTick = tick;
            go.GetComponents(_identityList);
            foreach (var identity in _identityList)
            {
                if (identity.id.objectId != entity.objectId)
                    continue;
                _visitingComponent = FindComponent(entity, identity);
                _declareOnly = true;
                identity.VisitData(this);
                _declareOnly = false;
            }
            _identityList.Clear();

            StreamRows poseBase = null;
            foreach (var c in entity.components)
                if (c.identity is PredictedTransform && c.state != null)
                    poseBase = c.state;
            entity.transform = CreateStream(typeof(RecordedTransform),
                new StreamInfo { kind = StreamKind.Transform, entity = entity.id }, true, poseBase);

            var hierarchy = _predictionManager.hierarchy;
            int prefabId = -1;
            string prefabName = null;
            var owner = first.owner;
            Entity parent = null;
            if (hierarchy && hierarchy.TryGetInstanceDetails(entity.objectId, out var details))
            {
                prefabId = details.prefabId;
                prefabName = PrefabName(prefabId);
            }

            var parentTransform = go.transform.parent;
            var parentIdentity = parentTransform ? parentTransform.GetComponentInParent<PredictedIdentity>() : null;
            if (parentIdentity)
                _entitiesByGameObject.TryGetValue(RecordingObjectId.Of(parentIdentity.gameObject), out parent);

            entity.spawnTick = tick;
            entity.owner = owner;
            entity.isRoot = parent == null;
            _summary.OnSpawn(prefabName, owner, parent == null, tick);
            foreach (var c in entity.components)
                if (c.identity is PredictedStateMachine machine)
                    _summary.TrackMachine(machine);

            FlushPendingTypes();
            var json = _json.Clear().BeginObject()
                .Prop("entity", entity.id).Prop("tick", tick).Prop("objectId", (ulong)entity.objectId.instanceId.value)
                .Prop("name", Name(go)).Prop("prefabId", prefabId).Prop("prefabName", prefabName)
                .Key("owner");
            if (owner.HasValue) json.Value(owner.Value.id.value); else json.Null();
            json.Key("parent");
            if (parent != null) json.Value(parent.id); else json.Null();
            WritePath(json.Key("path"), go.transform);
            json.Key("components").BeginArray();
            foreach (var c in entity.components)
            {
                json.BeginObject().Prop("index", (ulong)c.identity.id.componentId.value)
                    .Prop("type", TypeName(c.identity.GetType()));
                if (c.state != null) json.Prop("state", c.state.id);
                if (c.input != null) json.Prop("input", c.input.id);
                json.EndObject();
            }
            json.EndArray().Prop("transform", entity.transform.id).EndObject();
            WriteMeta(RecordKind.Entity, json);

            RefreshEntityColliders(entity, tick);
            return entity;
        }

        void Despawn(Entity entity, ulong tick)
        {
            int index = _entities.IndexOf(entity);
            if (index >= 0)
            {
                _entities.RemoveAt(index);
                if (index < _entityCheckCursor)
                    _entityCheckCursor--;
            }
            if (_entitiesByGameObject.TryGetValue(entity.gameObject ? RecordingObjectId.Of(entity.gameObject) : 0, out var current) && current == entity)
                _entitiesByGameObject.Remove(RecordingObjectId.Of(entity.gameObject));
            else
            {
                // The GameObject was destroyed; drop whichever key still points at this entity.
                long stale = 0;
                bool found = false;
                foreach (var pair in _entitiesByGameObject)
                    if (pair.Value == entity) { stale = pair.Key; found = true; break; }
                if (found)
                    _entitiesByGameObject.Remove(stale);
            }

            WriteMeta(RecordKind.Despawn, _json.Clear().BeginObject()
                .Prop("entity", entity.id).Prop("tick", tick).EndObject());
            ReleaseEntity(entity);
        }

        /// <summary>Retires the entity's streams (dropped after their last flush) and pools the entity.</summary>
        void ReleaseEntity(Entity entity)
        {
            if (entity.transform != null)
                entity.transform.retired = true;
            foreach (var c in entity.components)
            {
                if (c.state != null) c.state.retired = true;
                if (c.input != null) c.input.retired = true;
                c.identity = null;
                c.state = c.input = null;
                _componentPool.Push(c);
            }
            entity.components.Clear();
            ReleaseColliders(entity.colliders);
            entity.movingColliders = 0;
            entity.renderers.Clear();
            entity.rendererObjects.Clear();
            entity.foundIdentities.Clear();
            entity.foundRenderers.Clear();
            entity.foundGraphics.Clear();
            entity.foundTransforms.Clear();
            entity.foundColliders.Clear();
            entity.transform = null;
            entity.gameObject = null;
            entity.root = null;
            _entityPool.Push(entity);
        }

        void IPredictedDataVisitor.VisitState<T>(PredictedIdentity identity, ref T state)
        {
            var component = _visitingComponent;
            if (component == null)
                return;
            component.state ??= CreateStream(typeof(T), new StreamInfo
            {
                kind = StreamKind.State, entity = _visitingEntity.id,
                component = identity.id.componentId.value, componentType = identity.GetType()
            }, true);
            if (!_declareOnly)
                AppendRow(component.state, _visitTick, ref state);
        }

        void IPredictedDataVisitor.VisitInput<T>(PredictedIdentity identity, ref T input)
        {
            var component = _visitingComponent;
            if (component == null)
                return;
            component.input ??= CreateStream(typeof(T), new StreamInfo
            {
                kind = StreamKind.Input, entity = _visitingEntity.id,
                component = identity.id.componentId.value, componentType = identity.GetType()
            }, true);
            if (!_declareOnly)
                AppendRow(component.input, _visitTick, ref input);
        }

        StreamRows CreateStream(Type type, in StreamInfo info, bool forwardFill = false, StreamRows baseStream = null)
        {
            var layout = _types.GetRow(type);
            var rows = _streamPool.Count > 0 ? _streamPool.Pop() : new StreamRows();
            rows.id = _nextStreamId++;
            rows.layout = layout;
            rows.forwardFill = forwardFill;
            rows.retired = false;
            rows.box = null;
            rows.baseStream = null;
            rows.baseColumns = null;
            rows.slot = -1;
            rows.rowCount = 0;
            if (rows.lastValueOffset.Length != layout.managed.Count)
            {
                rows.lastValueOffset = new int[layout.managed.Count];
                rows.lastValueLength = new int[layout.managed.Count];
            }
            if (baseStream != null && info.kind == StreamKind.Transform)
            {
                rows.baseStream = baseStream;
                rows.baseColumns = PoseColumnsFor(layout, baseStream.layout);
            }
            _streams.Add(rows);

            FlushPendingTypes();
            var json = _json.Clear().BeginObject().Prop("id", rows.id).Prop("type", layout.id).Key("name");
            switch (info.kind)
            {
                case StreamKind.State:
                case StreamKind.Input:
                    json.BeginString().Part("e").Part(info.entity).Part(".c").Part((long)info.component)
                        .Part(info.kind == StreamKind.State ? ".state" : ".input").EndString()
                        .Prop("kind", info.kind == StreamKind.State ? "state" : "input")
                        .Prop("entity", info.entity).Prop("component", info.component)
                        .Prop("componentType", TypeName(info.componentType));
                    break;
                case StreamKind.Transform:
                    json.BeginString().Part("e").Part(info.entity).Part(".transform").EndString()
                        .Prop("kind", "transform").Prop("entity", info.entity);
                    break;
                case StreamKind.Player:
                    json.BeginString().Part("p").Part((long)info.player).EndString()
                        .Prop("kind", "player").Prop("player", info.player);
                    break;
                case StreamKind.Collider:
                    json.BeginString().Part(info.entity >= 0 ? "e" : "static");
                    if (info.entity >= 0)
                        json.Part(info.entity);
                    json.Part(".collider").Part(info.collider).EndString()
                        .Prop("kind", "collider").Key("entity");
                    if (info.entity >= 0) json.Value(info.entity); else json.Null();
                    json.Prop("collider", info.collider);
                    break;
                default:
                    json.BeginString();
                    if (info.hasPlayer)
                        json.Part("p").Part((long)info.player).Part(".");
                    json.Part(info.sample).EndString()
                        .Prop("kind", "custom").Prop("sample", info.sample)
                        .Key("player").Value(info.hasPlayer ? (long)info.player : -1);
                    break;
            }
            if (forwardFill)
                json.Prop("fill", true);
            if (rows.baseStream != null)
            {
                json.Key("base").BeginObject().Prop("stream", rows.baseStream.id).Key("columns").BeginObject();
                foreach (var (own, other) in PoseColumns)
                    json.Prop(own, other);
                json.EndObject().EndObject();
            }
            WriteMeta(RecordKind.Stream, json.EndObject());
            return rows;
        }

        // The generic transform duplicates PredictedTransform's pose; store it as a difference against that pose.
        static readonly (string, string)[] PoseColumns =
        {
            ("position.x", "unityPosition.x"), ("position.y", "unityPosition.y"), ("position.z", "unityPosition.z"),
            ("rotation.x", "unityRotation.x"), ("rotation.y", "unityRotation.y"), ("rotation.z", "unityRotation.z"),
            ("rotation.w", "unityRotation.w")
        };

        int[] PoseColumnsFor(RowLayout own, RowLayout other)
        {
            if (_poseColumns.TryGetValue(other, out var columns))
                return columns;
            columns = new int[PoseColumns.Length * 2];
            for (int i = 0; i < PoseColumns.Length; i++)
            {
                columns[i * 2] = ColumnOffset(own, PoseColumns[i].Item1);
                columns[i * 2 + 1] = ColumnOffset(other, PoseColumns[i].Item2);
            }
            _poseColumns[other] = columns;
            return columns;
        }

        static int ColumnOffset(RowLayout layout, string name)
        {
            foreach (var c in layout.columns)
                if (c.name == name)
                    return c.offset;
            throw new ArgumentException($"{layout.type} has no column {name}");
        }

        unsafe void AppendRow<T>(StreamRows rows, ulong tick, ref T value) where T : struct
        {
            BeginRow(rows, tick, _job.log);
            var log = _job.log.bytes;
            var layout = rows.layout;
            layout.WriteFixed(ref value, log);
            if (!layout.hasManaged)
                return;

            _stats.Begin(_statManaged);
            var writers = _types.GetManagedWriters(layout);
            byte* row = (byte*)UnsafeUtility.AddressOf(ref value);
            for (int i = 0; i < writers.Length; i++)
            {
                _scratch.Clear();
                _visiting.Clear();
                var writer = writers[i];
                try
                {
                    if (writer != null && !writer.disabled)
                    {
                        writer.Write(row, _types, _scratch, _visiting);
                        if (writer.NeedsSelfCheck(_scratch.length <= 2))
                            CheckManagedField(rows, ref value, i, writer);
                    }
                    else
                    {
                        _types.WriteTagged(_scratch, RowLayout.ReadManaged(Box(rows, ref value), layout.managed[i]), 0, _visiting);
                    }
                }
                catch (Exception)
                {
                    _scratch.Clear();
                    _scratch.WriteByte(12);
                }
                // The same value as this stream's previous row in the chunk is stored as a back-reference.
                if (rows.rowCount > 1 && SameAsLast(log, rows.lastValueOffset[i], rows.lastValueLength[i]))
                    log.WriteInt32(-1);
                else
                {
                    log.WriteInt32(_scratch.length);
                    rows.lastValueOffset[i] = log.length;
                    rows.lastValueLength[i] = _scratch.length;
                    log.WriteBytes(_scratch);
                }
            }
            _stats.End(_statManaged);
        }

        /// <summary>Starts a log entry: the stream's slot in this chunk, and the tick when it differs from the last entry's.</summary>
        void BeginRow(StreamRows rows, ulong tick, ChunkLog log)
        {
            if (rows.slot < 0)
            {
                rows.slot = _slots.Count;
                _slots.Add(rows);
            }
            rows.rowCount++;
            if (log.hasTick && log.lastTick == tick)
                log.bytes.WriteInt32(rows.slot);
            else
            {
                log.bytes.WriteInt32(rows.slot | ChunkLog.TickFollows);
                log.bytes.WriteUInt64(tick);
                log.lastTick = tick;
                log.hasTick = true;
            }
        }

        unsafe bool SameAsLast(RecordingBuffer log, int offset, int length)
        {
            if (length != _scratch.length)
                return false;
            if (length == 0)
                return true;
            fixed (byte* a = &log.data[offset])
            fixed (byte* b = &_scratch.data[0])
                return UnsafeUtility.MemCmp(a, b, length) == 0;
        }

        /// <summary>The first values of an in-place field writer are compared with the reflection path.</summary>
        void CheckManagedField<T>(StreamRows rows, ref T value, int field, ManagedFieldWriter writer) where T : struct
        {
            _check.Clear();
            _visiting.Clear();
            _types.WriteGeneric(_check, RowLayout.ReadManaged(Box(rows, ref value), rows.layout.managed[field]), 0, _visiting);
            if (_check.SameBytes(_scratch))
                return;
            writer.disabled = true;
            Debug.LogWarning($"[PredictionRecorder] The field writer for {rows.layout.managed[field].name} of " +
                             $"{TypeName(rows.layout.type)} disagreed with the reflection path; using reflection.");
            _scratch.CopyFrom(_check);
        }

        /// <summary>One box per stream, overwritten in place: a boxed struct is laid out like a class with one field of it.</summary>
        static object Box<T>(StreamRows rows, ref T value) where T : struct
        {
            object boxed = rows.box;
            if (boxed is T)
                UnsafeUtility.As<object, BoxedValue<T>>(ref boxed).value = value;
            else
                rows.box = boxed = value;
            return boxed;
        }

        sealed class BoxedValue<T> where T : struct
        {
            public T value;
        }

        void FlushPendingTypes()
        {
            _pendingTypes.Clear();
            if (!_types.TakePending(_pendingTypes))
                return;
            foreach (var json in _pendingTypes)
                WriteMeta(RecordKind.Type, json);
        }

        ChunkJob TakeJob()
        {
            ChunkJob job;
            lock (_jobPool)
                job = _jobPool.Count > 0 ? _jobPool.Pop() : null;
            job ??= new ChunkJob(this);
            job.log.Clear();
            job.log.bytes.Reserve(_logCapacity);
            return job;
        }

        void FlushChunk()
        {
            if (_file == null)
                return;

            if (_slots.Count > 0)
            {
                // Tagged values may have registered object types while this chunk was collected.
                FlushPendingTypes();
                FlushMeta();

                // Encoding takes milliseconds, so the log goes to the writer thread and the encode happens there.
                var log = _job.log;
                _peakLogBytes = Math.Max(_peakLogBytes, log.bytes.length);
                log.firstTick = _chunkFirstTick;
                log.tickCount = _lastTick + 1 - _chunkFirstTick;
                if (log.streams.Capacity < _slots.Count)
                    log.streams.Capacity = Mathf.NextPowerOfTwo(_slots.Count);
                foreach (var s in _slots)
                {
                    log.streams.Add(new ChunkStream
                    {
                        id = s.id, layout = s.layout, forwardFill = s.forwardFill, baseColumns = s.baseColumns,
                        baseSlot = s.baseStream != null ? s.baseStream.slot : -1
                    });
                }
                _file.Enqueue(RecordKind.Chunk, _job, true);
                _job = TakeJob();
                _chunks++;
            }

            foreach (var s in _slots)
            {
                s.slot = -1;
                s.rowCount = 0;
            }
            _slots.Clear();

            // Streams whose owner is gone (their last rows just left) are reused for new streams.
            int kept = 0;
            for (int i = 0; i < _streams.Count; i++)
            {
                var s = _streams[i];
                if (s.retired)
                {
                    s.baseStream = null;
                    s.box = null;
                    _streamPool.Push(s);
                    continue;
                }
                _streams[kept++] = s;
            }
            _streams.RemoveRange(kept, _streams.Count - kept);
            _chunkFirstTick = _lastTick + 1;
        }

        // ---- Metadata ---------------------------------------------------------------------------------

        // Metadata records are small and frequent (every bullet spawn writes several), so they are collected and
        // written as one compressed BATCH record right before each chunk, which keeps them ahead of the rows using them.

        void WriteMeta(RecordKind kind, JsonBuilder json)
        {
            _meta.WriteByte((byte)kind);
            _meta.WriteVarUInt((ulong)json.length);
            _meta.WriteBytes(json.data, 0, json.length);
        }

        void WriteMeta(RecordKind kind, string json)
        {
            _meta.WriteByte((byte)kind);
            _meta.WriteVarUInt((ulong)System.Text.Encoding.UTF8.GetByteCount(json));
            _meta.WriteUtf8(json);
        }

        void WriteMeta(RecordKind kind, byte[] payload, int length)
        {
            _meta.WriteByte((byte)kind);
            _meta.WriteVarUInt((ulong)length);
            _meta.WriteBytes(payload, 0, length);
        }

        void FlushMeta()
        {
            if (_meta.length == 0)
                return;
            MetaBatch batch;
            lock (_metaPool)
                batch = _metaPool.Count > 0 ? _metaPool.Pop() : null;
            batch ??= new MetaBatch { owner = this };
            // Hand the filled buffer over and continue with the batch's empty one.
            (batch.buffer, _meta) = (_meta, batch.buffer);
            _meta.Reserve(_logCapacity / 8);
            _file.Enqueue(RecordKind.Batch, batch, true);
        }

        // ---- Names ------------------------------------------------------------------------------------

        string Name(UnityEngine.Object o)
        {
            long id = RecordingObjectId.Of(o);
            if (_names.TryGetValue(id, out var name))
                return name;
            if (_names.Count >= MaxCachedNames)
                _names.Clear();
            return _names[id] = o.name;
        }

        readonly List<string> _pathParts = new List<string>(16);

        /// <summary>Writes "root/child/object" as a JSON string from the cached names, without building it.</summary>
        void WritePath(JsonBuilder json, Transform t)
        {
            for (var p = t; p; p = p.parent)
                _pathParts.Add(Name(p.gameObject));
            json.BeginString();
            for (int i = _pathParts.Count - 1; i >= 0; i--)
            {
                json.Part(_pathParts[i]);
                if (i > 0)
                    json.Part("/");
            }
            json.EndString();
            _pathParts.Clear();
        }

        string PrefabName(int prefabId)
        {
            if (_prefabNames.TryGetValue(prefabId, out var name))
                return name;
            name = _predictionManager.TryGetPrefab(prefabId, out var prefab) && prefab ? prefab.name : null;
            return _prefabNames[prefabId] = name;
        }

        string TypeName(Type type)
        {
            if (!_typeNames.TryGetValue(type, out var name))
                _typeNames[type] = name = RecordingTypeRegistry.TypeName(type);
            return name;
        }

        // ---- Colliders --------------------------------------------------------------------------------

        const ulong ColliderCheckTicks = 10;
        const ulong StaticColliderCheckTicks = 60;
        readonly RecordingBuffer _staticColliderKey = new RecordingBuffer(256);
        bool _hasStaticColliderKey;
        readonly RecordingBuffer _colliderKey = new RecordingBuffer(256);
        readonly List<Collider> _colliderList = new List<Collider>();
        readonly List<Collider> _colliderScratch = new List<Collider>();
        readonly List<GameObject> _sceneRoots = new List<GameObject>();
        readonly List<Renderer> _rendererList = new List<Renderer>();

        void RefreshStaticColliders(ulong tick)
        {
            // Every enabled collider of the loaded scenes that no predicted object owns.
            _colliderList.Clear();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                scene.GetRootGameObjects(_sceneRoots);
                foreach (var root in _sceneRoots)
                {
                    if (!root.activeInHierarchy)
                        continue;
                    root.GetComponentsInChildren(false, _colliderScratch);
                    foreach (var collider in _colliderScratch)
                        if (collider.enabled && !IsLocalPhysics(collider) && !collider.GetComponentInParent<PredictedIdentity>(true))
                            _colliderList.Add(collider);
                }
            }
            _sceneRoots.Clear();
            _colliderScratch.Clear();

            RefreshColliderSet(_colliderList, null, Matrix4x4.identity, _staticColliderKey, ref _hasStaticColliderKey,
                _staticColliders, ref _staticMovingColliders, -1, tick);
            _colliderList.Clear();
        }

        readonly List<TrackedCollider> _staticColliders = new List<TrackedCollider>();
        int _staticMovingColliders;

        // A map can have thousands of static colliders, too many to check in one tick without a hitch, so the periodic
        // check is spread over about StaticCheckSpreadTicks ticks. The scene is walked depth first a slice at a time (in
        // the order GetComponentsInChildren uses) and each collider's shape key is compared with the last written set
        // as it is found; when nothing changed, the poses are then checked a slice at a time.
        const int StaticCheckSpreadTicks = 45;
        const int StaticCheckMinimumWork = 32;
        enum StaticCheck { Idle, Scan, Poses }

        StaticCheck _staticCheck;
        readonly List<Transform> _staticStack = new List<Transform>();
        readonly List<Collider> _staticFound = new List<Collider>();
        readonly List<Collider> _nodeColliders = new List<Collider>();
        // Everything the last scan and the current one touched (transforms, objects, colliders), so Unity's C# objects
        // for them stay alive from one scan to the next instead of being made again (see Entity.foundColliders).
        List<UnityEngine.Object> _scanned = new List<UnityEngine.Object>(256), _scanning = new List<UnityEngine.Object>(256);
        int _staticNext;
        int _staticBudget;
        int _staticKeyMatched;
        bool _staticChanged;
        int _staticWork;
        int _lastStaticWork;

        void BeginStaticCheck()
        {
            _staticStack.Clear();
            _staticFound.Clear();
            // A stack: pushed last to first, so the first root of the first scene is walked first.
            for (int s = SceneManager.sceneCount - 1; s >= 0; s--)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;
                scene.GetRootGameObjects(_sceneRoots);
                for (int r = _sceneRoots.Count - 1; r >= 0; r--)
                    _staticStack.Add(_sceneRoots[r].transform);
            }
            _sceneRoots.Clear();

            _staticCheck = StaticCheck.Scan;
            _staticKeyMatched = 0;
            _staticChanged = false;
            _staticWork = 0;
            // The last check's work (objects and colliders walked, then poses), spread over the ticks.
            _staticBudget = Math.Max(256, (_lastStaticWork + _staticColliders.Count) / StaticCheckSpreadTicks + 1);
        }

        void ContinueStaticCheck(ulong tick)
        {
            // The work budget spreads a check over about StaticCheckSpreadTicks ticks; the time budget (shared with the
            // entity checks) may stretch it further. Some work is always done, so a check always finishes.
            int budget = _staticBudget;
            int minimum = budget - StaticCheckMinimumWork;
            if (_staticCheck == StaticCheck.Scan)
            {
                var key = _colliderKey;
                int walked = 0;
                while (budget > 0 && _staticStack.Count > 0 && (budget > minimum || (++walked & 7) != 0 || !OverBudget))
                {
                    var t = _staticStack[_staticStack.Count - 1];
                    _staticStack.RemoveAt(_staticStack.Count - 1);
                    budget--;
                    _staticWork++;
                    // Destroyed since the check began; whatever it held is compared at the end.
                    if (!t)
                        continue;
                    var go = t.gameObject;
                    _scanning.Add(t);
                    _scanning.Add(go);
                    // Inactive objects and everything a predicted object owns are not static colliders.
                    if (!go.activeSelf || go.TryGetComponent<PredictedIdentity>(out _))
                        continue;

                    go.GetComponents(_nodeColliders);
                    foreach (var collider in _nodeColliders)
                    {
                        _scanning.Add(collider);
                        budget--;
                        _staticWork++;
                        if (!collider.enabled || IsLocalPhysics(collider))
                            continue;
                        _staticFound.Add(collider);
                        if (_staticChanged)
                            continue;
                        key.Clear();
                        ShapeKey(key, collider);
                        if (_staticKeyMatched + key.length > _staticColliderKey.length ||
                            !_staticColliderKey.SameBytes(_staticKeyMatched, key.length, key.data, 0, key.length))
                            _staticChanged = true;
                        _staticKeyMatched += key.length;
                    }
                    _nodeColliders.Clear();

                    for (int c = t.childCount - 1; c >= 0; c--)
                        _staticStack.Add(t.GetChild(c));
                }
                if (_staticStack.Count > 0)
                    return;

                (_scanned, _scanning) = (_scanning, _scanned);
                _scanning.Clear();
                _lastStaticWork = _staticWork;
                bool changed = _hasStaticColliderKey
                    ? _staticChanged || _staticKeyMatched != _staticColliderKey.length
                    : _staticFound.Count > 0;
                if (changed)
                {
                    // Rare (the map changed): the whole set is compared and written now.
                    for (int i = _staticFound.Count - 1; i >= 0; i--)
                        if (!_staticFound[i])
                            _staticFound.RemoveAt(i);
                    RefreshColliderSet(_staticFound, null, Matrix4x4.identity, _staticColliderKey, ref _hasStaticColliderKey,
                        _staticColliders, ref _staticMovingColliders, -1, tick);
                    _staticFound.Clear();
                    _staticCheck = StaticCheck.Idle;
                    return;
                }
                _staticFound.Clear();
                _staticCheck = StaticCheck.Poses;
                _staticNext = 0;
                return;
            }

            int end = Math.Min(_staticColliders.Count, _staticNext + budget);
            int to = Math.Min(end, _staticNext + StaticCheckMinimumWork);
            CheckColliderPoses(_staticColliders, _staticNext, to, Matrix4x4.identity, ref _staticMovingColliders, -1);
            while (to < end && !OverBudget)
            {
                int from = to;
                to = Math.Min(end, to + 16);
                CheckColliderPoses(_staticColliders, from, to, Matrix4x4.identity, ref _staticMovingColliders, -1);
            }
            _staticNext = to;
            if (_staticNext >= _staticColliders.Count)
                _staticCheck = StaticCheck.Idle;
        }

        void ResetStaticCheck()
        {
            _staticCheck = StaticCheck.Idle;
            _staticStack.Clear();
            _staticFound.Clear();
            _scanned.Clear();
            _scanning.Clear();
            _lastStaticWork = 0;
            _entityCheckCursor = 0;
            _entityChecksOwed = 0;
        }

        readonly List<TrackedCollider> _trackedScratch = new List<TrackedCollider>();
        readonly Dictionary<long, TrackedCollider> _trackedById = new Dictionary<long, TrackedCollider>();
        readonly Stack<TrackedCollider> _trackedPool = new Stack<TrackedCollider>(256);
        const int StillChecksBeforeIdle = 2;

        /// <summary>
        /// Writes a COLLIDERS record when the set's shapes change (compared through a compact binary key, so an
        /// unchanged set costs no strings), and otherwise checks each collider's pose: a collider that moved relative
        /// to its entity gets a pose stream. An empty set that was never written does not count as a change.
        /// </summary>
        void RefreshColliderSet(List<Collider> colliders, Transform space, Matrix4x4 worldToSpace, RecordingBuffer lastKey,
            ref bool hasKey, List<TrackedCollider> tracked, ref int moving, int entity, ulong tick)
        {
            var key = _colliderKey;
            key.Clear();
            foreach (var collider in colliders)
                ShapeKey(key, collider);
            bool changed = !hasKey ? key.length != 0 : !key.SameBytes(lastKey);

            if (changed)
            {
                lastKey.CopyFrom(key);
                hasKey = true;

                // Keep the trackers (and pose streams) of colliders that stay in the set.
                _trackedScratch.Clear();
                _trackedById.Clear();
                foreach (var t in tracked)
                    _trackedById[t.id] = t;
                foreach (var collider in colliders)
                {
                    long id = RecordingObjectId.Of(collider);
                    if (_trackedById.TryGetValue(id, out var t) && ReferenceEquals(t.collider, collider))
                        _trackedById.Remove(id);
                    else
                        t = null;
                    if (t == null)
                    {
                        t = _trackedPool.Count > 0 ? _trackedPool.Pop() : new TrackedCollider();
                        t.collider = collider;
                        t.id = RecordingObjectId.Of(collider);
                        t.stream = null;
                        t.moving = false;
                        t.stillChecks = 0;
                    }
                    // World bounds (terrain and the like) have no pose of their own to follow, and a collider on the
                    // entity's own object is always at the entity's pose.
                    t.streamable = HasShapePose(collider) && !ReferenceEquals(collider.transform, space);
                    RoundedPose(collider, worldToSpace, out t.position, out t.rotation, out t.scale);
                    _trackedScratch.Add(t);
                }
                // What is left in the map is gone from the set.
                tracked.Clear();
                foreach (var pair in _trackedById)
                    tracked.Add(pair.Value);
                _trackedById.Clear();
                ReleaseColliders(tracked);
                tracked.AddRange(_trackedScratch);
                _trackedScratch.Clear();
                moving = 0;
                foreach (var t in tracked)
                    if (t.moving)
                        moving++;

                var json = _json.Clear().BeginObject().Key("entity");
                if (entity >= 0) json.Value(entity); else json.Null();
                json.Prop("tick", tick).Key("colliders").BeginArray();
                foreach (var collider in colliders)
                    WriteCollider(json, collider, worldToSpace);
                json.EndArray().EndObject();
                WriteMeta(RecordKind.Colliders, json);
                return;
            }

            CheckColliderPoses(tracked, 0, tracked.Count, worldToSpace, ref moving, entity);
        }

        /// <summary>Checks the poses of <c>tracked[from..to)</c>: a collider that moved gets a pose stream sampled every tick.</summary>
        void CheckColliderPoses(List<TrackedCollider> tracked, int from, int to, Matrix4x4 worldToSpace, ref int moving,
            int entity)
        {
            for (int i = from; i < to; i++)
            {
                var t = tracked[i];
                if (!t.streamable || !t.collider)
                    continue;
                RoundedPose(t.collider, worldToSpace, out var position, out var rotation, out var scale);
                // Exact comparison (Unity's == on vectors and quaternions is approximate).
                bool moved = !position.Equals(t.position) || !rotation.Equals(t.rotation) || !scale.Equals(t.scale);
                t.position = position;
                t.rotation = rotation;
                t.scale = scale;
                if (t.moving)
                {
                    t.stillChecks = moved ? 0 : t.stillChecks + 1;
                    if (t.stillChecks >= StillChecksBeforeIdle)
                    {
                        // Its stream keeps the last pose; sampling resumes when it moves again.
                        t.moving = false;
                        moving--;
                    }
                }
                else if (moved)
                {
                    t.moving = true;
                    t.stillChecks = 0;
                    moving++;
                    t.stream ??= CreateStream(typeof(RecordedColliderPose),
                        new StreamInfo { kind = StreamKind.Collider, entity = entity, collider = t.id }, true);
                }
            }
        }

        unsafe void SampleMovingColliders(List<TrackedCollider> tracked, Matrix4x4 worldToSpace, ulong tick)
        {
            foreach (var t in tracked)
            {
                if (!t.moving || !t.collider)
                    continue;
                var local = worldToSpace * t.collider.transform.localToWorldMatrix;
                var pose = new RecordedColliderPose
                {
                    position = local.GetColumn(3),
                    rotation = local.ValidTRS() ? local.rotation : Quaternion.identity,
                    scale = local.lossyScale
                };
                AppendRow(t.stream, tick, ref pose);
            }
        }

        void ReleaseColliders(List<TrackedCollider> tracked)
        {
            foreach (var t in tracked)
            {
                if (t.stream != null)
                    t.stream.retired = true;
                t.stream = null;
                t.collider = null;
                t.moving = false;
                _trackedPool.Push(t);
            }
            tracked.Clear();
        }

        static void RoundedPose(Collider collider, Matrix4x4 worldToSpace, out Vector3 position, out Quaternion rotation,
            out Vector3 scale)
        {
            var pose = worldToSpace * collider.transform.localToWorldMatrix;
            Vector3 p = pose.GetColumn(3);
            var r = pose.ValidTRS() ? pose.rotation : Quaternion.identity;
            var s = pose.lossyScale;
            position = new Vector3(Round(p.x), Round(p.y), Round(p.z));
            rotation = new Quaternion(Round(r.x), Round(r.y), Round(r.z), Round(r.w));
            scale = new Vector3(Round(s.x), Round(s.y), Round(s.z));
        }

        /// <summary>
        /// Colliders on a dynamic rigidbody that no predicted object drives (debris, ragdoll bits) are simulated by each
        /// machine on its own: not part of the game's simulation, so they are treated as visual effects and not recorded.
        /// </summary>
        static bool IsLocalPhysics(Collider collider)
        {
            var body = collider.attachedRigidbody;
            return body && !body.isKinematic && !body.TryGetComponent<PredictedIdentity>(out _);
        }

        /// <summary>False for colliders recorded as world bounds, which carry no pose of their own.</summary>
        static bool HasShapePose(Collider collider) =>
            collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider ||
            collider is CharacterController || collider is MeshCollider;

        /// <summary>Everything a COLLIDERS record holds about a collider except its pose (that is tracked apart).</summary>
        static void ShapeKey(RecordingBuffer key, Collider collider)
        {
            key.WriteVarInt(RecordingObjectId.Of(collider));
            key.WriteByte(collider.isTrigger ? (byte)1 : (byte)0);
            key.WriteVarInt(collider.gameObject.layer);
            switch (collider)
            {
                case BoxCollider box:
                    key.WriteByte(1); KeyVector(key, box.center); KeyVector(key, box.size);
                    break;
                case SphereCollider sphere:
                    key.WriteByte(2); KeyVector(key, sphere.center); key.WriteFloat(sphere.radius);
                    break;
                case CapsuleCollider capsule:
                    key.WriteByte(3); KeyVector(key, capsule.center); key.WriteFloat(capsule.radius);
                    key.WriteFloat(capsule.height); key.WriteVarInt(capsule.direction);
                    break;
                case CharacterController controller:
                    key.WriteByte(4); KeyVector(key, controller.center); key.WriteFloat(controller.radius);
                    key.WriteFloat(controller.height);
                    break;
                case MeshCollider meshCollider:
                {
                    var mesh = meshCollider.sharedMesh;
                    key.WriteByte(5); key.WriteVarInt(mesh ? RecordingObjectId.Of(mesh) : 0);
                    key.WriteByte(meshCollider.convex ? (byte)1 : (byte)0);
                    break;
                }
                default:
                {
                    // Written as world bounds, so the bounds are its shape.
                    var b = collider.bounds;
                    key.WriteByte(6); KeyVector(key, b.center); KeyVector(key, b.size);
                    break;
                }
            }
        }

        static void KeyVector(RecordingBuffer key, Vector3 v)
        {
            key.WriteFloat(v.x);
            key.WriteFloat(v.y);
            key.WriteFloat(v.z);
        }

        /// <summary>
        /// 1 when any mesh, skinned or sprite renderer of the entity is enabled on an active object, 0 when it has such
        /// renderers but all are hidden (a bullet waiting for its hit effect to end), 2 when it has none.
        /// Particles, trails and lines do not count: they are effects, not the object.
        /// </summary>
        static bool IsBodyRenderer(Renderer renderer) =>
            renderer is MeshRenderer || renderer is SkinnedMeshRenderer || renderer is SpriteRenderer;

        static byte Visibility(Entity entity)
        {
            var renderers = entity.renderers;
            if (renderers.Count == 0)
                return 2;
            var objects = entity.rendererObjects;
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r && r.enabled && objects[i].activeInHierarchy)
                    return 1;
            }
            return 0;
        }


        static bool OwnedBy(UnityEngine.Component component, GameObject root)
        {
            var owner = component.GetComponentInParent<PredictedIdentity>(true);
            return owner && owner.gameObject == root;
        }

        void RefreshEntityColliders(Entity entity, ulong tick)
        {
            var root = entity.gameObject;
            if (!root)
                return;

            // Renderers and colliders under a nested predicted object belong to that object's entity. Without nested
            // objects (the usual case) everything under the root is the entity's, and no owner has to be looked up.
            root.GetComponentsInChildren(true, entity.foundIdentities);
            bool nested = false;
            foreach (var identity in entity.foundIdentities)
            {
                if (!ReferenceEquals(identity.gameObject, root))
                {
                    nested = true;
                    break;
                }
            }

            // The renderer list changes with the hierarchy, so it is refreshed on the collider cadence.
            entity.renderers.Clear();
            entity.rendererObjects.Clear();
            root.GetComponentsInChildren(true, entity.foundRenderers);
            foreach (var renderer in entity.foundRenderers)
            {
                if (!IsBodyRenderer(renderer))
                    continue;
                if (!nested || OwnedBy(renderer, root))
                {
                    entity.renderers.Add(renderer);
                    entity.rendererObjects.Add(renderer.gameObject);
                }
            }

            // PredictedTransform can unparent its graphics; they still show this entity.
            root.GetComponents(entity.foundTransforms);
            entity.foundGraphics.Clear();
            foreach (var predictedTransform in entity.foundTransforms)
            {
                var graphics = predictedTransform.graphics;
                if (!graphics || graphics.IsChildOf(root.transform))
                    continue;
                graphics.GetComponentsInChildren(true, _rendererList);
                entity.foundGraphics.AddRange(_rendererList);
                foreach (var renderer in _rendererList)
                {
                    if (IsBodyRenderer(renderer))
                    {
                        entity.renderers.Add(renderer);
                        entity.rendererObjects.Add(renderer.gameObject);
                    }
                }
            }
            _rendererList.Clear();

            root.GetComponentsInChildren(false, entity.foundColliders);
            _colliderList.Clear();
            foreach (var collider in entity.foundColliders)
            {
                if (collider.enabled && !IsLocalPhysics(collider) && (!nested || OwnedBy(collider, root)))
                    _colliderList.Add(collider);
            }

            RefreshColliderSet(_colliderList, entity.root, entity.root.worldToLocalMatrix, entity.colliderKey,
                ref entity.hasColliderKey, entity.colliders, ref entity.movingColliders, entity.id, tick);
            _colliderList.Clear();
        }

        void WriteCollider(JsonBuilder json, Collider collider, Matrix4x4 worldToSpace)
        {
            var local = worldToSpace * collider.transform.localToWorldMatrix;
            switch (collider)
            {
                case BoxCollider box:
                    BeginCollider(json, "box", collider, local);
                    json.Floats("center", box.center.x, box.center.y, box.center.z)
                        .Floats("size", box.size.x, box.size.y, box.size.z);
                    break;
                case SphereCollider sphere:
                    BeginCollider(json, "sphere", collider, local);
                    json.Floats("center", sphere.center.x, sphere.center.y, sphere.center.z).Prop("radius", sphere.radius);
                    break;
                case CapsuleCollider capsule:
                    BeginCollider(json, "capsule", collider, local);
                    json.Floats("center", capsule.center.x, capsule.center.y, capsule.center.z)
                        .Prop("radius", capsule.radius).Prop("height", capsule.height).Prop("direction", capsule.direction);
                    break;
                case CharacterController controller:
                    BeginCollider(json, "capsule", collider, local);
                    json.Floats("center", controller.center.x, controller.center.y, controller.center.z)
                        .Prop("radius", controller.radius).Prop("height", controller.height).Prop("direction", 1);
                    break;
                case MeshCollider meshCollider:
                {
                    var mesh = meshCollider.sharedMesh;
                    if (!mesh)
                        return;
                    int meshId = WriteMesh(mesh);
                    if (meshId > 0)
                    {
                        BeginCollider(json, "mesh", collider, local);
                        json.Prop("mesh", meshId).Prop("convex", meshCollider.convex);
                    }
                    else
                    {
                        var b = mesh.bounds;
                        BeginCollider(json, "bounds", collider, local);
                        json.Floats("center", b.center.x, b.center.y, b.center.z).Floats("size", b.size.x, b.size.y, b.size.z);
                    }
                    break;
                }
                default:
                {
                    // Terrain, wheel and other colliders: world bounds as a box.
                    var b = collider.bounds;
                    BeginCollider(json, "bounds", collider, worldToSpace);
                    json.Floats("center", b.center.x, b.center.y, b.center.z).Floats("size", b.size.x, b.size.y, b.size.z);
                    break;
                }
            }

            json.EndObject();
        }

        void BeginCollider(JsonBuilder json, string shape, Collider collider, Matrix4x4 pose)
        {
            Vector3 p = pose.GetColumn(3);
            var r = pose.ValidTRS() ? pose.rotation : Quaternion.identity;
            var s = pose.lossyScale;
            // Poses come out of a world-to-local multiply with ~1e-7 noise; rounding to 0.1 mm keeps an unchanged
            // set byte-identical so it is not re-written every check.
            json.BeginObject().Prop("shape", shape).Prop("id", RecordingObjectId.Of(collider))
                .Key("pose").BeginObject()
                .Floats("p", Round(p.x), Round(p.y), Round(p.z)).Floats("r", Round(r.x), Round(r.y), Round(r.z), Round(r.w))
                .Floats("s", Round(s.x), Round(s.y), Round(s.z))
                .EndObject()
                .Prop("trigger", collider.isTrigger).Prop("layer", collider.gameObject.layer)
                .Prop("name", Name(collider));
        }

        static float Round(float value) => (float)Math.Round(value, 4) + 0f;

        readonly List<Vector3> _meshVertices = new List<Vector3>();
        readonly List<int> _meshTriangles = new List<int>();
        readonly List<int> _meshIndices = new List<int>();
        readonly RecordingBuffer _meshBuffer = new RecordingBuffer(1024);

        static int VarLength(ulong value)
        {
            int length = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                length++;
            }
            return length;
        }

        /// <summary>
        /// Writes a triangle mesh straight from Unity's native mesh data into the metadata batch: a map's collision
        /// mesh can have hundreds of thousands of vertices, and no managed copy of them is made.
        /// </summary>
        unsafe bool WriteMeshNative(Mesh mesh, int id)
        {
            using var meshes = Mesh.AcquireReadOnlyMeshData(mesh);
            var data = meshes[0];
            int indexCount = 0;
            for (int sub = 0; sub < data.subMeshCount; sub++)
            {
                var desc = data.GetSubMesh(sub);
                if (desc.topology != MeshTopology.Triangles)
                    return false;
                indexCount += desc.indexCount;
            }

            int vertexCount = data.vertexCount;
            using var vertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            using var indices = new NativeArray<int>(indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            data.GetVertices(vertices);
            for (int sub = 0, at = 0; sub < data.subMeshCount; sub++)
            {
                int count = data.GetSubMesh(sub).indexCount;
                data.GetIndices(indices.GetSubArray(at, count), sub);
                at += count;
            }

            // Same bytes as the managed path: little-endian floats and 32-bit indices.
            string name = mesh.name;
            int nameBytes = System.Text.Encoding.UTF8.GetByteCount(name);
            int length = VarLength((ulong)id) + VarLength((ulong)nameBytes) + nameBytes +
                         VarLength((ulong)vertexCount) + vertexCount * 12 + VarLength((ulong)indexCount) + indexCount * 4;
            _meta.WriteByte((byte)RecordKind.Mesh);
            _meta.WriteVarUInt((ulong)length);
            _meta.WriteVarUInt((ulong)id);
            _meta.WriteString(name);
            _meta.WriteVarUInt((ulong)vertexCount);
            _meta.WriteBytes((byte*)vertices.GetUnsafeReadOnlyPtr(), vertexCount * 12);
            _meta.WriteVarUInt((ulong)indexCount);
            _meta.WriteBytes((byte*)indices.GetUnsafeReadOnlyPtr(), indexCount * 4);
            return true;
        }

        int WriteMesh(Mesh mesh)
        {
            long key = RecordingObjectId.Of(mesh);
            if (_meshIds.TryGetValue(key, out int existing))
                return existing;

            if (!mesh.isReadable && !Application.isEditor)
            {
                _meshIds[key] = 0;
                return 0;
            }

            int id = _nextMeshId++;
            _meshIds[key] = id;
            try
            {
                bool written;
                try
                {
                    // Everything is read before anything is written, so a failure leaves the batch as it was.
                    written = WriteMeshNative(mesh, id);
                }
                catch (Exception)
                {
                    written = false;
                }
                if (written)
                    return id;

                // Other topologies (quads, say): Unity converts them to triangles. Once per mesh per recording; the
                // vertex and index lists are reused.
                mesh.GetVertices(_meshVertices);
                _meshIndices.Clear();
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    mesh.GetTriangles(_meshTriangles, sub);
                    _meshIndices.AddRange(_meshTriangles);
                }
                var buffer = _meshBuffer;
                buffer.Clear();
                buffer.WriteVarUInt((ulong)id);
                buffer.WriteString(mesh.name);
                buffer.WriteVarUInt((ulong)_meshVertices.Count);
                foreach (var v in _meshVertices)
                {
                    buffer.WriteFloat(v.x);
                    buffer.WriteFloat(v.y);
                    buffer.WriteFloat(v.z);
                }
                buffer.WriteVarUInt((ulong)_meshIndices.Count);
                buffer.Ensure(_meshIndices.Count * 4);
                foreach (var i in _meshIndices)
                {
                    buffer.data[buffer.length++] = (byte)i;
                    buffer.data[buffer.length++] = (byte)(i >> 8);
                    buffer.data[buffer.length++] = (byte)(i >> 16);
                    buffer.data[buffer.length++] = (byte)(i >> 24);
                }
                WriteMeta(RecordKind.Mesh, buffer.data, buffer.length);
                return id;
            }
            catch (Exception)
            {
                _meshIds[key] = 0;
                return 0;
            }
        }
    }
}
