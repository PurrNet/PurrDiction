using System;
using System.Collections.Generic;
using PurrNet.Prediction.StateMachine;
using PurrNet.Recording;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Prediction.Recording
{
    /// <summary>
    /// PurrDiction's part of a match recording (see PurrNet's recording/FORMAT.md): every predicted component's state
    /// and input per tick, the predicted players, and the match summary. PurrNet's <see cref="NetworkRecorder"/>
    /// carries one automatically; there is no component to add. While a <see cref="PredictionManager"/> is spawned,
    /// recordings run on its ticks: the row for tick T is the state before T was simulated and the input applied
    /// during T.
    /// </summary>
    public sealed class PredictionRecording : RecorderExtension, IPredictedDataVisitor, IRecordingContext
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register() => NetworkRecorder.RegisterExtension<PredictionRecording>();

        /// <summary>What the recorder keeps about an entity's predicted object.</summary>
        sealed class EntityData
        {
            public PredictedObjectID objectId;
            /// <summary>Tick the entity's pose was last recorded for (once per tick, whatever its component count).</summary>
            public ulong poseTick;
            public readonly List<Component> components = new List<Component>(4);
            // Kept between collider checks so Unity's C# objects for them are not made again (see RecordedEntity).
            public readonly List<PredictedIdentity> foundIdentities = new List<PredictedIdentity>(4);
            public readonly List<PredictedTransform> foundTransforms = new List<PredictedTransform>(2);
        }

        /// <summary>What an identity index resolved to last tick.</summary>
        struct IdentitySlot
        {
            public PredictedIdentity identity;
            public RecordedEntity entity;
            public EntityData data;
            public int entityId;
            public Component component;
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

        /// <summary>Tells the session which scene objects belong to predicted objects.</summary>
        sealed class Source : RecordingSource
        {
            readonly PredictionRecording _owner;

            readonly List<PredictedIdentity> _scratch = new List<PredictedIdentity>();

            public Source(PredictionRecording owner) => _owner = owner;

            public override string name => "purrdiction";

            public override bool IsEntityObject(GameObject go) => go.TryGetComponent<PredictedIdentity>(out _);

            public override Transform NearestEntity(Transform t, bool includeInactive)
            {
                var identity = t.GetComponentInParent<PredictedIdentity>(includeInactive);
                return identity ? identity.transform : null;
            }

            public override bool HasNestedEntities(RecordedEntity entity)
            {
                // Entities another source created have no list of ours to keep the found identities in.
                var list = entity.DataOf(this) is EntityData data ? data.foundIdentities : _scratch;
                var root = entity.gameObject;
                root.GetComponentsInChildren(true, list);
                bool nested = false;
                foreach (var identity in list)
                {
                    if (!ReferenceEquals(identity.gameObject, root))
                    {
                        nested = true;
                        break;
                    }
                }
                _scratch.Clear();
                return nested;
            }

            // PredictedTransform can unparent its graphics; they still show this entity.
            public override void GetDetachedGraphics(RecordedEntity entity, List<Transform> graphics)
            {
                var data = (EntityData)entity.DataOf(this);
                entity.gameObject.GetComponents(data.foundTransforms);
                foreach (var predictedTransform in data.foundTransforms)
                    graphics.Add(predictedTransform.graphics);
            }

            public override void OnEntityReleased(RecordedEntity entity, object data) => _owner.ReleaseData((EntityData)data);

            public override void RecordTick(ulong tick) => _owner.RecordTick(tick);

#if UNITY_PHYSICS_3D
            public override bool DrivesBody(Rigidbody body) => body.TryGetComponent<PredictedIdentity>(out _);
#endif
        }

        // The generic transform duplicates PredictedTransform's pose; store it as a difference against that pose.
        static readonly (string, string)[] PoseColumns =
        {
            ("position.x", "unityPosition.x"), ("position.y", "unityPosition.y"), ("position.z", "unityPosition.z"),
            ("rotation.x", "unityRotation.x"), ("rotation.y", "unityRotation.y"), ("rotation.z", "unityRotation.z"),
            ("rotation.w", "unityRotation.w")
        };

        RecordingSession _session;
        Source _source;
        PredictionManager _predictionManager;
        PredictionManager _subscribed;
        readonly Dictionary<ulong, Player> _players = new Dictionary<ulong, Player>();
        readonly List<ulong> _playersLeft = new List<ulong>();
        readonly List<PredictedIdentity> _identityList = new List<PredictedIdentity>();
        IdentitySlot[] _identitySlots = Array.Empty<IdentitySlot>();
        readonly Stack<EntityData> _dataPool = new Stack<EntityData>(256);
        readonly Stack<Component> _componentPool = new Stack<Component>(256);
        readonly Dictionary<int, string> _prefabNames = new Dictionary<int, string>();

        RecordingSummary _summary;
        RecorderStats.Section _statPlayers, _statIdentities;
        bool _recording;

        // Visitor context for the component being captured.
        Component _visitingComponent;
        RecordedEntity _visitingEntity;
        ulong _visitTick;
        bool _declareOnly;

        /// <summary>The manager being recorded: the one of the recorder's scene, else of any loaded scene.</summary>
        public PredictionManager predictionManager
        {
            get => _predictionManager;
            set => _predictionManager = value;
        }

        public override RecordingSource source => _source ??= new Source(this);

        bool active => _predictionManager && _predictionManager.isSpawned;

        /// <summary>While a manager is spawned, recordings run on prediction ticks.</summary>
        public override IRecordingContext clock => active ? this : null;

        /// <summary>A manager that is still spawning delays the recording, so the file starts on its clock.</summary>
        public override bool isReady => !_predictionManager || _predictionManager.isSpawned;

        public override bool recordsPlayers => active;

        bool IRecordingContext.isReady => _predictionManager;
        ulong IRecordingContext.localTick => _predictionManager.localTick;
        double IRecordingContext.viewTick => _predictionManager.viewTick;
        bool IRecordingContext.isServer => _predictionManager.isServer;
        bool IRecordingContext.isClient => _predictionManager.isClient;
        int IRecordingContext.tickRate => _predictionManager.tickRate;
        PlayerID? IRecordingContext.localPlayer => _predictionManager.localPlayer;

        public override void OnCreated()
        {
            _session = session;
            _summary = new RecordingSummary(_session.TypeName);
            _statPlayers = _session.Section("players");
            _statIdentities = _session.stats.Add("predicted");
            FindManager();
        }

        public override void OnUpdate()
        {
            if (!_predictionManager)
                FindManager();
        }

        void FindManager()
        {
            if (PredictionManager.TryGetInstance(recorder.gameObject.scene.handle, out _predictionManager) && _predictionManager)
                return;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (PredictionManager.TryGetInstance(SceneManager.GetSceneAt(i).handle, out _predictionManager) && _predictionManager)
                    return;
            _predictionManager = null;
        }

        public override void OnRecordingStarted()
        {
            Array.Clear(_identitySlots, 0, _identitySlots.Length);
            _players.Clear();
            _prefabNames.Clear();
            _recording = active;
            if (!_recording)
                return;
            _summary.Begin(_predictionManager.tickRate);
            _subscribed = _predictionManager;
            _subscribed.onServerTickReady += OnServerTickReady;
        }

        public override void OnRecordingStopping()
        {
            if (_subscribed)
                _subscribed.onServerTickReady -= OnServerTickReady;
            _subscribed = null;
        }

        public override void WriteFooter(JsonBuilder json)
        {
            if (_recording)
                _summary.Write(json, _predictionManager ? _predictionManager.tickRate : 0);
            _recording = false;
        }

        public override void OnDestroyed() => OnRecordingStopping();

        // ---- Ticks ------------------------------------------------------------------------------------

        // The recording runs on prediction ticks: the recorder records everything else right after our rows.
        void OnServerTickReady(ulong tick) => recorder.RecordTick(tick);

        void RecordTick(ulong tick)
        {
            if (!_recording || !_predictionManager)
                return;
            var stats = _session.stats;
            stats.Begin(_statPlayers);
            RecordPlayers(tick);
            stats.End(_statPlayers);
            RecordIdentities(tick);
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
                        player.rows = _session.CreateStream(typeof(RecordedPlayerTick),
                            new RecordedStreamInfo { kind = RecordedStreamKind.Player, player = id.id.value, hasPlayer = true }, true);
                        _players[id.id.value] = player;
                        _session.FlushPendingTypes();
                        _session.WriteMeta(RecordKind.Player, _session.json.Clear().BeginObject()
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
                    _session.AppendRow(player.rows, tick, ref row);
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
                    gone.rows.Retire();
                _players.Remove(id);
                _session.WriteMeta(RecordKind.Player, _session.json.Clear().BeginObject()
                    .Prop("player", id).Prop("tick", tick).Prop("event", "left").EndObject());
            }
        }

        void RecordIdentities(ulong tick)
        {
            var stats = _session.stats;
            stats.Begin(_statIdentities);
            int count = _predictionManager.identityCount;
            _visitTick = tick;
            bool sample = _summary.IsSampleTick(tick);

            if (_identitySlots.Length < count)
                Array.Resize(ref _identitySlots, Math.Max(count, _identitySlots.Length * 2));
            var own = source;

            for (int i = 0; i < count; i++)
            {
                var identity = _predictionManager.GetIdentityAt(i);
                if (!identity)
                    continue;

                // Identities keep their index from tick to tick, so the entity found last tick is checked first.
                ref var slot = ref _identitySlots[i];
                RecordedEntity entity = slot.entity;
                EntityData data = slot.data;
                Component component;
                if (ReferenceEquals(slot.identity, identity) && entity != null && entity.id == slot.entityId &&
                    !ReferenceEquals(entity.gameObject, null) && data.objectId == identity.id.objectId)
                {
                    component = slot.component;
                }
                else
                {
                    bool found = _session.TryGetEntity(identity.gameObject, out entity);
                    data = found ? entity.DataOf(source) as EntityData : null;
                    if (data == null || data.objectId != identity.id.objectId)
                    {
                        // Ours, but another predicted object now (a pooled instance reused): it starts over.
                        if (data != null)
                        {
                            _session.Release(entity, source, tick);
                            found = _session.TryGetEntity(identity.gameObject, out entity);
                        }
                        // An object another source already records gets our part attached; any other object is new.
                        entity = found ? Attach(entity, identity, tick) : Spawn(identity, tick);
                    }
                    data = (EntityData)entity.DataOf(source);
                    component = FindComponent(data, identity);
                    slot.identity = identity;
                    slot.entity = entity;
                    slot.data = data;
                    slot.entityId = entity.id;
                    slot.component = component;
                }

                if (data.poseTick != tick)
                {
                    data.poseTick = tick;
                    if (_session.RecordTransform(entity, own, tick, out var position) && entity.owner.HasValue && entity.isRoot)
                        _summary.OnOwnedEntity(entity.owner.Value, entity.spawnTick, position, tick, sample);
                }

                _visitingEntity = entity;
                _visitingComponent = component;
                identity.VisitData(this);
            }

            _visitingComponent = null;
            _visitingEntity = null;

            _session.DespawnUnseen(source, tick);
            _summary.EndTick(tick, sample);
            stats.End(_statIdentities);
        }

        Component FindComponent(EntityData data, PredictedIdentity identity)
        {
            foreach (var c in data.components)
                if (ReferenceEquals(c.identity, identity))
                    return c;
            var created = _componentPool.Count > 0 ? _componentPool.Pop() : new Component();
            created.identity = identity;
            created.state = null;
            created.input = null;
            data.components.Add(created);
            return created;
        }

        RecordedEntity Spawn(PredictedIdentity first, ulong tick)
        {
            var go = first.gameObject;
            var data = _dataPool.Count > 0 ? _dataPool.Pop() : new EntityData();
            data.objectId = first.id.objectId;
            data.poseTick = 0;
            var entity = _session.BeginSpawn(source, go, data);

            // Declare every component stream up front so the ENTITY record can list them.
            DeclareComponents(entity, data, go, tick);

            StreamRows poseBase = null;
            foreach (var c in data.components)
                if (c.identity is PredictedTransform && c.state != null)
                    poseBase = c.state;
            _session.CreateTransformStream(entity, poseBase, PoseColumns);

            var hierarchy = _predictionManager.hierarchy;
            int prefabId = -1;
            string prefabName = null;
            var owner = first.owner;
            RecordedEntity parent = null;
            if (hierarchy && hierarchy.TryGetInstanceDetails(data.objectId, out var details))
            {
                prefabId = details.prefabId;
                prefabName = PrefabName(prefabId);
            }

            parent = _session.ParentEntity(go.transform.parent, false);

            entity.spawnTick = tick;
            entity.owner = owner;
            entity.isRoot = parent == null;
            _summary.OnSpawn(prefabName, owner, parent == null, tick);
            foreach (var c in data.components)
                if (c.identity is PredictedStateMachine machine)
                    _summary.TrackMachine(machine);

            _session.FlushPendingTypes();
            var json = _session.json.Clear().BeginObject()
                .Prop("entity", entity.id).Prop("tick", tick).Prop("objectId", (ulong)data.objectId.instanceId.value)
                .Prop("name", _session.Name(go)).Prop("prefabId", prefabId).Prop("prefabName", prefabName)
                .Key("owner");
            if (owner.HasValue) json.Value(owner.Value.id.value); else json.Null();
            json.Key("parent");
            if (parent != null) json.Value(parent.id); else json.Null();
            _session.WritePath(json.Key("path"), go.transform);
            WriteComponents(json, data);
            json.Prop("transform", entity.transform.id).EndObject();
            _session.WriteMeta(RecordKind.Entity, json);

            _session.EndSpawn(entity, tick);
            return entity;
        }

        /// <summary>Adds the predicted components of an object another source already records (an ATTACH record).</summary>
        RecordedEntity Attach(RecordedEntity entity, PredictedIdentity first, ulong tick)
        {
            var data = _dataPool.Count > 0 ? _dataPool.Pop() : new EntityData();
            data.objectId = first.id.objectId;
            data.poseTick = 0;
            _session.Attach(entity, source, data);
            DeclareComponents(entity, data, first.gameObject, tick);
            foreach (var c in data.components)
                if (c.identity is PredictedStateMachine machine)
                    _summary.TrackMachine(machine);
            var json = _session.BeginAttachRecord(entity, source, tick).Prop("objectId", (ulong)data.objectId.instanceId.value);
            WriteComponents(json, data);
            _session.EndAttachRecord(json);
            return entity;
        }

        void DeclareComponents(RecordedEntity entity, EntityData data, GameObject go, ulong tick)
        {
            _visitingEntity = entity;
            _visitTick = tick;
            go.GetComponents(_identityList);
            foreach (var identity in _identityList)
            {
                if (identity.id.objectId != data.objectId)
                    continue;
                _visitingComponent = FindComponent(data, identity);
                _declareOnly = true;
                identity.VisitData(this);
                _declareOnly = false;
            }
            _identityList.Clear();
        }

        void WriteComponents(JsonBuilder json, EntityData data)
        {
            json.Key("components").BeginArray();
            foreach (var c in data.components)
            {
                json.BeginObject().Prop("index", (ulong)c.identity.id.componentId.value)
                    .Prop("type", _session.TypeName(c.identity.GetType()));
                if (c.state != null) json.Prop("state", c.state.id);
                if (c.input != null) json.Prop("input", c.input.id);
                json.EndObject();
            }
            json.EndArray();
        }

        /// <summary>Retires the components' streams (dropped after their last flush) and pools them.</summary>
        void ReleaseData(EntityData data)
        {
            if (data == null)
                return;
            foreach (var c in data.components)
            {
                if (c.state != null) c.state.Retire();
                if (c.input != null) c.input.Retire();
                c.identity = null;
                c.state = c.input = null;
                _componentPool.Push(c);
            }
            data.components.Clear();
            data.foundIdentities.Clear();
            data.foundTransforms.Clear();
            _dataPool.Push(data);
        }

        void IPredictedDataVisitor.VisitState<T>(PredictedIdentity identity, ref T state)
        {
            var component = _visitingComponent;
            if (component == null)
                return;
            component.state ??= _session.CreateStream(typeof(T), new RecordedStreamInfo
            {
                kind = RecordedStreamKind.State, entity = _visitingEntity.id,
                component = identity.id.componentId.value, componentType = identity.GetType()
            }, true);
            if (!_declareOnly)
                _session.AppendRow(component.state, _visitTick, ref state);
        }

        void IPredictedDataVisitor.VisitInput<T>(PredictedIdentity identity, ref T input)
        {
            var component = _visitingComponent;
            if (component == null)
                return;
            component.input ??= _session.CreateStream(typeof(T), new RecordedStreamInfo
            {
                kind = RecordedStreamKind.Input, entity = _visitingEntity.id,
                component = identity.id.componentId.value, componentType = identity.GetType()
            }, true);
            if (!_declareOnly)
                _session.AppendRow(component.input, _visitTick, ref input);
        }

        string PrefabName(int prefabId)
        {
            if (_prefabNames.TryGetValue(prefabId, out var name))
                return name;
            name = _predictionManager.TryGetPrefab(prefabId, out var prefab) && prefab ? prefab.name : null;
            return _prefabNames[prefabId] = name;
        }
    }
}
