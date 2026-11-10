using System;
using System.Collections.Generic;
using PurrNet.Prediction.StateMachine;
using PurrNet.Recording;
using UnityEngine;

namespace PurrNet.Prediction.Recording
{
    /// <summary>
    /// Match-level figures the recorder collects while it records and writes into the FOOTER as <c>summary</c>, so a
    /// dashboard can show how a match went without reading the whole file. Everything here is derived from what the
    /// recorder sees anyway (players, owners, spawns, state machines, positions); games add nothing.
    /// </summary>
    sealed class RecordingSummary
    {
        public const int Version = 1;

        /// <summary>Cells along the longer side of the position heatmap.</summary>
        const int HeatmapCells = 48;

        sealed class Owner
        {
            public ulong id;
            public bool isBot;
            public bool isConnected;
            public bool isLocal;
            public ulong firstTick;
            public ulong lastTick;
            public int spawns;
            public double viewOffsetSum;
            public float viewOffsetMax;
            public int viewOffsetCount;
            // The owner's oldest live root entity this sample: the one taken as its avatar.
            public ulong avatarSpawnTick;
            public Vector3 avatarPosition;
            public bool hasAvatar;
        }

        sealed class Machine
        {
            public PredictedStateMachine machine;
            public string name;
            public int lastIndex = -1;
            public long[] ticks = new long[8];
            public int[] entered = new int[8];
        }

        readonly Dictionary<ulong, Owner> _owners = new Dictionary<ulong, Owner>();
        readonly List<Owner> _ownerList = new List<Owner>();
        readonly Dictionary<string, int> _prefabSpawns = new Dictionary<string, int>();
        readonly List<Machine> _machines = new List<Machine>();
        readonly Func<Type, string> _typeName;

        Vector3[] _samples = new Vector3[1024];
        bool[] _sampleIsBot = new bool[1024];
        int _sampleCount;
        ulong _sampleEvery = 1;

        public RecordingSummary(Func<Type, string> typeName) => _typeName = typeName;

        public void Begin(double tickRate)
        {
            _owners.Clear();
            _ownerList.Clear();
            _prefabSpawns.Clear();
            _machines.Clear();
            _sampleCount = 0;
            // Positions are sampled once a second.
            _sampleEvery = (ulong)Math.Max(1, Math.Round(tickRate));
        }

        Owner GetOwner(PlayerID id, ulong tick)
        {
            if (!_owners.TryGetValue(id.id.value, out var owner))
            {
                owner = new Owner { id = id.id.value, isBot = id.isBot, firstTick = tick };
                _owners[id.id.value] = owner;
                _ownerList.Add(owner);
            }
            owner.lastTick = tick;
            return owner;
        }

        /// <summary>A connected player this tick, with how far behind the server their view was, when known.</summary>
        public void OnPlayerTick(PlayerID id, bool isLocal, ulong tick, bool hasViewOffset, float viewOffsetTicks)
        {
            var owner = GetOwner(id, tick);
            owner.isConnected = true;
            owner.isLocal |= isLocal;
            if (!hasViewOffset)
                return;
            owner.viewOffsetSum += viewOffsetTicks;
            owner.viewOffsetCount++;
            if (viewOffsetTicks > owner.viewOffsetMax)
                owner.viewOffsetMax = viewOffsetTicks;
        }

        public void OnSpawn(string prefabName, PlayerID? owner, bool isRoot, ulong tick)
        {
            if (prefabName != null)
                _prefabSpawns[prefabName] = _prefabSpawns.TryGetValue(prefabName, out var n) ? n + 1 : 1;
            if (owner.HasValue && isRoot)
                GetOwner(owner.Value, tick).spawns++;
        }

        public void TrackMachine(PredictedStateMachine machine)
        {
            foreach (var m in _machines)
                if (m.machine == machine)
                    return;
            _machines.Add(new Machine { machine = machine, name = machine.name });
        }

        public bool IsSampleTick(ulong tick) => tick % _sampleEvery == 0;

        /// <summary>An owned root entity that exists this tick. On sample ticks it may be taken as its owner's avatar.</summary>
        public void OnOwnedEntity(PlayerID id, ulong spawnTick, Vector3 position, ulong tick, bool sample)
        {
            var owner = GetOwner(id, tick);
            if (!sample)
                return;
            if (!owner.hasAvatar || spawnTick < owner.avatarSpawnTick)
            {
                owner.hasAvatar = true;
                owner.avatarSpawnTick = spawnTick;
                owner.avatarPosition = position;
            }
        }

        /// <summary>Ends a tick: counts state machine states and, on sample ticks, stores each owner's avatar position.</summary>
        public void EndTick(ulong tick, bool sample)
        {
            foreach (var m in _machines)
            {
                if (!m.machine)
                    continue;
                int index = m.machine.currentState.stateIndex;
                if (index < 0)
                    continue;
                if (index >= m.ticks.Length)
                {
                    Array.Resize(ref m.ticks, index + 8);
                    Array.Resize(ref m.entered, index + 8);
                }
                m.ticks[index]++;
                if (index != m.lastIndex)
                    m.entered[index]++;
                m.lastIndex = index;
            }

            if (!sample)
                return;
            foreach (var owner in _ownerList)
            {
                if (!owner.hasAvatar)
                    continue;
                owner.hasAvatar = false;
                if (_sampleCount == _samples.Length)
                {
                    Array.Resize(ref _samples, _samples.Length * 2);
                    Array.Resize(ref _sampleIsBot, _sampleIsBot.Length * 2);
                }
                _samples[_sampleCount] = owner.avatarPosition;
                _sampleIsBot[_sampleCount] = owner.isBot;
                _sampleCount++;
            }
        }

        public void Write(JsonBuilder json, double tickRate)
        {
            json.Key("summary").BeginObject().Prop("version", Version);

            json.Key("players").BeginArray();
            foreach (var o in _ownerList)
            {
                json.BeginObject().Prop("id", o.id).Prop("isBot", o.isBot).Prop("connected", o.isConnected)
                    .Prop("isLocal", o.isLocal).Prop("firstTick", o.firstTick).Prop("lastTick", o.lastTick)
                    .Prop("spawns", o.spawns);
                if (o.viewOffsetCount > 0)
                    json.Prop("viewOffsetAvgTicks", (float)(o.viewOffsetSum / o.viewOffsetCount))
                        .Prop("viewOffsetMaxTicks", o.viewOffsetMax);
                json.EndObject();
            }
            json.EndArray();

            json.Key("spawns").BeginObject();
            foreach (var pair in _prefabSpawns)
                json.Prop(pair.Key, pair.Value);
            json.EndObject();

            json.Key("stateMachines").BeginArray();
            foreach (var m in _machines)
            {
                json.BeginObject().Prop("name", m.name).Key("states").BeginArray();
                var states = m.machine ? m.machine.states : null;
                for (int i = 0; i < m.ticks.Length; i++)
                {
                    if (m.ticks[i] == 0)
                        continue;
                    var node = states != null && i < states.Count ? states[i] : null;
                    json.BeginObject().Prop("index", i).Prop("name", node != null ? _typeName(node.GetType()) : null)
                        .Prop("ticks", m.ticks[i]).Prop("entered", m.entered[i]).EndObject();
                }
                json.EndArray().EndObject();
            }
            json.EndArray();

            WriteHeatmap(json);
            json.EndObject();
        }

        /// <summary>
        /// Where avatars were, sampled once a second, binned on the two axes the positions spread over most
        /// (x/z for most 3D games, x/y for 2D). Cells are square; humans and bots are counted apart.
        /// </summary>
        void WriteHeatmap(JsonBuilder json)
        {
            if (_sampleCount == 0)
                return;

            Vector3 min = _samples[0], max = _samples[0];
            for (int i = 1; i < _sampleCount; i++)
            {
                min = Vector3.Min(min, _samples[i]);
                max = Vector3.Max(max, _samples[i]);
            }
            var extent = max - min;
            // Drop the axis with the smallest spread; ties keep the usual ground plane (x, z).
            int drop = extent.y <= extent.x && extent.y <= extent.z ? 1 : extent.z <= extent.x ? 2 : 0;
            int a = drop == 0 ? 1 : 0, b = drop == 2 ? 1 : 2;

            float span = Mathf.Max(extent[a], extent[b]);
            float cell = span > 0f ? span / HeatmapCells : 1f;
            int width = Mathf.Min(HeatmapCells, Mathf.FloorToInt(extent[a] / cell) + 1);
            int height = Mathf.Min(HeatmapCells, Mathf.FloorToInt(extent[b] / cell) + 1);

            var humans = new int[width * height];
            var bots = new int[width * height];
            for (int i = 0; i < _sampleCount; i++)
            {
                int x = Mathf.Min(width - 1, Mathf.FloorToInt((_samples[i][a] - min[a]) / cell));
                int y = Mathf.Min(height - 1, Mathf.FloorToInt((_samples[i][b] - min[b]) / cell));
                (_sampleIsBot[i] ? bots : humans)[y * width + x]++;
            }

            const string axes = "xyz";
            json.Key("heatmap").BeginObject()
                .Key("axes").BeginString().Part(axes.Substring(a, 1)).Part(axes.Substring(b, 1)).EndString()
                .Key("min").BeginArray().Value(min[a]).Value(min[b]).EndArray()
                .Prop("cell", cell).Prop("width", width).Prop("height", height).Prop("sampleSeconds", 1);
            WriteCells(json, "humans", humans);
            WriteCells(json, "bots", bots);
            json.EndObject();
        }

        /// <summary>Non-empty cells as a flat [index, count, index, count, ...] list.</summary>
        static void WriteCells(JsonBuilder json, string key, int[] counts)
        {
            json.Key(key).BeginArray();
            for (int i = 0; i < counts.Length; i++)
                if (counts[i] > 0)
                    json.Value(i).Value(counts[i]);
            json.EndArray();
        }
    }
}
