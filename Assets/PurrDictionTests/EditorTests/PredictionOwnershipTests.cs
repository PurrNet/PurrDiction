using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public class PredictionOwnershipTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp()
        {
            TrackedDisposable.Reset();
        }

        [Test]
        public void SparseHistoryPrunesByTickAgeAndKeepsRollbackAnchor()
        {
            var history = new History<TrackedDisposable>(10);
            history.Write(1, new TrackedDisposable(1));
            history.Write(20, new TrackedDisposable(2));
            history.Write(95, new TrackedDisposable(3));

            history.PruneByTickWindow(100);

            Assert.That(history.Count, Is.EqualTo(2));
            Assert.That(history.OldestTick, Is.EqualTo(20));
            Assert.That(history.ReadOrPrevious(90, out var anchor), Is.True);
            Assert.That(anchor.id, Is.EqualTo(2));
            Assert.That(TrackedDisposable.disposeCount, Is.EqualTo(1));

            history.PruneByTickWindow(120);

            Assert.That(history.Count, Is.EqualTo(1));
            Assert.That(history.OldestTick, Is.EqualTo(95));
            Assert.That(TrackedDisposable.disposeCount, Is.EqualTo(2));

            history.Clear();
            Assert.That(TrackedDisposable.disposeCount, Is.EqualTo(3));
        }

        [Test]
        public void StatefulInputIdentityReleasesEveryOwnedInputWhenPooled()
        {
            var gameObject = new GameObject(nameof(StatefulInputIdentityReleasesEveryOwnedInputWhenPooled));
            try
            {
                var identity = gameObject.AddComponent<StatefulInputProbe>();
                var history = SeedInputStorage(identity, typeof(PredictedIdentity<TrackedInput, EmptyState>));

                identity.ReleasePredictionStateForPool();

                Assert.That(history.Count, Is.Zero);
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));

                identity.ReleasePredictionStateForPool();
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DeterministicInputIdentityReleasesEveryOwnedInputWhenPooled()
        {
            var gameObject = new GameObject(nameof(DeterministicInputIdentityReleasesEveryOwnedInputWhenPooled));
            try
            {
                var identity = gameObject.AddComponent<DeterministicInputProbe>();
                var history = SeedInputStorage(identity, typeof(DeterministicIdentity<TrackedInput, EmptyState>));

                identity.ReleasePredictionStateForPool();

                Assert.That(history.Count, Is.Zero);
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));

                identity.ReleasePredictionStateForPool();
                Assert.That(TrackedInput.disposeCount, Is.EqualTo(6));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

#if UNITY_PHYSICS_3D
        [Test]
        public void ProjectileInterpolationDoesNotAllocateDisposableViewState()
        {
            var gameObject = new GameObject(nameof(ProjectileInterpolationDoesNotAllocateDisposableViewState));
            try
            {
                var projectile = gameObject.AddComponent<ProjectileInterpolationProbe>();
                var result = projectile.InterpolateForTest(default, default, 0.5f);

                Assert.That(result.overlappingTriggers.isDisposed, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
#endif

        [Test]
        public void ReparentingIdentityRefreshesNearestPredictionPolicyScope()
        {
            var managerObject = new GameObject("PredictionManager");
            var firstParent = new GameObject("FirstScope");
            var secondParent = new GameObject("SecondScope");
            var identityObject = new GameObject("PolicyProbe");

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var firstScope = firstParent.AddComponent<PredictionPolicyScope>();
                var secondScope = secondParent.AddComponent<PredictionPolicyScope>();
                firstScope.configuredPredictionPolicy = PredictionPolicy.ServerRelay;
                secondScope.configuredPredictionPolicy = PredictionPolicy.FullPrediction;

                identityObject.transform.SetParent(firstParent.transform);
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                identityObject.transform.SetParent(secondParent.transform);
                identity.RefreshParentScopeForTest();
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(firstParent);
                UnityEngine.Object.DestroyImmediate(secondParent);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void RuntimePolicyApisPersistOverridesAndReturnToScope()
        {
            var managerObject = new GameObject("PredictionManager");
            var scopeObject = new GameObject("Scope");
            var identityObject = new GameObject("PolicyProbe");

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var scope = scopeObject.AddComponent<PredictionPolicyScope>();
                scope.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;

                identityObject.transform.SetParent(scopeObject.transform);
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));

                identity.SetPredictionPolicyOverride(PredictionPolicy.ServerRelay);
                Assert.That(identity.predictionPolicySource, Is.EqualTo(PredictionPolicySource.OverrideScope));
                Assert.That(identity.configuredPredictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                scope.SetPredictionPolicy(PredictionPolicy.FullPrediction);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                identity.UsePredictionPolicyScope();
                Assert.That(identity.predictionPolicySource, Is.EqualTo(PredictionPolicySource.UseScope));
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.SetPredictionPolicy(PredictionPolicy.SoftCorrection);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));
                scope.ApplyToIdentities();
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(scopeObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void CustomScopeResolvesDescendantsAgainWhenOwnershipChanges()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var scopeObject = new GameObject("OwnershipScope");
            var firstIdentityObject = new GameObject("FirstIdentity");
            var secondIdentityObject = new GameObject("SecondIdentity");

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var localPlayer = new PlayerID(7, false);
                var remotePlayer = new PlayerID(8, false);
                SetField(typeof(NetworkIdentity), manager, "_localPlayer", (PlayerID?)localPlayer);

                scopeObject.AddComponent<OwnershipPolicyScopeProbe>();
                firstIdentityObject.transform.SetParent(scopeObject.transform);
                secondIdentityObject.transform.SetParent(scopeObject.transform);

                var firstIdentity = firstIdentityObject.AddComponent<PolicyProbe>();
                var secondIdentity = secondIdentityObject.AddComponent<PolicyProbe>();
                firstIdentity.AttachForTest(manager);
                secondIdentity.AttachForTest(manager);

                firstIdentity.owner = localPlayer;
                secondIdentity.owner = remotePlayer;

                Assert.That(firstIdentity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));
                Assert.That(secondIdentity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));

                firstIdentity.owner = remotePlayer;
                secondIdentity.owner = localPlayer;

                Assert.That(firstIdentity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(secondIdentity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                firstIdentity.DetachForTest();
                secondIdentity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstIdentityObject);
                UnityEngine.Object.DestroyImmediate(secondIdentityObject);
                UnityEngine.Object.DestroyImmediate(scopeObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void OwnedSoftFallbackSwitchesEffectiveTimelineWithoutReplacingConfiguredPolicy()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var scopeObject = new GameObject("PolicyScope");
            var identityObject = new GameObject("PolicyProbe");

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var localPlayer = new PlayerID(7, false);
                var remotePlayer = new PlayerID(8, false);
                SetField(typeof(NetworkIdentity), manager, "_localPlayer", (PlayerID?)localPlayer);

                var scope = scopeObject.AddComponent<PredictionPolicyScope>();
                scope.configuredPredictionPolicy =
                    PredictionPolicy.PredictedIfOwnedWithSoftFallback;
                identityObject.transform.SetParent(scopeObject.transform);
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                identity.owner = remotePlayer;

                Assert.That(identity.predictionPolicy,
                    Is.EqualTo(PredictionPolicy.PredictedIfOwnedWithSoftFallback));
                Assert.That(identity.EffectivePolicy(), Is.EqualTo(PredictionPolicy.SoftCorrection));

                identity.ResetPolicyTransitions();
                identity.owner = localPlayer;

                Assert.That(identity.predictionPolicy,
                    Is.EqualTo(PredictionPolicy.PredictedIfOwnedWithSoftFallback));
                Assert.That(identity.EffectivePolicy(), Is.EqualTo(PredictionPolicy.FullPrediction));
                Assert.That(identity.policyTransitionCount, Is.EqualTo(1));
                Assert.That(identity.lastOldPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(identity.lastNewPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.owner = remotePlayer;

                Assert.That(identity.predictionPolicy,
                    Is.EqualTo(PredictionPolicy.PredictedIfOwnedWithSoftFallback));
                Assert.That(identity.EffectivePolicy(), Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(identity.policyTransitionCount, Is.EqualTo(2));
                Assert.That(identity.lastOldPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));
                Assert.That(identity.lastNewPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));

                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(scopeObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void SetupAppliesPolicySideEffectsAfterUnityStateCapture()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(SetupAppliesPolicySideEffectsAfterUnityStateCapture));

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var identity = identityObject.AddComponent<PolicySetupOrderProbe>();
                identity.SetPredictionPolicyOverride(PredictionPolicy.SoftCorrection);

                manager.RegisterInstance(identityObject, new PredictedObjectID(1), null, false, false);

                Assert.That(identity.unityStateCaptured, Is.True);
                Assert.That(identity.policyChangedBeforeUnityStateCapture, Is.False,
                    "The policy callback ran before the stateful setup rebuilt its Unity state");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void PooledReuseEvaluatesIncomingPolicyBeforePreservingSoftState()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(PooledReuseEvaluatesIncomingPolicyBeforePreservingSoftState));

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var identity = identityObject.AddComponent<PolicySetupOrderProbe>();
                identity.SetPredictionPolicyOverride(PredictionPolicy.SoftCorrection);

                var objectId = new PredictedObjectID(1);
                manager.RegisterInstance(identityObject, objectId, null, false, false);
                manager.UnregisterInstance(identity);

                SetField(typeof(PredictedIdentity), identity, "_predictionPolicy",
                    PredictionPolicy.FullPrediction);
                manager.RegisterInstance(identityObject, objectId, null, false, false);

                Assert.That(identity.preSetupCount, Is.EqualTo(2),
                    "The old SoftCorrection policy incorrectly suppressed setup for the incoming full-prediction lifetime");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void PooledReuseDoesNotPreserveStateWhenSwitchingIntoSoftCorrection()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(PooledReuseDoesNotPreserveStateWhenSwitchingIntoSoftCorrection));

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var identity = identityObject.AddComponent<PolicySetupOrderProbe>();
                identity.SetPredictionPolicyOverride(PredictionPolicy.FullPrediction);

                var objectId = new PredictedObjectID(1);
                manager.RegisterInstance(identityObject, objectId, null, false, false);
                manager.UnregisterInstance(identity);

                SetField(typeof(PredictedIdentity), identity, "_predictionPolicy",
                    PredictionPolicy.SoftCorrection);
                manager.RegisterInstance(identityObject, objectId, null, false, false);

                Assert.That(identity.preSetupCount, Is.EqualTo(2),
                    "The previous non-soft lifetime was incorrectly preserved when switching into SoftCorrection");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void RootPosePreservationUsesIncomingPolicyOnReplayReuse()
        {
            var managerObject = new GameObject("PredictionManager");
            var hierarchyObject = new GameObject("PredictedHierarchy");
            var instanceObject = new GameObject(nameof(RootPosePreservationUsesIncomingPolicyOnReplayReuse));

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                SetField(typeof(PredictionManager), manager, "<isReplaying>k__BackingField", true);

                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                SetField(typeof(PredictedIdentity), hierarchy,
                    "<predictionManager>k__BackingField", manager);

                var instanceId = new PredictedObjectID(7);
                var predictedTransform = instanceObject.AddComponent<PredictedTransform>();
                predictedTransform.id = new PredictedComponentID(instanceId, 0);
                predictedTransform.SetPredictionPolicy(PredictionPolicy.SoftCorrection);
                SetField(typeof(PredictedIdentity), predictedTransform,
                    "_predictionPolicy", PredictionPolicy.FullPrediction);

                var method = typeof(PredictedHierarchy).GetMethod(
                    "PreservesSoftCorrectionRootPose", InstanceFields);
                Assert.That(method, Is.Not.Null);
                var preservesPose = (bool)method.Invoke(
                    hierarchy, new object[] { instanceObject, instanceId, null });

                Assert.That(preservesPose, Is.False,
                    "The previous lifetime's SoftCorrection policy preserved a stale pooled root pose");

                predictedTransform.SetPredictionPolicy(PredictionPolicy.FullPrediction);
                SetField(typeof(PredictedIdentity), predictedTransform,
                    "_predictionPolicy", PredictionPolicy.SoftCorrection);
                preservesPose = (bool)method.Invoke(
                    hierarchy, new object[] { instanceObject, instanceId, null });

                Assert.That(preservesPose, Is.False,
                    "A non-soft lifetime was incorrectly treated as a live soft pose when switching into SoftCorrection");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instanceObject);
                UnityEngine.Object.DestroyImmediate(hierarchyObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void SetupResolutionIncludesEnabledScopesOnInactivePooledRoots()
        {
            var instanceObject = new GameObject(nameof(SetupResolutionIncludesEnabledScopesOnInactivePooledRoots));

            try
            {
                var scope = instanceObject.AddComponent<PredictionPolicyScope>();
                scope.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
                var predictedTransform = instanceObject.AddComponent<PredictedTransform>();
                instanceObject.SetActive(false);

                Assert.That(predictedTransform.ResolvePredictionPolicyForSetup(),
                    Is.EqualTo(PredictionPolicy.SoftCorrection),
                    "An enabled scope on an inactive pooled root was ignored during registration-time resolution");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instanceObject);
            }
        }

        [Test]
        public void DeactivatingScopedPooledRootPreservesAppliedSoftCorrectionPolicy()
        {
            var managerObject = new GameObject("PredictionManager");
            var instanceObject = new GameObject(
                nameof(DeactivatingScopedPooledRootPreservesAppliedSoftCorrectionPolicy));

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var scope = instanceObject.AddComponent<PredictionPolicyScope>();
                scope.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
                var identity = instanceObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));

                instanceObject.SetActive(false);

                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection),
                    "Pool deactivation replaced the scoped policy before unregistration");
                Assert.That(identity.ResolvePredictionPolicyForSetup(),
                    Is.EqualTo(PredictionPolicy.SoftCorrection),
                    "Registration-time resolution did not preserve the enabled pooled scope");

                instanceObject.SetActive(true);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));
                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instanceObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void DeactivatingRegisteredScopeRefreshesItsLiveIdentities()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var scopeObject = new GameObject(nameof(DeactivatingRegisteredScopeRefreshesItsLiveIdentities));

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                var scope = scopeObject.AddComponent<PredictionPolicyScope>();
                scope.configuredPredictionPolicy = PredictionPolicy.ServerRelay;
                var identity = scopeObject.AddComponent<PolicyProbe>();

                manager.RegisterInstance(scopeObject, new PredictedObjectID(1), null, false, false);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                scopeObject.SetActive(false);
                InvokeLifecycle(scope, "OnDisable");

                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction),
                    "The inactive live scope remained applied to a registered identity");
                manager.UnregisterInstance(identity);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scopeObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void SetupResolutionPreservesVirtualPolicyOverridesOnInactivePooledRoots()
        {
            var instanceObject = new GameObject(nameof(SetupResolutionPreservesVirtualPolicyOverridesOnInactivePooledRoots));

            try
            {
                var identity = instanceObject.AddComponent<SetupPolicyOverrideProbe>();
                instanceObject.SetActive(false);

                Assert.That(identity.ResolvePredictionPolicyForSetup(),
                    Is.EqualTo(PredictionPolicy.ServerRelay),
                    "Registration-time policy resolution bypassed the identity's virtual override");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instanceObject);
            }
        }

        [Test]
        public void ModuleVerifiedStateUsesTheParentVerifiedTick()
        {
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(ModuleVerifiedStateUsesTheParentVerifiedTick));

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                var module = new ModuleHistoryProbe(identity);

                var historyField = typeof(PredictedModule<ModuleHistoryState>).GetField(
                    "_history", InstanceFields);
                Assert.That(historyField, Is.Not.Null);
                var history = (History<MODULE_STATE<ModuleHistoryState>>)historyField.GetValue(module);
                history.Write(5, new MODULE_STATE<ModuleHistoryState>
                {
                    state = new ModuleHistoryState { value = 5 }
                });
                history.Write(10, new MODULE_STATE<ModuleHistoryState>
                {
                    state = new ModuleHistoryState { value = 10 }
                });
                identity.lastVerifiedTick = 5;

                Assert.That(module.verifiedState.HasValue, Is.True);
                Assert.That(module.verifiedState.Value.value, Is.EqualTo(5),
                    "The module exposed a future predicted entry as its verified state");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void DefaultCorrectionRingRetainsTheDefaultTenSecondHistoryWindow()
        {
            var ring = new AppliedCorrectionRing<int>();

            for (ulong tick = 0; tick < 200; tick++)
                ring.Record(tick, (int)tick);

            Assert.That(ring.TryGetBaseline(1, out var baseline), Is.True);
            Assert.That(baseline, Is.EqualTo(1),
                "The correction baseline was overwritten before the default 20 Hz history window expired");
        }

        [Test]
        public void UnsupportedIdentityKeepsSoftCorrectionAndRelaysBehaviorally()
        {
            var gameObject = new GameObject(
                nameof(UnsupportedIdentityKeepsSoftCorrectionAndRelaysBehaviorally));
            var managerObject = new GameObject("PredictionManager");
            try
            {
                var identity = gameObject.AddComponent<UnsupportedPolicyProbe>();
                var manager = managerObject.AddComponent<PredictionManager>();

                identity.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
                identity.AttachForTest(manager);

                Assert.That(identity.supportsSoftCorrection, Is.False);
                Assert.That(identity.configuredPredictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(identity.GetResolvedPredictionPolicy(), Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(identity.EffectivePolicy(), Is.EqualTo(PredictionPolicy.ServerRelay));
                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void UnsupportedIdentityKeepsOwnedSoftFallbackAndRelaysWhenNotOwned()
        {
            var gameObject = new GameObject(
                nameof(UnsupportedIdentityKeepsOwnedSoftFallbackAndRelaysWhenNotOwned));
            var managerObject = new GameObject("PredictionManager");
            try
            {
                var identity = gameObject.AddComponent<UnsupportedPolicyProbe>();
                var manager = managerObject.AddComponent<PredictionManager>();

                identity.configuredPredictionPolicy =
                    PredictionPolicy.PredictedIfOwnedWithSoftFallback;
                identity.AttachForTest(manager);

                Assert.That(identity.supportsSoftCorrection, Is.False);
                Assert.That(identity.configuredPredictionPolicy,
                    Is.EqualTo(PredictionPolicy.PredictedIfOwnedWithSoftFallback));
                Assert.That(identity.GetResolvedPredictionPolicy(),
                    Is.EqualTo(PredictionPolicy.PredictedIfOwnedWithSoftFallback));
                Assert.That(identity.EffectivePolicy(),
                    Is.EqualTo(PredictionPolicy.ServerRelay));
                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ResetStateClearsPendingSoftTransformCorrection()
        {
            var gameObject = new GameObject(nameof(ResetStateClearsPendingSoftTransformCorrection));
            try
            {
                var predictedTransform = gameObject.AddComponent<PredictedTransform>();
                SetField(typeof(PredictedTransform), predictedTransform,
                    "_softPositionError", new Vector3(2f, 0f, 0f));
                SetField(typeof(PredictedTransform), predictedTransform,
                    "_hasSoftError", true);

                predictedTransform.ResetState();

                var errorField = typeof(PredictedTransform).GetField("_softPositionError", InstanceFields);
                var hasErrorField = typeof(PredictedTransform).GetField("_hasSoftError", InstanceFields);
                Assert.That(errorField, Is.Not.Null);
                Assert.That(hasErrorField, Is.Not.Null);
                Assert.That((Vector3)errorField.GetValue(predictedTransform), Is.EqualTo(Vector3.zero));
                Assert.That((bool)hasErrorField.GetValue(predictedTransform), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DisablingOrRemovingScopeRefreshesDescendants()
        {
            var managerObject = new GameObject("PredictionManager");
            var scopeObject = new GameObject("Scope");
            var identityObject = new GameObject("PolicyProbe");

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var scope = scopeObject.AddComponent<PredictionPolicyScope>();
                scope.configuredPredictionPolicy = PredictionPolicy.ServerRelay;

                identityObject.transform.SetParent(scopeObject.transform);
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                scope.enabled = false;
                InvokeLifecycle(scope, "OnDisable");
                Assert.That(identity.TryGetPredictionPolicyScope(out _), Is.False);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                scope.enabled = true;
                scope.ApplyToIdentities();
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                scope.enabled = false;
                InvokeLifecycle(scope, "OnDisable");
                UnityEngine.Object.DestroyImmediate(scope);
                Assert.That(identity.TryGetPredictionPolicyScope(out _), Is.False);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(scopeObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void InheritedScopeTracksParentPolicyAndCanReturnToLocalPolicy()
        {
            var managerObject = new GameObject("PredictionManager");
            var parentObject = new GameObject("ParentScope");
            var childObject = new GameObject("ChildScope");
            var identityObject = new GameObject("PolicyProbe");

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var parentScope = parentObject.AddComponent<PredictionPolicyScope>();
                parentScope.configuredPredictionPolicy = PredictionPolicy.ServerRelay;

                childObject.transform.SetParent(parentObject.transform);
                var childScope = childObject.AddComponent<PredictionPolicyScope>();
                childScope.configuredPredictionPolicy = PredictionPolicy.FullPrediction;
                childScope.inheritFromParent = true;

                identityObject.transform.SetParent(childObject.transform);
                var identity = identityObject.AddComponent<PolicyProbe>();
                identity.AttachForTest(manager);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                parentScope.SetPredictionPolicy(PredictionPolicy.SoftCorrection);
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));

                childScope.inheritFromParent = false;
                Assert.That(identity.predictionPolicy, Is.EqualTo(PredictionPolicy.FullPrediction));

                identity.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(childObject);
                UnityEngine.Object.DestroyImmediate(parentObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void TransformFollowsPhysicsPolicyOwnerInsteadOfIndependentScope()
        {
            var managerObject = new GameObject("PredictionManager");
            var scopeObject = new GameObject("Scope");
            var identityObject = new GameObject("PolicyOwner");

            try
            {
                var manager = managerObject.AddComponent<PredictionManager>();
                var scope = scopeObject.AddComponent<PredictionPolicyScope>();
                scope.configuredPredictionPolicy = PredictionPolicy.FullPrediction;

                identityObject.transform.SetParent(scopeObject.transform);
                var predictedTransform = identityObject.AddComponent<PredictedTransformPolicyProbe>();
                var policyOwner = identityObject.AddComponent<PolicyOwnerProbe>();
                policyOwner.AttachForTest(manager);
                policyOwner.SetPredictionPolicyOverride(PredictionPolicy.ServerRelay);
                predictedTransform.AttachForTest(manager);

                Assert.That(policyOwner.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));
                Assert.That(predictedTransform.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                scope.SetPredictionPolicy(PredictionPolicy.SoftCorrection);
                Assert.That(policyOwner.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));
                Assert.That(predictedTransform.predictionPolicy, Is.EqualTo(PredictionPolicy.ServerRelay));

                predictedTransform.DetachForTest();
                policyOwner.DetachForTest();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(scopeObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ResetStateDoesNotInterpolateDisposedState()
        {
            var gameObject = new GameObject(nameof(ResetStateDoesNotInterpolateDisposedState));
            try
            {
                var identity = gameObject.AddComponent<PredictedPlayerSpawner>();
                var state = new FULL_STATE<PlayerSpawnerState>
                {
                    state = new PlayerSpawnerState
                    {
                        values = DisposableList<PlayerWithObject>.Create(1)
                    }
                };
                state.state.values.Add(default);
                identity.fullPredictedState = state;

                var initial = new FULL_STATE<PlayerSpawnerState>
                {
                    state = state.state.Duplicate()
                };
                var interpolation = new PredictedViewBuffer<FULL_STATE<PlayerSpawnerState>>(
                    (from, _, _) => from,
                    0,
                    initial,
                    2);
                SetField(typeof(DeterministicIdentity<PlayerSpawnerState>), identity,
                    "_interpolatedState", interpolation);

                Assert.DoesNotThrow(identity.ResetState);
                Assert.That(identity.currentState.values.isDisposed, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void RepeatedEmptyDynamicModuleSnapshotsReuseSparseHistory()
        {
            var gameObject = new GameObject(nameof(RepeatedEmptyDynamicModuleSnapshotsReuseSparseHistory));
            try
            {
                var identity = gameObject.AddComponent<PolicyProbe>();
                var history = new History<DisposableList<uint>>(8);
                SetField(typeof(PredictedIdentity), identity, "_moduleHistory", history);
                SetField(typeof(PredictedIdentity), identity, "_staticModuleCount", 0);

                ApplyEmptyDynamicSnapshot(identity, 10);
                ApplyEmptyDynamicSnapshot(identity, 11);

                Assert.That(history.Count, Is.EqualTo(1));
                Assert.That(history.OldestTick, Is.EqualTo(10));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

#if UNITY_PHYSICS_3D
        [Test]
        public void ServerRelaySetupCapturesTheAuthoredRigidbodyModeBeforeForcingKinematic()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(ServerRelaySetupCapturesTheAuthoredRigidbodyModeBeforeForcingKinematic));

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                identityObject.AddComponent<PredictedTransform>();
                var rigidbody = identityObject.AddComponent<Rigidbody>();
                rigidbody.isKinematic = false;
                rigidbody.useGravity = true;
                var predicted = identityObject.AddComponent<PredictedRigidbody>();
                predicted.SetPredictionPolicyOverride(PredictionPolicy.ServerRelay);

                manager.RegisterInstance(identityObject, new PredictedObjectID(1), null, false, false);

                Assert.That(predicted.currentState.isKinematic, Is.False,
                    "Relay policy was applied before the authored rigidbody mode was captured");
                Assert.That(predicted.currentState.useGravity, Is.True);
                Assert.That(rigidbody.isKinematic, Is.True,
                    "The live client body should still be forced kinematic after setup completes");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void FullAndSoftPolicyTransitionsPreserveLiveRigidbodyMode()
        {
            var gameObject = new GameObject(nameof(FullAndSoftPolicyTransitionsPreserveLiveRigidbodyMode));
            try
            {
                gameObject.AddComponent<PredictedTransform>();
                var rigidbody = gameObject.AddComponent<Rigidbody>();
                var predicted = gameObject.AddComponent<PredictedRigidbody>();

                rigidbody.isKinematic = true;
                rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

                predicted.SetPredictionPolicy(PredictionPolicy.SoftCorrection);

                Assert.That(rigidbody.isKinematic, Is.True,
                    "FullPrediction -> SoftCorrection changed the live kinematic mode");
                Assert.That(rigidbody.collisionDetectionMode,
                    Is.EqualTo(CollisionDetectionMode.ContinuousSpeculative),
                    "FullPrediction -> SoftCorrection changed the live collision mode");

                rigidbody.isKinematic = true;
                rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

                predicted.SetPredictionPolicy(PredictionPolicy.FullPrediction);

                Assert.That(rigidbody.isKinematic, Is.True,
                    "SoftCorrection -> FullPrediction changed the live kinematic mode");
                Assert.That(rigidbody.collisionDetectionMode,
                    Is.EqualTo(CollisionDetectionMode.ContinuousSpeculative),
                    "SoftCorrection -> FullPrediction changed the live collision mode");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void LeavingServerRelayRestoresAuthoritativeRigidbodyState()
        {
            var gameObject = new GameObject(nameof(LeavingServerRelayRestoresAuthoritativeRigidbodyState));
            try
            {
                gameObject.AddComponent<PredictedTransform>();
                var rigidbody = gameObject.AddComponent<Rigidbody>();
                rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                var predicted = gameObject.AddComponent<PredictedRigidbody>();
                SetField(typeof(PredictedRigidbody), predicted,
                    "_defaultCollisionMode", CollisionDetectionMode.ContinuousDynamic);

                predicted.currentState.isKinematic = false;
                predicted.currentState.useGravity = false;
                predicted.currentState.linearVelocity = new Vector3(3f, 4f, 5f);
                predicted.currentState.angularVelocity = new Vector3(1f, 2f, 3f);

                predicted.SetPredictionPolicy(PredictionPolicy.ServerRelay);
                Assert.That(rigidbody.isKinematic, Is.True);

                predicted.SetPredictionPolicy(PredictionPolicy.FullPrediction);

                Assert.That(rigidbody.isKinematic, Is.False);
                Assert.That(rigidbody.useGravity, Is.False);
                Assert.That(rigidbody.linearVelocity, Is.EqualTo(new Vector3(3f, 4f, 5f)));
                Assert.That(rigidbody.angularVelocity, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(rigidbody.collisionDetectionMode,
                    Is.EqualTo(CollisionDetectionMode.ContinuousDynamic));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
#endif

#if UNITY_PHYSICS_2D
        [Test]
        public void ServerRelaySetupCapturesTheAuthoredRigidbody2DModeBeforeForcingKinematic()
        {
            var networkObject = new GameObject("NetworkManager");
            var managerObject = new GameObject("PredictionManager");
            var identityObject = new GameObject(nameof(ServerRelaySetupCapturesTheAuthoredRigidbody2DModeBeforeForcingKinematic));

            try
            {
                var networkManager = networkObject.AddComponent<NetworkManager>();
                var manager = CreateSpawnedPredictionManager(managerObject, networkManager);
                identityObject.AddComponent<PredictedTransform>();
                var rigidbody = identityObject.AddComponent<Rigidbody2D>();
                rigidbody.bodyType = RigidbodyType2D.Dynamic;
                var predicted = identityObject.AddComponent<PredictedRigidbody2D>();
                predicted.SetPredictionPolicyOverride(PredictionPolicy.ServerRelay);

                manager.RegisterInstance(identityObject, new PredictedObjectID(1), null, false, false);

                Assert.That((RigidbodyType2D)predicted.currentState.bodyType, Is.EqualTo(RigidbodyType2D.Dynamic),
                    "Relay policy was applied before the authored rigidbody mode was captured");
                Assert.That(rigidbody.bodyType, Is.EqualTo(RigidbodyType2D.Kinematic),
                    "The live client body should still be forced kinematic after setup completes");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(identityObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
                UnityEngine.Object.DestroyImmediate(networkObject);
            }
        }

        [Test]
        public void FullAndSoftPolicyTransitionsPreserveLiveRigidbody2DMode()
        {
            var gameObject = new GameObject(nameof(FullAndSoftPolicyTransitionsPreserveLiveRigidbody2DMode));
            try
            {
                gameObject.AddComponent<PredictedTransform>();
                var rigidbody = gameObject.AddComponent<Rigidbody2D>();
                var predicted = gameObject.AddComponent<PredictedRigidbody2D>();

                rigidbody.bodyType = RigidbodyType2D.Static;

                predicted.SetPredictionPolicy(PredictionPolicy.SoftCorrection);

                Assert.That(rigidbody.bodyType, Is.EqualTo(RigidbodyType2D.Static),
                    "FullPrediction -> SoftCorrection changed the live body type");

                rigidbody.bodyType = RigidbodyType2D.Kinematic;

                predicted.SetPredictionPolicy(PredictionPolicy.FullPrediction);

                Assert.That(rigidbody.bodyType, Is.EqualTo(RigidbodyType2D.Kinematic),
                    "SoftCorrection -> FullPrediction changed the live body type");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
#endif

        private static void ApplyEmptyDynamicSnapshot(PredictedIdentity identity, ulong tick)
        {
            var method = typeof(PredictedIdentity).GetMethod(
                "ApplyEmptyDynamicModuleSnapshot", InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(identity, new object[] { tick, false });
        }

        private static PredictionManager CreateSpawnedPredictionManager(
            GameObject managerObject,
            NetworkManager networkManager)
        {
            var tickManager = new TickManager(20, networkManager, null, false);
            SetField(typeof(NetworkManager), networkManager, "_clientTickManager", tickManager);

            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", networkManager);
            SetField(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 20);
            manager.SetIsSpawned(true, false);
            return manager;
        }

        private static History<TrackedInput> SeedInputStorage(Component identity, Type identityBaseType)
        {
            var history = new History<TrackedInput>(8);
            history.Write(1, new TrackedInput(1));
            history.Write(2, new TrackedInput(2));

            SetField(identityBaseType, identity, "_inputHistory", history);
            SetField(identityBaseType, identity, "_currentInput", new TrackedInput(3));
            SetField(identityBaseType, identity, "_nextInput", new TrackedInput(4));
            SetField(identityBaseType, identity, "_lastInput", (TrackedInput?)new TrackedInput(5));
            SetField(identityBaseType, identity, "_queuedInput", (TrackedInput?)new TrackedInput(6));
            return history;
        }

        private static void SetField(Type declaringType, object target, string fieldName, object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null, $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }

        private static void InvokeLifecycle(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, InstanceFields);
            Assert.That(method, Is.Not.Null, $"Missing lifecycle method {target.GetType().FullName}.{methodName}");
            method.Invoke(target, null);
        }

        private readonly struct TrackedDisposable : IDisposable
        {
            public static int disposeCount;
            public readonly int id;

            public TrackedDisposable(int id)
            {
                this.id = id;
            }

            public static void Reset()
            {
                disposeCount = 0;
                TrackedInput.disposeCount = 0;
            }

            public void Dispose()
            {
                if (id != 0)
                    disposeCount++;
            }
        }
    }

    public struct TrackedInput : IPredictedData
    {
        public static int disposeCount;
        public int id;

        public TrackedInput(int id)
        {
            this.id = id;
        }

        public void Dispose()
        {
            if (id != 0)
                disposeCount++;
        }
    }

    public struct EmptyState : IPredictedData<EmptyState>
    {
        public void Dispose() { }
    }

    public struct ModuleHistoryState : IPredictedData<ModuleHistoryState>
    {
        public int value;

        public void Dispose() { }
    }

    public sealed class ModuleHistoryProbe : PredictedModule<ModuleHistoryState>
    {
        public ModuleHistoryProbe(PredictedIdentity identity) : base(identity) { }
    }

    public sealed class SetupPolicyOverrideProbe : PredictedIdentity<EmptyState>
    {
        protected override PredictionPolicy ResolvePredictionPolicy() => PredictionPolicy.ServerRelay;

        protected override void Simulate(ref EmptyState state, float delta) { }
    }

    public sealed class StatefulInputProbe : PredictedIdentity<TrackedInput, EmptyState>
    {
        protected override void Simulate(TrackedInput input, ref EmptyState state, float delta) { }
    }

    public sealed class DeterministicInputProbe : DeterministicIdentity<TrackedInput, EmptyState>
    {
        protected override void Simulate(TrackedInput input, ref EmptyState state, sfloat delta) { }
    }

#if UNITY_PHYSICS_3D
    public sealed class ProjectileInterpolationProbe : PredictedProjectile3D
    {
        public ProjectileState3D InterpolateForTest(ProjectileState3D from, ProjectileState3D to, float t)
            => base.Interpolate(from, to, t);
    }
#endif

    public sealed class PolicyProbe : PredictedIdentity<EmptyState>
    {
        public override bool supportsSoftCorrection => true;

        public int policyTransitionCount { get; private set; }
        public PredictionPolicy lastOldPolicy { get; private set; }
        public PredictionPolicy lastNewPolicy { get; private set; }

        public void AttachForTest(PredictionManager manager)
        {
            predictionManager = manager;
            RefreshResolvedPredictionPolicy();
        }

        public void DetachForTest()
        {
            predictionManager = null;
        }

        public void RefreshParentScopeForTest()
        {
            base.OnTransformParentChanged();
        }

        public void ResetPolicyTransitions()
        {
            policyTransitionCount = 0;
            lastOldPolicy = default;
            lastNewPolicy = default;
        }

        protected override void OnPredictionPolicyChanged(
            PredictionPolicy oldPolicy,
            PredictionPolicy newPolicy)
        {
            base.OnPredictionPolicyChanged(oldPolicy, newPolicy);
            policyTransitionCount++;
            lastOldPolicy = oldPolicy;
            lastNewPolicy = newPolicy;
        }
    }

    public sealed class OwnershipPolicyScopeProbe : PredictionPolicyScope
    {
        protected override PredictionPolicy ResolveLocalPolicy(PredictedIdentity identity)
            => identity.IsOwner()
                ? PredictionPolicy.FullPrediction
                : PredictionPolicy.SoftCorrection;
    }

    public sealed class PolicySetupOrderProbe : PredictedIdentity<EmptyState>
    {
        public override bool supportsSoftCorrection => true;

        public int preSetupCount { get; private set; }
        public bool unityStateCaptured { get; private set; }
        public bool policyChangedBeforeUnityStateCapture { get; private set; }

        public override void OnPreSetup()
        {
            preSetupCount++;
        }

        protected override void GetUnityState(ref EmptyState state)
        {
            unityStateCaptured = true;
        }

        protected override void OnPredictionPolicyChanged(PredictionPolicy oldPolicy, PredictionPolicy newPolicy)
        {
            base.OnPredictionPolicyChanged(oldPolicy, newPolicy);
            if (!unityStateCaptured)
                policyChangedBeforeUnityStateCapture = true;
        }
    }

    public sealed class PolicyOwnerProbe : PredictedIdentity<EmptyState>
    {
        public override bool controlsTransformPolicy => true;

        public void AttachForTest(PredictionManager manager)
        {
            predictionManager = manager;
            RefreshResolvedPredictionPolicy();
        }

        public void DetachForTest()
        {
            predictionManager = null;
        }

        protected override void OnPredictionPolicyChanged(PredictionPolicy oldPolicy, PredictionPolicy newPolicy)
        {
            base.OnPredictionPolicyChanged(oldPolicy, newPolicy);
            SyncControlledTransformPolicy(predictionPolicy);
        }
    }

    public sealed class UnsupportedPolicyProbe : PredictedIdentity<EmptyState>
    {
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

    public sealed class PredictedTransformPolicyProbe : PredictedTransform
    {
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
}
