using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Prediction.Recording;
using PurrNet.Recording;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// What the recorder writes about colliders: the scene's static set (checked a slice at a time over the ticks) and
    /// each entity's own set.
    /// </summary>
    public sealed class RecorderColliderTests
    {
        [OneTimeSetUp]
        public void RegisterPackers() => new PooledSpawnAllocationTests().RegisterPackers();

        // Long enough for a periodic static check to start and finish.
        const int CheckTicks = 130;

        static List<int> StaticSets(List<(RecordKind kind, string json)> meta) => meta
            .Where(m => m.kind == RecordKind.Colliders && m.json.StartsWith("{\"entity\":null"))
            .Select(m => Regex.Matches(m.json, "\\{\"shape\":").Count)
            .ToList();

        static List<string> StaticNames(string json) =>
            Regex.Matches(json, "\"name\":\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();

        [Test]
        public void StaticColliderChangesAreWrittenAndUnchangedSetsAreNot()
        {
            using var h = new RecorderHarness(60);
            var map = h.SceneObject("Map");
            var a = h.SceneObject("A", map.transform).AddComponent<BoxCollider>();
            var b = h.SceneObject("B", map.transform).AddComponent<BoxCollider>();
            var c = h.SceneObject("C", map.transform).AddComponent<BoxCollider>();
            // Not static: owned by a predicted object, disabled, or on an inactive object.
            var owned = h.SceneObject("Owned");
            owned.AddComponent<SpawnChurnProbe>();
            h.SceneObject("Owned child", owned.transform).AddComponent<BoxCollider>();
            h.SceneObject("Disabled", map.transform).AddComponent<BoxCollider>().enabled = false;
            var inactive = h.SceneObject("Inactive", map.transform);
            inactive.AddComponent<BoxCollider>();
            inactive.SetActive(false);

            h.Start("static-colliders.purrrec");
            h.Ticks(CheckTicks * 2);
            b.enabled = false;
            h.Ticks(CheckTicks);
            Object.DestroyImmediate(c.gameObject);
            h.Ticks(CheckTicks);
            h.SceneObject("D", map.transform).AddComponent<SphereCollider>();
            h.Ticks(CheckTicks);
            inactive.SetActive(true);
            h.Ticks(CheckTicks);
            a.transform.localPosition = new Vector3(0, 5, 0);
            h.Ticks(CheckTicks);
            var meta = h.Stop();

            var sets = StaticSets(meta);
            Assert.That(sets, Is.EqualTo(new[] { 3, 2, 1, 2, 3 }), "one COLLIDERS record per change of the static set");
            var last = meta.Last(m => m.kind == RecordKind.Colliders && m.json.StartsWith("{\"entity\":null")).json;
            Assert.That(StaticNames(last), Is.EqualTo(new[] { "A", "Inactive", "D" }), "the walk keeps hierarchy order");
            Assert.That(meta.Count(m => m.kind == RecordKind.Stream && m.json.Contains("\"kind\":\"collider\"") &&
                                         m.json.Contains("\"entity\":null")), Is.EqualTo(1),
                "the static collider that moved gets a pose stream");
        }

        [Test]
        public void StaticCheckIsSpreadOverTicks()
        {
            using var h = new RecorderHarness(60);
            var map = h.SceneObject("Map");
            for (int i = 0; i < 3000; i++)
                h.SceneObject("Piece " + i, map.transform).AddComponent<BoxCollider>();
            h.Start("static-spread.purrrec");

            // Ticks that do static-check work, seen through the check's state.
            int busy = 0;
            for (int i = 0; i < 240; i++)
            {
                h.Tick();
                if (h.Private("_staticCheck").ToString() != "Idle")
                    busy++;
            }
            var meta = h.Stop();
            Assert.That(StaticSets(meta), Is.EqualTo(new[] { 3000 }), "an unchanged set is written once");
            // Four checks; each would take one tick if it were not spread.
            Assert.That(busy, Is.GreaterThan(4 * 10), "the check is spread over many ticks");
            Assert.That(busy, Is.LessThan(240), "a check finishes before the next one is due");
        }

        [Test]
        public void NestedPredictedObjectsKeepTheirOwnCollidersAndRenderers()
        {
            using var h = new RecorderHarness(60, prefab =>
            {
                prefab.AddComponent<BoxCollider>();
                prefab.AddComponent<MeshRenderer>();
                var nested = new GameObject("Nested");
                nested.transform.SetParent(prefab.transform, false);
                nested.AddComponent<SpawnChurnProbe>();
                nested.AddComponent<SphereCollider>();
                nested.AddComponent<CapsuleCollider>();
                nested.AddComponent<MeshRenderer>();
            });
            h.world.server.hierarchy.Create(0, Vector3.zero, Quaternion.identity);
            h.Start("nested-colliders.purrrec");
            h.Ticks(30);
            var meta = h.Stop();

            var entities = meta.Where(m => m.kind == RecordKind.Entity).Select(m => m.json).ToList();
            var root = entities.Single(e => e.Contains("\"parent\":null") && e.Contains("Churn prefab"));
            var nestedEntity = entities.Single(e => e.Contains("\"name\":\"Nested\""));
            string Id(string json) => Regex.Match(json, "\"entity\":(\\d+)").Groups[1].Value;
            string Shapes(string entity) => string.Join(",", Regex.Matches(meta.Last(m =>
                    m.kind == RecordKind.Colliders && m.json.StartsWith("{\"entity\":" + entity + ",")).json,
                "\"shape\":\"(\\w+)\"").Select(m => m.Groups[1].Value));
            Assert.That(Shapes(Id(root)), Is.EqualTo("box"));
            Assert.That(Shapes(Id(nestedEntity)), Is.EqualTo("sphere,capsule"));
        }
    }
}
