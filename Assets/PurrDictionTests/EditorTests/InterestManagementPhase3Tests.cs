using NUnit.Framework;
using PurrNet;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class InterestManagementPhase3Tests
    {
        [Test]
        public void PredictionLodProfileMapsVisibleAndCulledTierPolicy()
        {
            var networkProfile = ScriptableObject.CreateInstance<NetworkLODProfile>();
            var predictionProfile = ScriptableObject.CreateInstance<PredictionLODProfile>();
            try
            {
                predictionProfile.Configure(
                    networkProfile,
                    new[]
                    {
                        new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.KeepConfigured },
                        new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.ServerRelay },
                        new PredictionLODTier { suggestedPolicy = PredictionPolicyOverride.SoftCorrection }
                    },
                    PredictionPolicyOverride.PredictedIfOwned);

                Assert.That(predictionProfile.networkProfile, Is.SameAs(networkProfile));
                Assert.That(predictionProfile.tierCount, Is.EqualTo(3));
                Assert.That(predictionProfile.GetSuggestedPolicy(0),
                    Is.EqualTo(PredictionPolicyOverride.KeepConfigured));
                Assert.That(predictionProfile.TryGetSuggestedPolicy(0, out _), Is.False);
                Assert.That(predictionProfile.TryGetSuggestedPolicy(1, out var tierOnePolicy), Is.True);
                Assert.That(tierOnePolicy, Is.EqualTo(PredictionPolicy.ServerRelay));
                Assert.That(predictionProfile.TryGetSuggestedPolicy(9, out var clampedPolicy), Is.True);
                Assert.That(clampedPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(predictionProfile.TryGetSuggestedPolicy(
                    NetworkLODProfile.CulledTier, out var culledPolicy), Is.True);
                Assert.That(culledPolicy, Is.EqualTo(PredictionPolicy.PredictedIfOwned));
            }
            finally
            {
                Object.DestroyImmediate(predictionProfile);
                Object.DestroyImmediate(networkProfile);
            }
        }

        [Test]
        public void PredictionLodProfileDefaultsToConfiguredPolicy()
        {
            var profile = ScriptableObject.CreateInstance<PredictionLODProfile>();
            try
            {
                Assert.That(profile.GetSuggestedPolicy(0),
                    Is.EqualTo(PredictionPolicyOverride.KeepConfigured));
                Assert.That(profile.TryGetSuggestedPolicy(0, out _), Is.False);
                Assert.That(profile.GetSuggestedPolicy(NetworkLODProfile.CulledTier),
                    Is.EqualTo(PredictionPolicyOverride.ServerRelay));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InterestPolicyOverrideRestoresLatestConfiguredPolicy()
        {
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(InterestPolicyOverrideRestoresLatestConfiguredPolicy));
            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var identity = identityObject.AddComponent<Phase3PolicyProbe>();
                identity.AttachForTest(manager);
                identity.SetPredictionPolicyOverride(PredictionPolicy.SoftCorrection);

                identity.SetInterestPolicyOverride(PredictionPolicy.ServerRelay);

                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                identity.SetPredictionPolicyOverride(PredictionPolicy.FullPrediction);

                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                identity.SetInterestPolicyOverride(null);

                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));
                Assert.That(identity.configuredPredictionPolicy,
                    Is.EqualTo(PredictionPolicy.FullPrediction));
                identity.DetachForTest();
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void InterestPolicyOverrideNormalizesWithoutReplacingConfiguredPolicy()
        {
            var identityObject = new GameObject(
                nameof(InterestPolicyOverrideNormalizesWithoutReplacingConfiguredPolicy));
            try
            {
                var identity = identityObject.AddComponent<Phase3UnsupportedPolicyProbe>();
                identity.SetPredictionPolicy(PredictionPolicy.ServerRelay);

                identity.SetInterestPolicyOverride(PredictionPolicy.SoftCorrection);

                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.SetInterestPolicyOverride(null);

                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
            }
        }

        [Test]
        public void RootWithoutAcknowledgedSendsFallsBackToGlobalAcknowledgedTick()
        {
            var baselines = new RootSendBaselineState();
            var unseenRoot = new PredictedObjectID(11);
            var pendingRoot = new PredictedObjectID(12);

            Assert.That(baselines.GetBaseline(unseenRoot, 25), Is.EqualTo(25));

            baselines.MarkSent(pendingRoot, 30);

            Assert.That(baselines.GetBaseline(pendingRoot, 25), Is.EqualTo(25));
        }

        [Test]
        public void SkippedSendTicksKeepTheLastAcknowledgedSentTickAsBaseline()
        {
            var baselines = new RootSendBaselineState();
            var root = new PredictedObjectID(13);

            baselines.MarkSent(root, 8);
            Assert.That(baselines.GetBaseline(root, 10), Is.EqualTo(8));
            Assert.That(baselines.GetBaseline(root, 13), Is.EqualTo(8));

            baselines.MarkSent(root, 14);
            Assert.That(baselines.GetBaseline(root, 16), Is.EqualTo(14));
            Assert.That(baselines.GetBaseline(root, 21), Is.EqualTo(14));
        }
    }

    public sealed class Phase3PolicyProbe : PredictedIdentity<EmptyState>
    {
        public override bool supportsSoftCorrection => true;

        public void AttachForTest(PredictionManager manager)
        {
            predictionManager = manager;
            RefreshResolvedPredictionPolicy();
        }

        public void DetachForTest()
        {
            predictionManager = null;
        }
    }

    public sealed class Phase3UnsupportedPolicyProbe : PredictedIdentity<EmptyState>
    {
    }
}
