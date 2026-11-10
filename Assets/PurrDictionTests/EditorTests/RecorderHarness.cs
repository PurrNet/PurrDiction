using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using PurrNet.Prediction.Profiler;
using PurrNet.Prediction.Recording;
using PurrNet.Recording;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// A server world (see <see cref="PooledSpawnAllocationTests.World"/>) with a <see cref="NetworkRecorder"/> fed by
    /// hand: each <see cref="Tick"/> steps the server, records the tick and delivers the frame.
    /// </summary>
    internal sealed class RecorderHarness : IDisposable
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public readonly PooledSpawnAllocationTests.World world;
        public readonly NetworkRecorder recorder;
        public readonly string directory;
        readonly Action<ulong> _record;
        readonly List<GameObject> _scene = new List<GameObject>();
        public ulong tick = 11;
        string _path;

        public RecorderHarness(int tickRate, Action<GameObject> decoratePrefab = null)
        {
            world = new PooledSpawnAllocationTests.World(tickRate, decoratePrefab: decoratePrefab);
            // Edit mode runs no load-time registration.
            NetworkRecorder.RegisterExtension<PredictionRecording>();
            recorder = world.server.gameObject.AddComponent<NetworkRecorder>();
            directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../test-results/recorder-tests"));
            Set(recorder, "_recordCamera", false);
            Set(recorder, "_upload", false);
            Set(recorder, "_directory", directory);
            Invoke(recorder, "Awake");
            recorder.GetExtension<PredictionRecording>().predictionManager = world.server;
            _record = recorder.RecordTick;
        }

        /// <summary>A scene object destroyed with the harness.</summary>
        public GameObject SceneObject(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent)
                go.transform.SetParent(parent, false);
            else
                _scene.Add(go);
            return go;
        }

        public void Start(string fileName)
        {
            recorder.StartRecording(fileName);
            Assert.That(recorder.isRecording, Is.True);
            _path = recorder.currentPath;
        }

        public void Tick(Action beforeRecord = null)
        {
            world.StepServer(tick);
            beforeRecord?.Invoke();
            _record(tick);
            world.Deliver(tick);
            TickBandwidthProfiler.MarkEndOfTick();
            world.LateUpdate();
            tick++;
        }

        public void Ticks(int count)
        {
            for (int i = 0; i < count; i++)
                Tick();
        }

        public void Record(ulong at) => _record(at);

        /// <summary>Stops the recording and returns its metadata records (kind, JSON), in file order.</summary>
        public List<(RecordKind kind, string json)> Stop()
        {
            recorder.StopRecording();
            return ReadMeta(_path);
        }

        /// <summary>Sets a recorder field (an inspector setting, say) before <see cref="Start"/>.</summary>
        public void Configure(string name, object value) => Set(recorder, name, value);

        public object Private(string name) => typeof(RecordingSession).GetField(name, Fields)!.GetValue(recorder.session);

        public void Dispose()
        {
            if (recorder)
            {
                Invoke(recorder, "OnDestroy");
                Object.DestroyImmediate(recorder);
            }
            foreach (var go in _scene)
                if (go) Object.DestroyImmediate(go);
            world.Dispose();
        }

        /// <summary>The JSON metadata records of a .purrrec file, including those inside BATCH records.</summary>
        public static List<(RecordKind kind, string json)> ReadMeta(string path)
        {
            var result = new List<(RecordKind, string)>();
            var bytes = File.ReadAllBytes(path);
            Assert.That(Encoding.ASCII.GetString(bytes, 0, 7), Is.EqualTo("PURRREC"));
            Read(bytes, 10, bytes.Length, true, result);
            return result;
        }

        /// <summary>The raw payloads of the records of one kind, including those inside BATCH records.</summary>
        public static List<byte[]> ReadPayloads(string path, RecordKind kind)
        {
            var result = new List<byte[]>();
            var bytes = File.ReadAllBytes(path);
            Read(bytes, 10, bytes.Length, true, null, (k, payload) =>
            {
                if (k == kind)
                    result.Add(payload);
            });
            return result;
        }

        public string path => _path;

        /// <summary>The recorder's description of the last recording (it logs nothing itself).</summary>
        public string Describe() => recorder.DescribeLastRecording();

        static void Read(byte[] data, int pos, int end, bool topLevel, List<(RecordKind, string)> into,
            Action<RecordKind, byte[]> onRecord = null)
        {
            while (pos < end)
            {
                int kind = data[pos++];
                int length = (int)VarUInt(data, ref pos);
                var payload = new byte[length];
                Buffer.BlockCopy(data, pos, payload, 0, length);
                pos += length;
                if (topLevel && (kind & 0x80) != 0)
                {
                    using var inflate = new DeflateStream(new MemoryStream(payload), CompressionMode.Decompress);
                    using var raw = new MemoryStream();
                    inflate.CopyTo(raw);
                    payload = raw.ToArray();
                    kind &= 0x7F;
                }
                var k = (RecordKind)kind;
                onRecord?.Invoke(k, payload);
                if (k == RecordKind.Batch)
                    Read(payload, 0, payload.Length, false, into, onRecord);
                else if (into != null && k != RecordKind.Chunk && k != RecordKind.Mesh)
                    into.Add((k, Encoding.UTF8.GetString(payload)));
            }
        }

        static ulong VarUInt(byte[] data, ref int pos)
        {
            ulong value = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    return value;
            }
        }

        static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, Fields);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            field.SetValue(target, value);
        }

        static void Invoke(object target, string name)
        {
            var method = target.GetType().GetMethod(name, Fields);
            Assert.That(method, Is.Not.Null, $"Missing method {name}");
            method.Invoke(target, null);
        }
    }
}
