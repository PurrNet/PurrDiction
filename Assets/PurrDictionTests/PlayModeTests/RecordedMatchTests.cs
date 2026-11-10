using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using PurrNet.Prediction.Recording;
using PurrNet.Recording;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PurrNet.Prediction.Tests
{
    /// <summary>
    /// A real hosted match of the SimpleSetup example recorded by one <see cref="NetworkRecorder"/>: predicted state and
    /// input come through PurrDiction's extension, on prediction ticks, and the PredictionManager's own networking
    /// stays out of the network data.
    /// </summary>
    public class RecordedMatchTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static IEnumerator WaitUntil(System.Func<bool> condition, float timeout)
        {
            var deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        [UnityTest]
        public IEnumerator AHostedMatchIsOneRecordingWithPredictedStateAndNoPredictionPlumbing()
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
                "Assets/PurrDiction/Examples/SimpleSetup.unity", new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("Loads an example scene by path; editor only.");
            yield break;
#endif
            yield return WaitUntil(() => NetworkManager.main, 10);
            var manager = NetworkManager.main;
            Assert.That(manager, Is.Not.Null, "the scene has a NetworkManager");
            yield return WaitUntil(() => manager.isServer, 5);
            if (!manager.isServer)
                manager.StartHost();
            yield return WaitUntil(() => manager.isServer && manager.isClient, 20);
            Assert.That(manager.isServer && manager.isClient, Is.True, "hosting");

            var finished = new List<string>();
            var recorder = manager.gameObject.AddComponent<NetworkRecorder>();
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../test-results/recorder-tests"));
            typeof(NetworkRecorder).GetField("_upload", Fields)!.SetValue(recorder, false);
            typeof(NetworkRecorder).GetField("_recordInEditor", Fields)!.SetValue(recorder, true);
            recorder.SetDirectory(directory);
            recorder.onRecordingFinished += finished.Add;

            var extension = recorder.GetExtension<PredictionRecording>();
            Assert.That(extension, Is.Not.Null, "PurrDiction registered its extension at load");
            yield return WaitUntil(() => recorder.isRecording, 20);
            Assert.That(recorder.isRecording, Is.True, "recording started on its own");
            Assert.That(extension.predictionManager && extension.predictionManager.isSpawned, Is.True,
                "the recording waited for the PredictionManager");
            ulong startTick = extension.predictionManager.localTick;

            yield return new WaitForSecondsRealtime(4f);
            string path = recorder.currentPath;
            ulong endTick = extension.predictionManager.localTick;
            recorder.StopRecording();
            yield return WaitUntil(() => finished.Count > 0 && File.Exists(path) && new FileInfo(path).Length > 0, 10);
            File.Copy(path, Path.Combine(directory, "hosted-match.purrrec"), true);

            Assert.That(finished, Is.EqualTo(new[] { path }), "one file for the match");
            var meta = ReadMeta(path);
            string all = Dump(meta);
            var header = meta.Find(m => m.kind == RecordKind.Header).json;
            StringAssert.Contains("\"recorder\":\"host\"", header);
            ulong firstTick = ulong.Parse(System.Text.RegularExpressions.Regex.Match(header, "\"firstTick\":(\\d+)").Groups[1].Value);
            Assert.That(firstTick, Is.InRange(startTick > 200 ? startTick - 200 : 0, endTick), "the file runs on prediction ticks");

            Assert.That(meta.Exists(m => m.kind == RecordKind.Stream && m.json.Contains("\"kind\":\"state\"")), "predicted state:\n" + all);
            Assert.That(meta.Exists(m => m.kind == RecordKind.Stream && m.json.Contains("\"kind\":\"input\"")), "predicted input:\n" + all);
            Assert.That(meta.Exists(m => m.kind == RecordKind.Player && m.json.Contains("\"event\":\"joined\"") && m.json.Contains("\"stream\":")),
                "players come from PurrDiction, with their per-tick stream:\n" + all);
            Assert.That(meta.FindAll(m => m.kind == RecordKind.Player && m.json.Contains("\"event\":\"joined\"")).Count, Is.EqualTo(1),
                "and only once:\n" + all);
            Assert.That(meta.Exists(m => m.kind == RecordKind.Footer && m.json.Contains("\"summary\"")), "the match summary:\n" + all);

            Assert.That(meta.Exists(m => m.json.Contains("\"syncs\"") && m.json.Contains("PredictionManager")), Is.False,
                "the PredictionManager is not recorded as a network identity:\n" + all);
            Assert.That(meta.Exists(m => m.kind == RecordKind.Stream && m.json.Contains("\"kind\":\"sync\"") && m.json.Contains("PredictionManager")),
                Is.False, "nor any sync type of it:\n" + all);
            Debug.Log("[RecordedMatchTests] " + path + "\n" + all);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            var manager = NetworkManager.main;
            if (manager)
            {
                manager.StopClient();
                manager.StopServer();
                for (int i = 0; i < 10; i++)
                    yield return null;
            }
        }

        static string Dump(List<(RecordKind kind, string json)> meta)
        {
            var text = new StringBuilder();
            int shown = 0;
            foreach (var m in meta)
            {
                if (m.kind == RecordKind.Type || shown++ > 80)
                    continue;
                text.Append(m.kind).Append(' ').Append(m.json.Length > 260 ? m.json.Substring(0, 260) : m.json).Append('\n');
            }
            return text.ToString();
        }

        /// <summary>The JSON metadata records of a recording, in file order (batches expanded).</summary>
        static List<(RecordKind kind, string json)> ReadMeta(string path)
        {
            var data = File.ReadAllBytes(path);
            var result = new List<(RecordKind, string)>();
            Read(data, 10, data.Length, result);
            return result;
        }

        static void Read(byte[] data, int pos, int end, List<(RecordKind, string)> into)
        {
            while (pos < end)
            {
                byte kind = data[pos++];
                ulong length = 0;
                int shift = 0;
                while (true)
                {
                    byte b = data[pos++];
                    length |= (ulong)(b & 0x7F) << shift;
                    if (b < 0x80)
                        break;
                    shift += 7;
                }
                var payload = new byte[(int)length];
                System.Array.Copy(data, pos, payload, 0, (int)length);
                pos += (int)length;
                if ((kind & 0x80) != 0)
                {
                    using var input = new MemoryStream(payload);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    deflate.CopyTo(output);
                    payload = output.ToArray();
                }
                var k = (RecordKind)(kind & 0x7F);
                if (k == RecordKind.Batch)
                    Read(payload, 0, payload.Length, into);
                else if (k != RecordKind.Chunk && k != RecordKind.Mesh)
                    into.Add((k, Encoding.UTF8.GetString(payload)));
            }
        }
    }
}
