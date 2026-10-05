using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class SoftCorrectionPoolFixtureTests
    {
        [Test]
        public void LateAwakeCapturesPlacedObjectBeforePredictedStateInitialization()
        {
            var go = new GameObject("pool fixture spawn origin test");
            go.SetActive(false);
            try
            {
                var spawn = new Vector3(60f, 4f, 0f);
                go.transform.position = spawn;
                var probe = go.AddComponent<SoftCorrectionPoolProbe>();
                Assert.That(probe.currentState.unityPosition, Is.EqualTo(Vector3.zero),
                    "LateAwake precedes initial predicted-state capture in Setup.");

                typeof(SoftCorrectionPoolProbe).GetMethod("LateAwake", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(probe, null);

                Assert.That(typeof(SoftCorrectionPoolProbe)
                    .GetField("_spawnPosition", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(probe), Is.EqualTo(spawn),
                    "The one-meter disturbance must be relative to the placed object, not world zero.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void PoolResetRetainsCompletedLifetimeProvenanceForTheFirstLiveTick()
        {
            var go = new GameObject("pool fixture lifecycle test");
            go.SetActive(false);
            try
            {
                SoftCorrectionPoolProbe.ResetStats();
                var probe = go.AddComponent<SoftCorrectionPoolProbe>();
                typeof(SoftCorrectionPoolProbe).GetMethod("OnAddedToPool", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(probe, null);

                probe.ResetState();

                Assert.That(SoftCorrectionPoolProbe.priorLifetimeReuses, Is.EqualTo(1));
                Assert.That(typeof(SoftCorrectionPoolProbe)
                    .GetField("_reusedAfterCompletedLifetime", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(probe), Is.EqualTo(true),
                    "ResetState must not erase the provenance set by its base OnRemovedFromPool callback.");
            }
            finally
            {
                Object.DestroyImmediate(go);
                SoftCorrectionPoolProbe.ResetStats();
            }
        }
    }
}
