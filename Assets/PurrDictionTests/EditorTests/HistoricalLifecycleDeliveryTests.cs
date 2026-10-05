using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class HistoricalLifecycleDeliveryTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ulong Baseline = 10;
        private const ulong FinalTick = 17;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(LifecycleInput));
            Hasher.PrepareType(typeof(LifecycleState));
            Hasher.PrepareType(typeof(LifecycleLedgerState));
            Hasher.PrepareType(typeof(LifecycleInputProbe));
            Hasher.PrepareType(typeof(LifecycleLedger));
            Hasher.PrepareType(typeof(PredictedHierarchyState));
            Hasher.PrepareType(typeof(InstanceDetails));
            Packer<LifecycleInput>.RegisterWriter((p, v) => Packer<int>.Write(p, v.amount));
            Packer<LifecycleInput>.RegisterReader((BitPacker p, ref LifecycleInput v) =>
                v.amount = Packer<int>.Read(p));
            Packer<LifecycleState>.RegisterWriter((p, v) =>
            {
                Packer<int>.Write(p, v.value);
                Packer<int>.Write(p, v.starts);
                Packer<bool>.Write(p, v.suppressLedger);
            });
            Packer<LifecycleState>.RegisterReader((BitPacker p, ref LifecycleState v) =>
            {
                v.value = Packer<int>.Read(p);
                v.starts = Packer<int>.Read(p);
                v.suppressLedger = Packer<bool>.Read(p);
            });
            Packer<LifecycleLedgerState>.RegisterWriter((p, v) =>
            {
                Packer<long>.Write(p, v.sum);
                Packer<int>.Write(p, v.count);
            });
            Packer<LifecycleLedgerState>.RegisterReader((BitPacker p, ref LifecycleLedgerState v) =>
            {
                v.sum = Packer<long>.Read(p);
                v.count = Packer<int>.Read(p);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LifecycleComparisonUsesStatesWithoutFullTopologySerialization(bool filtered)
        {
            PredictedObjectID existing = default, born = default;
            using var f = new Fixture(true,
                beforeBaseline: world => existing = world.CreateServerIdentity(100));
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (filtered && tick == 11)
                    f.SetVisible(existing, false);
                if (tick == 13)
                    born = f.CreateServerIdentity(700);
                using var packet = f.StepServer(tick, assertNoLifecycleFullWrites: true);
                Assert.That(packet.full, Is.False);
                if (tick == FinalTick)
                {
                    f.AssertLifecycleRepeatsAndChangesWithoutFullWriter(filtered);
                    f.Deliver(packet);
                }
            }

            f.AssertVerifiedAgreement(compareObservations: !filtered);
            var observations = f.clientLedger.observations.FindAll(value => value.objectId.Equals(born));
            Assert.That(observations.Count, Is.EqualTo(5));
            Assert.That(observations[0], Is.EqualTo(new LifecycleObservation(born, 13, 700, 13, 1)));
            Assert.That(f.client.hierarchy.TryGetGameObject(born, out _), Is.True);
        }

        private static void WithoutFullTopologyWriter(Action action)
        {
            var writer = Packer<PredictedHierarchyState>.WriteFunc;
            var directWriter = Packer<PredictedHierarchyState>.DirectWrite;
            try
            {
                Packer<PredictedHierarchyState>.WriteFunc = (_, _) => throw new AssertionException(
                    "Lifecycle comparisons must not serialize full topology states.");
                Packer<PredictedHierarchyState>.DirectWrite = Packer<PredictedHierarchyState>.WriteFunc;
                action();
            }
            finally
            {
                Packer<PredictedHierarchyState>.WriteFunc = writer;
                Packer<PredictedHierarchyState>.DirectWrite = directWriter;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LostBirthFrameRestoresCustomEnteringStateAndOriginalTickInputs(bool pooled)
        {
            using var f = new Fixture(pooled);
            PredictedObjectID born = default;
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    born = f.CreateServerIdentity(700);
                using var packet = f.StepServer(tick);
                Assert.That(packet.full, Is.False, $"unexpected checkpoint at tick {tick}");
                if (tick == FinalTick)
                    f.Deliver(packet);
            }

            f.AssertVerifiedAgreement();
            Assert.That(f.clientLedger.observations.Count, Is.EqualTo(5));
            Assert.That(f.clientLedger.observations[0],
                Is.EqualTo(new LifecycleObservation(born, 13, 700, 13, 1)));
            Assert.That(f.clientLedger.materializations, Does.Contain((born, 13UL)),
                "LateAwake must observe the historical birth tick, not the current prediction head");
            for (var i = 0; i < 5; i++)
                Assert.That(f.clientLedger.observations[i].tick, Is.EqualTo(13UL + (ulong)i));
            Assert.That(f.client.hierarchy.TryGetGameObject(born, out var live), Is.True);
            Assert.That(live.GetComponent<LifecycleInputProbe>().id.objectId, Is.EqualTo(born));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BirthAndDeathBetweenDeliveredFramesStillRunTheCompleteHistoricalLifetime(bool pooled)
        {
            using var f = new Fixture(pooled);
            PredictedObjectID born = default;
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    born = f.CreateServerIdentity(700);
                if (tick == 15)
                    f.server.hierarchy.Delete(born);
                using var packet = f.StepServer(tick);
                Assert.That(packet.full, Is.False, $"normal lifetime forced checkpoint at tick {tick}");
                if (tick == FinalTick)
                    f.Deliver(packet);
            }

            f.AssertVerifiedAgreement();
            Assert.That(f.clientLedger.observations, Is.EqualTo(new[]
            {
                new LifecycleObservation(born, 13, 700, 13, 1),
                new LifecycleObservation(born, 14, 713, 14, 1)
            }));
            Assert.That(f.client.hierarchy.TryGetGameObject(born, out _), Is.False,
                "the historical object must be removed before the surviving final tick");
            Assert.That(f.clientLedger.currentState.count, Is.EqualTo(2),
                "the persistent deterministic consumer must retain the vanished object's effects");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoalescedUnsentFramesRetainAnEntireTransientLifetime(bool pooled)
        {
            using var f = new Fixture(pooled);
            PredictedObjectID born = default;
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    born = f.CreateServerIdentity(700);
                if (tick == 15)
                    f.server.hierarchy.Delete(born);
                using var packet = f.StepServer(tick, deferSend: true);
                Assert.That(packet.full, Is.False, $"coalescing forced a checkpoint at tick {tick}");
                Assert.That(f.PreparedFrame.sentVisibilityTick, Is.EqualTo(Baseline),
                    "preparing another frame must not mark any skipped visibility state as sent");
                if (tick == FinalTick)
                {
                    f.CompleteDeferredSend(packet);
                    f.Deliver(packet);
                }
            }

            f.AssertVerifiedAgreement();
            Assert.That(f.clientLedger.observations, Is.EqualTo(new[]
            {
                new LifecycleObservation(born, 13, 700, 13, 1),
                new LifecycleObservation(born, 14, 713, 14, 1)
            }));
            Assert.That(f.client.hierarchy.TryGetGameObject(born, out _), Is.False);
            Assert.That(f.clientLedger.currentState.count, Is.EqualTo(2));
        }

        [Test]
        public void CoalescedUnsentVisibilityTransitionsReplayOnlyOriginalVisibleIntervals()
        {
            PredictedObjectID id = default;
            using var f = new Fixture(true, beforeBaseline: world => id = world.CreateServerIdentity(100));
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 11 || tick == 13)
                {
                    f.SetVisible(id, false);
                    f.ServerProbe(id).currentState.value = tick == 11 ? 700 : 900;
                }
                if (tick == 12 || tick == 16)
                    f.SetVisible(id, true);
                using var packet = f.StepServer(tick, deferSend: true);
                Assert.That(packet.full, Is.False);
                Assert.That(f.PreparedFrame.sentVisibilityTick, Is.EqualTo(Baseline));
                if (tick == FinalTick)
                {
                    f.CompleteDeferredSend(packet);
                    f.Deliver(packet);
                }
            }

            f.AssertVerifiedAgreement(compareObservations: false);
            Assert.That(f.clientLedger.observations, Is.EqualTo(new[]
            {
                new LifecycleObservation(id, 12, 711, 12, 1),
                new LifecycleObservation(id, 16, 942, 16, 1),
                new LifecycleObservation(id, 17, 958, 17, 1)
            }));
            Assert.That(f.client.hierarchy.TryGetGameObject(id, out _), Is.True);
        }

        [Test]
        public void CoalescedUnsentDeleteCannotBeRetiredByAnEarlierAcknowledgement()
        {
            PredictedObjectID id = default;
            using var f = new Fixture(true, beforeBaseline: world => id = world.CreateServerIdentity(100));
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    f.server.hierarchy.Delete(id);
                using var packet = f.StepServer(tick, deferSend: true);
                Assert.That(packet.full, Is.False);
                if (tick >= 13)
                {
                    var tombstones = f.PendingVisibilityDeletes;
                    Assert.That(tombstones, Is.Not.Null);
                    Assert.That(tombstones.Count, Is.EqualTo(1));
                    tombstones.Acknowledge(tick - 1);
                    Assert.That(tombstones.Count, Is.EqualTo(1),
                        "an unsent tombstone has no delivery tick that an ACK can retire");
                }
                if (tick == FinalTick)
                {
                    f.CompleteDeferredSend(packet);
                    f.Deliver(packet);
                    f.PendingVisibilityDeletes.Acknowledge(FinalTick - 1);
                    Assert.That(f.PendingVisibilityDeletes.Count, Is.EqualTo(1));
                    f.PendingVisibilityDeletes.Acknowledge(FinalTick);
                    Assert.That(f.PendingVisibilityDeletes.Count, Is.Zero);
                }
            }

            f.AssertVerifiedAgreement();
            Assert.That(f.client.hierarchy.TryGetGameObject(id, out _), Is.False);
            Assert.That(f.clientLedger.observations.Count, Is.EqualTo(2));
        }

        [Test]
        public void SpawnedAfterTheBaselineIsDeliveredAsADeltaAgainstItsEnteringState()
        {
            using var f = new Fixture(true);
            PredictedObjectID born = default;
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    born = f.CreateServerIdentity(700);
                using var packet = f.StepServer(tick);
                Assert.That(packet.full, Is.False);
                if (tick == FinalTick)
                    f.Deliver(packet);
            }

            // Tick 13 writes the newborn in full; frames 14..17 delta it against its entering state.
            Assert.That(f.server.spawnBaselineRecordsTotal, Is.EqualTo(4));
            f.AssertVerifiedAgreement();
            Assert.That(f.client.hierarchy.TryGetGameObject(born, out _), Is.True);
        }

        [Test]
        public void ReplacingUnsentFramesKeepsTheDesyncHealPendingUntilDelivery()
        {
            PredictedObjectID id = default;
            using var f = new Fixture(true, beforeBaseline: world => id = world.CreateServerIdentity(100));
            var component = f.ServerProbe(id).id;
            f.QueueDesyncHeal(component);
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                using var packet = f.StepServer(tick, deferSend: true);
                Assert.That(packet.full, Is.False, "an identity heal must remain an ordinary continuation");
                Assert.That(f.PendingDesyncHeals, Does.Contain(component),
                    "preparing or coalescing a heal must not consume the pending request");
                Assert.That(f.PreparedDesyncHeals, Does.Contain(component));
                if (tick == FinalTick)
                {
                    f.CompleteDeferredSend(packet);
                    Assert.That(f.PendingDesyncHeals, Is.Empty);
                    Assert.That(f.PreparedDesyncHeals, Is.Empty);
                    f.Deliver(packet);
                }
            }

            f.AssertVerifiedAgreement();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LaterBirthKeepsDistinctIdsAndInitializationAfterAnEarlierLifetimeEnds(bool pooled)
        {
            using var f = new Fixture(pooled);
            PredictedObjectID first = default;
            PredictedObjectID second = default;
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    first = f.CreateServerIdentity(700);
                if (tick == 15)
                    f.server.hierarchy.Delete(first);
                if (tick == 16)
                    second = f.CreateServerIdentity(900);
                using var packet = f.StepServer(tick);
                Assert.That(packet.full, Is.False, $"pool/lifetime transition forced checkpoint at tick {tick}");
                if (tick == FinalTick)
                    f.Deliver(packet);
            }

            f.AssertVerifiedAgreement();
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(f.clientLedger.observations, Is.EqualTo(new[]
            {
                new LifecycleObservation(first, 13, 700, 13, 1),
                new LifecycleObservation(first, 14, 713, 14, 1),
                new LifecycleObservation(second, 16, 900, 16, 1),
                new LifecycleObservation(second, 17, 916, 17, 1)
            }));
            Assert.That(f.client.hierarchy.TryGetGameObject(first, out _), Is.False);
            Assert.That(f.client.hierarchy.TryGetGameObject(second, out var live), Is.True);
            Assert.That(live.GetComponent<LifecycleInputProbe>().id.objectId, Is.EqualTo(second));
        }

        [Test]
        public void UnchangedAcknowledgedTickUsesItsSparseLogicalBaselineForLaterLifetimes()
        {
            using var f = new Fixture(true);
            for (ulong tick = 11; tick <= 12; tick++)
            {
                using var packet = f.StepServer(tick);
                f.Deliver(packet);
                f.AcknowledgeClient();
            }
            Assert.That(f.client.hierarchy.TryGetExactVerifiedState(12, out _), Is.False,
                "unchanged hierarchy history must remain sparse so this test exercises a logical ACK baseline");
            Assert.That(f.client.hierarchy.TryGetVerifiedState(12, out _, out _), Is.True);

            PredictedObjectID first = default, second = default;
            for (ulong tick = 13; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    first = f.CreateServerIdentity(700);
                if (tick == 15)
                    f.server.hierarchy.Delete(first);
                if (tick == 16)
                    second = f.CreateServerIdentity(900);
                using var packet = f.StepServer(tick);
                Assert.That(packet.full, Is.False);
                Assert.That(packet.baseline, Is.EqualTo(12));
                if (tick == FinalTick)
                    f.Deliver(packet);
            }

            f.AssertVerifiedAgreement();
            Assert.That(f.clientLedger.observations, Is.EqualTo(new[]
            {
                new LifecycleObservation(first, 13, 700, 13, 1),
                new LifecycleObservation(first, 14, 713, 14, 1),
                new LifecycleObservation(second, 16, 900, 16, 1),
                new LifecycleObservation(second, 17, 916, 17, 1)
            }));
            Assert.That(f.client.hierarchy.TryGetGameObject(first, out _), Is.False);
            Assert.That(f.client.hierarchy.TryGetGameObject(second, out _), Is.True);
        }

        [TestCase("signedZeroPosition")]
        [TestCase("signedZeroRotation")]
        [TestCase("ownerBot")]
        public void AcknowledgedTopologyReferencePreservesExactRecordsDuringGapReplay(string change)
        {
            PredictedObjectID existing = default;
            using var f = new Fixture(true, beforeBaseline: world =>
            {
                var created = world.server.hierarchy.Create(0, new Vector3(5, 0, 0),
                    Quaternion.identity, new PlayerID(7, false));
                Assert.That(created.HasValue, Is.True);
                existing = created.Value;
                world.ServerProbe(existing).currentState.value = 700;
            });

            // Exercise the real hierarchy restore/capture/store/wire paths. These are
            // valid serialized record changes that numeric/PlayerID equality coalesces;
            // this is an exact-reference edge case, not a normal gameplay-motion claim.
            var restored = f.server.hierarchy.currentState.Duplicate();
            try
            {
                int index = restored.spawnedPrefabs.list.FindIndex(r => r.instanceId.Equals(existing));
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                var prior = restored.spawnedPrefabs[index];
                var position = prior.spawnPosition;
                var rotation = prior.spawnRotation;
                var owner = prior.owner;
                float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
                switch (change)
                {
                    case "signedZeroPosition": position.y = negativeZero; break;
                    case "signedZeroRotation": rotation.x = negativeZero; break;
                    case "ownerBot": owner = new PlayerID(7, true); break;
                    default: throw new ArgumentOutOfRangeException(nameof(change));
                }
                restored.spawnedPrefabs[index] = new InstanceDetails(prior.prefabId, prior.pieceIndex,
                    prior.instanceId, position, rotation, owner, prior.parent);
                f.server.hierarchy.ApplyHistoricalTopology(restored);
            }
            finally { restored.Dispose(); }

            using (var acknowledged = f.StepServer(11))
            {
                Assert.That(acknowledged.full, Is.False);
                f.Deliver(acknowledged);
                f.AcknowledgeClient();
            }

            var authoritative = new Dictionary<ulong, int>();
            var replayed = new Dictionary<ulong, int>();
            void Observe(PredictionManager world, Dictionary<ulong, int> values)
            {
                if (!world.cachedIsServer && !world.isVerifiedAndReplaying)
                    return;
                var record = world.hierarchy.currentState.spawnedPrefabs.list.Find(r => r.instanceId.Equals(existing));
                values[world.localTickInContext] = change == "ownerBot"
                    ? (record.owner.GetValueOrDefault().isBot ? 1 : 0)
                    : BitConverter.SingleToInt32Bits(change == "signedZeroPosition" ? record.spawnPosition.y : record.spawnRotation.x);
            }
            Action observeServer = () => Observe(f.server, authoritative);
            Action observeClient = () => Observe(f.client, replayed);
            f.server.onBeforePhysicsPass += observeServer;
            f.client.onBeforePhysicsPass += observeClient;
            try
            {
                for (ulong tick = 12; tick <= FinalTick; tick++)
                {
                    if (tick == 13)
                        f.CreateServerIdentity(900);
                    using var packet = f.StepServer(tick);
                    Assert.That(packet.full, Is.False);
                    Assert.That(packet.baseline, Is.EqualTo(11));
                    if (tick == FinalTick)
                        f.Deliver(packet);
                }
                Assert.That(Get<ulong>(f.client, "_verifiedServerTick"), Is.EqualTo(FinalTick));
                for (ulong tick = 12; tick < FinalTick; tick++)
                {
                    Assert.That(authoritative.ContainsKey(tick), Is.True, $"server did not simulate {tick}");
                    Assert.That(replayed.ContainsKey(tick), Is.True, $"client did not replay {tick}");
                    Assert.That(authoritative[tick], Is.EqualTo(change == "ownerBot" ? 1 : unchecked((int)0x80000000)),
                        "the exact changed record must survive authoritative capture");
                    Assert.That(replayed[tick], Is.EqualTo(authoritative[tick]),
                        $"the ACK-baseline reference substituted numerically equal record bits at tick {tick}");
                }
            }
            finally
            {
                f.server.onBeforePhysicsPass -= observeServer;
                f.client.onBeforePhysicsPass -= observeClient;
            }
        }

        [Test]
        public void MissingLaterEnteringStateStallsAckAndNeverDispatchesEarlierCallbacksTwice()
        {
            using var f = new Fixture(true);
            PredictedObjectID first = default;
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                    first = f.CreateServerIdentity(700);
                if (tick == 15)
                    f.server.hierarchy.Delete(first);
                if (tick == 16)
                    f.CreateServerIdentity(900);
                using var packet = f.StepServer(tick);
                if (tick != FinalTick)
                    continue;

                using var incomplete = f.WithoutEnteringStates(packet, 16);
                LogAssert.Expect(LogType.Error, new Regex(
                    "Cannot apply prediction frame 17: Missing entering state for recreated.*at tick 16"));
                f.Deliver(incomplete);
                Assert.That(f.clientLedger.observations, Is.EqualTo(new[]
                {
                    new LifecycleObservation(first, 13, 700, 13, 1),
                    new LifecycleObservation(first, 14, 713, 14, 1)
                }), "earlier valid historical simulations ran before the later missing entrant was discovered");
                Assert.That(Get<ulong>(f.client, "_verifiedServerTick"), Is.EqualTo(Baseline));
                Assert.That(Get<ulong>(f.client, "_ackedServerTick"), Is.EqualTo(Baseline));
                Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.True);
                Assert.That(Get<bool>(f.client, "_awaitingFullFrame"), Is.True);

                // A fresh intact continuation cannot replay those first-delivery callbacks
                // again after part of the previous frame already reached gameplay code.
                f.Deliver(packet);
                Assert.That(f.clientLedger.observations.Count, Is.EqualTo(2));
                Assert.That(Get<ulong>(f.client, "_ackedServerTick"), Is.EqualTo(Baseline));
                Assert.That(Get<ulong>(f.client, "_verifiedServerTick"), Is.EqualTo(Baseline));
            }
        }

        [Test]
        public void RepeatedVisibilityEntriesRestoreTheirOriginalEnteringStateAndInputs()
        {
            PredictedObjectID id = default;
            using var f = new Fixture(true, beforeBaseline: world => id = world.CreateServerIdentity(100));
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 11 || tick == 13)
                {
                    f.SetVisible(id, false);
                    f.ServerProbe(id).currentState.value = tick == 11 ? 700 : 900;
                }
                if (tick == 12 || tick == 16)
                    f.SetVisible(id, true);
                using var packet = f.StepServer(tick);
                Assert.That(packet.full, Is.False);
                if (tick == FinalTick)
                    f.Deliver(packet);
            }

            f.AssertVerifiedAgreement(compareObservations: false);
            Assert.That(f.clientLedger.observations, Is.EqualTo(new[]
            {
                new LifecycleObservation(id, 12, 711, 12, 1),
                new LifecycleObservation(id, 16, 942, 16, 1),
                new LifecycleObservation(id, 17, 958, 17, 1)
            }), "hidden intervals must not run client callbacks; each visible entry restores its original state");
            Assert.That(f.client.hierarchy.TryGetGameObject(id, out _), Is.True);
        }

        [Test]
        public void LostRuntimeReparentKeepsIdentityStateAndParentAtEachOriginalTick()
        {
            PredictedObjectID firstParent = default, secondParent = default, child = default;
            using var f = new Fixture(true, parented: true, beforeBaseline: world =>
            {
                firstParent = world.CreateServerIdentity(1000);
                secondParent = world.CreateServerIdentity(2000);
                child = world.CreateServerIdentity(700, firstParent);
            });
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                if (tick == 13)
                {
                    f.ServerProbe(child).transform.SetParent(f.ServerProbe(secondParent).transform, true);
                    var parent = f.ServerProbe(child).GetComponent<PredictedParent>();
                    parent.RunGetLatestUnityState();
                    TestContext.WriteLine($"EditMode reparent before callback: transform={secondParent}, captured={parent.currentState.parent}");
                    // EditMode does not guarantee Unity message delivery for this ordinary
                    // MonoBehaviour. Drive the same callback that SetParent sends in play.
                    typeof(PredictedParent).GetMethod("OnTransformParentChanged", Fields).Invoke(parent, null);
                }
                using var packet = f.StepServer(tick, tick == 13 ? () =>
                    Assert.That(f.ServerProbe(child).GetComponent<PredictedParent>().currentState.parent,
                        Is.EqualTo(new PredictedComponentID(secondParent, 0)),
                        "the server must capture the new parent before writing the lost frame") : null);
                Assert.That(packet.full, Is.False);
                if (tick == FinalTick)
                    f.Deliver(packet);
            }

            f.AssertVerifiedAgreement();
            var childObservations = f.clientLedger.observations.FindAll(o => o.objectId.Equals(child));
            Assert.That(childObservations.Count, Is.EqualTo(7));
            foreach (var observed in childObservations)
            {
                Assert.That(observed.parent, Is.EqualTo(observed.tick < 13 ? firstParent : secondParent),
                    $"callback at tick {observed.tick} must see its historical attachment");
                Assert.That(observed.starts, Is.EqualTo(1), "reparenting must not restart the existing identity");
            }
            Assert.That(childObservations[2].enteringValue, Is.EqualTo(733));
            Assert.That(f.client.hierarchy.TryGetGameObject(child, out var childObject), Is.True);
            Assert.That(f.client.hierarchy.TryGetGameObject(secondParent, out var parentObject), Is.True);
            Assert.That(childObject.transform.parent, Is.EqualTo(parentObject.transform));
        }

        [Test]
        public void MissingLatestHierarchyCannotVerifyDespiteRetainedLifecycleBaseline()
        {
            using var f = new Fixture(false);
            for (ulong tick = 11; tick <= FinalTick; tick++)
            {
                using var packet = f.StepServer(tick);
                if (tick != FinalTick)
                    continue;
                using var incomplete = f.WithoutLatestHierarchy(packet);
                LogAssert.Expect(LogType.Error, new Regex(
                    "Cannot apply prediction frame 17: Required authoritative hierarchy is missing or unexpected"));
                f.Deliver(incomplete);
                Assert.That(Get<ulong>(f.client, "_ackedServerTick"), Is.EqualTo(Baseline));
                Assert.That(Get<ulong>(f.client, "_verifiedServerTick"), Is.EqualTo(Baseline));
                Assert.That(Get<bool>(f.client, "_historyResyncPending"), Is.True);
                Assert.That(f.clientLedger.observations, Is.Empty);
            }
        }

        private sealed class Packet : IDisposable
        {
            internal readonly ulong tick, baseline;
            internal readonly bool full;
            internal readonly BitPacker payload;
            internal Packet(ulong tick, PlayerPacker prepared)
            {
                this.tick = tick;
                baseline = prepared.preparedBaselineTick;
                full = prepared.fullFrame;
                payload = Copy(prepared.packer);
            }
            internal Packet(Packet original, BitPacker replacement)
            {
                tick = original.tick;
                baseline = original.baseline;
                full = original.full;
                payload = replacement;
            }
            public void Dispose() => payload.Dispose();
        }

        private sealed class Fixture : IDisposable
        {
            private readonly List<GameObject> _objects = new();
            private readonly List<NetworkManager> _networks = new();
            private readonly PredictedPrefabs _prefabs;
            private readonly List<PlayerPacker> _frames;
            private readonly PredictionManager.InputQueue _ack;
            private readonly PlayerID _recipient = new(7, false);
            private readonly double _previousCadence;
            internal readonly PredictionManager server, client;
            internal readonly LifecycleLedger serverLedger, clientLedger;

            internal Fixture(bool pooled, bool parented = false, Action<Fixture> beforeBaseline = null)
            {
                _previousCadence = PredictionPerformanceTelemetry.reconcileIntervalSeconds;
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = 0;
                server = CreateWorld("Lifecycle server", true);
                client = CreateWorld("Lifecycle client", false);
                serverLedger = server.RegisterSystem<LifecycleLedger>();
                clientLedger = client.RegisterSystem<LifecycleLedger>();
                var prefab = NewObject("Historical lifecycle prefab");
                prefab.AddComponent<LifecycleInputProbe>();
                if (parented)
                    prefab.AddComponent<PredictedParent>();
                _prefabs = ScriptableObject.CreateInstance<PredictedPrefabs>();
                _prefabs.prefabs.Add(new PredictedPrefab { prefab = prefab, pooled = pooled, warmupCount = 0 });
                server.predictedPrefabs = _prefabs;
                client.predictedPrefabs = _prefabs;

                _frames = Get<List<PlayerPacker>>(server, "_clientFrames");
                _frames.Add(new PlayerPacker
                {
                    player = _recipient, packer = BitPackerPool.Get(),
                    requiresFullCheckpoint = true
                });
                _ack = new PredictionManager.InputQueue();
                Get<Dictionary<PlayerID, PredictionManager.InputQueue>>(server, "_clientTicks").Add(_recipient, _ack);
                beforeBaseline?.Invoke(this);
                using var baseline = StepServer(Baseline);
                Assert.That(baseline.full, Is.True);
                Deliver(baseline);
                Assert.That(Get<ulong>(client, "_verifiedServerTick"), Is.EqualTo(Baseline));
                _ack.ackedServerTick = Baseline;
                serverLedger.observations.Clear();
                clientLedger.observations.Clear();
                serverLedger.materializations.Clear();
                clientLedger.materializations.Clear();
            }

            internal PredictedObjectID CreateServerIdentity(int customValue, PredictedObjectID? parent = null)
            {
                var id = parent.HasValue
                    ? server.hierarchy.Create(0, new Vector3(5, 0, 0), Quaternion.identity,
                        new PredictedComponentID(parent.Value, 0))
                    : server.hierarchy.Create(0, new Vector3(5, 0, 0), Quaternion.identity);
                Assert.That(id.HasValue, Is.True);
                Assert.That(server.hierarchy.TryGetGameObject(id, out var instance), Is.True);
                instance.GetComponent<LifecycleInputProbe>().currentState.value = customValue;
                return id.Value;
            }

            internal LifecycleInputProbe ServerProbe(PredictedObjectID id)
            {
                Assert.That(server.hierarchy.TryGetGameObject(id, out var instance), Is.True);
                return instance.GetComponent<LifecycleInputProbe>();
            }

            internal void SetVisible(PredictedObjectID id, bool visible)
            {
                // Hidden objects do not contribute effects to this recipient's persistent
                // ledger; their own simulation and custom state continue on the server.
                ServerProbe(id).currentState.suppressLedger = !visible;
                Assert.That(visible ? server.ShowTo(_recipient, id) : server.HideFrom(_recipient, id), Is.True);
            }

            internal PlayerPacker PreparedFrame => _frames[0];
            internal PlayerPendingVisibilityDeletes PendingVisibilityDeletes =>
                Get<Dictionary<PlayerID, PlayerPendingVisibilityDeletes>>(server, "_pendingVisibilityDeletes")
                    .TryGetValue(_recipient, out var pending) ? pending : null;
            internal HashSet<PredictedComponentID> PendingDesyncHeals =>
                Get<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(server, "_pendingDesyncHeals")[_recipient];
            internal HashSet<PredictedComponentID> PreparedDesyncHeals =>
                Get<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(server, "_preparedDesyncHeals")[_recipient];

            internal void QueueDesyncHeal(PredictedComponentID id)
                => Get<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(server, "_pendingDesyncHeals")
                    .Add(_recipient, new HashSet<PredictedComponentID> { id });

            internal Packet StepServer(ulong tick, Action afterCapture = null, bool assertNoLifecycleFullWrites = false,
                bool deferSend = false)
            {
                Set(server, "<localTick>k__BackingField", tick);
                Set(server, "<localTickInContext>k__BackingField", tick);
                Set(server, "<isVerified>k__BackingField", true);
                var systems = Get<List<PredictedIdentity>>(server, "_systems");
                // These are the real OnPreTick stages, split only at the transport seam:
                // no socket/RPC is needed to retain, lose, and deliver serialized frames.
                for (var i = 0; i < systems.Count; i++)
                {
                    systems[i].RunGetLatestUnityState();
                    systems[i].PrepareInput(true, true, tick, false);
                }
                Invoke(server, "SaveEnteringState", tick);
                Invoke(server, "CaptureInputHistory", tick);
                if (assertNoLifecycleFullWrites)
                    WithoutFullTopologyWriter(() => Invoke(server, "CaptureLifecycleHistory", tick));
                else
                    Invoke(server, "CaptureLifecycleHistory", tick);
                afterCapture?.Invoke();
                Invoke(server, "CapturePhysicsEventHierarchy", tick);
                Invoke(server, "WriteInitialFrameToOthers");
                Assert.That(_frames[0].preparedFrameTick, Is.EqualTo(tick));

                var mode = Enum.Parse(typeof(PredictionManager).GetNestedType("HistorySaveMode", BindingFlags.NonPublic), "None");
                Invoke(server, "SimulateFrame", tick, mode, PredictionPassKind.Forward);
                for (var i = 0; i < systems.Count; i++)
                    systems[i].lastVerifiedTick = tick;
                Invoke(server, "CapturePhysicsEventState", tick);
                Invoke(server, "WriteEventHandles");
                var prepared = _frames[0];
                var packet = new Packet(tick, prepared);

                if (deferSend)
                {
                    Assert.That(prepared.fullFrame, Is.False,
                        "this transport seam only defers ordinary updates; full frames bypass coalescing");
                    Invoke(server, "DispatchPreparedServerFrames");
                    Assert.That(_frames[0].preparedFrameTick, Is.EqualTo(tick),
                        "per-tick dispatch must defer this update until the host's final flush");
                }
                // Frames share one ordered reliable stream, so a frame is sent exactly when it is delivered;
                // a stepped tick that is never delivered was coalesced into the next frame that is.
                Set(server, "<isVerified>k__BackingField", false);
                return packet;
            }

            internal void CompleteDeferredSend(Packet packet)
            {
                // Keep the existing explicit transport seam: dispatch/capture/replay are real,
                // while the final copied packet replaces a successful socket/RPC handoff.
                var prepared = _frames[0];
                ulong tick = packet.tick;
                Assert.That(prepared.preparedFrameTick, Is.EqualTo(tick));
                var visibility = Get<Dictionary<PlayerID, PlayerVisibilityTimeline>>(server, "_playerVisibility")[_recipient];
                Invoke(server, "HandleVisibilityFrameSent", _recipient, visibility, tick);
                Invoke(server, "MarkPendingVisibilityDeletesSent", _recipient, tick);
                Invoke(server, "CommitPreparedDesyncHeals", _recipient);
                if (prepared.fullFrame)
                    prepared.BeginFullFrame(tick);
                prepared.fullFrame = false;
                prepared.sentVisibilityTick = tick;
                prepared.lastSentFrameTick = tick;
                prepared.preparedVisibilityTick = 0;
                prepared.preparedFrameTick = 0;
                _frames[0] = prepared;
            }

            internal void AssertLifecycleRepeatsAndChangesWithoutFullWriter(bool filtered)
            {
                var timeline = Get<Dictionary<PlayerID, PlayerVisibilityTimeline>>(server, "_playerVisibility")[_recipient];
                Assert.That(timeline.isPassThrough, Is.EqualTo(!filtered));
                using var transcript = BitPackerPool.Get();
                WithoutFullTopologyWriter(() => Invoke(server, "WriteLifecycleHistory", transcript, Baseline, timeline, Baseline + 1));
                int end = transcript.positionInBits;
                transcript.ResetPositionAndMode(true);
                Assert.That(Packer<bool>.Read(transcript), Is.True);
                uint ticks = Packer<PackedUInt>.Read(transcript);
                Assert.That(ticks, Is.EqualTo(FinalTick - Baseline - 1));
                int repeats = 0, changes = 0;
                for (uint i = 0; i < ticks; i++)
                {
                    if (Packer<bool>.Read(transcript))
                        repeats++;
                    else
                    {
                        changes++;
                        uint bits = Packer<PackedUInt>.Read(transcript);
                        transcript.SkipBits((int)bits);
                    }
                    AddressedPredictionRecords.SkipSection(transcript, end);
                }
                Assert.That(repeats, Is.GreaterThan(0), "Unchanged topology must still use repeat flags.");
                Assert.That(changes, Is.GreaterThan(0), "Changed topology must still emit replayable patches.");
                Assert.That(transcript.positionInBits, Is.EqualTo(end));
            }

            internal void Deliver(Packet packet)
            {
                if (_frames[0].preparedFrameTick == packet.tick)
                    CompleteDeferredSend(packet);
                // Preserve the real repair/fence behavior while keeping its outbound RPC
                // at the same explicit transport seam as the frame packet handoff.
                Set(client, "_nextHistoryResyncRequestAt", double.PositiveInfinity);
                var copy = Copy(packet.payload);
                try
                {
                    int bytes = copy.ToByteData().length;
                    copy.ResetPositionAndMode(true);
                    Invoke(client, "HandleFrameFromServer", packet.tick, packet.baseline,
                        packet.tick, packet.full, false, default(PackedInt), false, default(PackedInt),
                        new BitPackerWithLength(bytes, copy));
                }
                catch { copy.Dispose(); throw; }
                Invoke(client, "ProcessQueuedFrames", true);
            }

            internal void AcknowledgeClient()
                => _ack.ackedServerTick = Get<ulong>(client, "_ackedServerTick");

            internal Packet WithoutLatestHierarchy(Packet packet)
            {
                using var reader = Copy(packet.payload);
                int end = reader.positionInBits;
                reader.ResetPositionAndMode(true);
                uint deletes = Packer<PackedUInt>.Read(reader);
                for (uint i = 0; i < deletes; i++)
                    Packer<PredictedObjectID>.Read(reader);
                int start = reader.positionInBits;
                Assert.That(Packer<bool>.Read(reader), Is.True);
                AddressedPredictionRecords.ReadOne(null, reader);
                int after = reader.positionInBits;
                Invoke(client, "ReadInputHistory", reader, packet.tick, packet.baseline, end);
                Invoke(client, "ClearVerifiedInputTranscript");
                Assert.That(Packer<bool>.Read(reader), Is.True);
                Assert.That(Packer<PackedUInt>.Read(reader).value, Is.GreaterThan(0));
                Assert.That(Packer<bool>.Read(reader), Is.True,
                    "the real stable-roster transcript must repeat its acknowledged hierarchy baseline first");
                var replacement = BitPackerPool.Get();
                replacement.WriteBitDataWithoutConsumingIt(new BitData(packet.payload, 0, start));
                Packer<bool>.Write(replacement, false);
                replacement.WriteBitDataWithoutConsumingIt(new BitData(packet.payload, after, end - after));
                return new Packet(packet, replacement);
            }

            internal Packet WithoutEnteringStates(Packet packet, ulong targetTick)
            {
                using var reader = Copy(packet.payload);
                int end = reader.positionInBits;
                reader.ResetPositionAndMode(true);
                uint deletes = Packer<PackedUInt>.Read(reader);
                for (uint i = 0; i < deletes; i++)
                    Packer<PredictedObjectID>.Read(reader);
                Assert.That(Packer<bool>.Read(reader), Is.True);
                AddressedPredictionRecords.ReadOne(null, reader);
                Invoke(client, "ReadInputHistory", reader, packet.tick, packet.baseline, end);
                Invoke(client, "ClearVerifiedInputTranscript");
                Assert.That(Packer<bool>.Read(reader), Is.True);
                uint ticks = Packer<PackedUInt>.Read(reader);
                for (uint i = 0; i < ticks; i++)
                {
                    if (!Packer<bool>.Read(reader))
                    {
                        uint topologyBits = Packer<PackedUInt>.Read(reader);
                        reader.SkipBits((int)topologyBits);
                    }
                    int start = reader.positionInBits;
                    uint entries = Packer<PackedUInt>.Read(reader);
                    reader.SetBitPosition(start);
                    AddressedPredictionRecords.SkipSection(reader, end);
                    if (packet.baseline + 1 + i != targetTick)
                        continue;
                    Assert.That(entries, Is.GreaterThan(0), "the real frame must contain initialization to remove");
                    int after = reader.positionInBits;
                    var replacement = BitPackerPool.Get();
                    replacement.WriteBitDataWithoutConsumingIt(new BitData(packet.payload, 0, start));
                    AddressedPredictionRecords.WriteSectionCount(0, replacement);
                    replacement.WriteBitDataWithoutConsumingIt(new BitData(packet.payload, after, end - after));
                    return new Packet(packet, replacement);
                }
                throw new AssertionException($"Missing lifecycle tick {targetTick} in frame {packet.tick}.");
            }

            internal void AssertVerifiedAgreement(bool compareObservations = true)
            {
                Assert.That(Get<ulong>(client, "_verifiedServerTick"), Is.EqualTo(FinalTick));
                Assert.That(Get<ulong>(client, "_ackedServerTick"), Is.EqualTo(FinalTick));
                Assert.That(Get<bool>(client, "_historyResyncPending"), Is.False);
                Assert.That(Get<bool>(client, "_awaitingFullFrame"), Is.False);
                Assert.That(_frames[0].lastFullFrameSentTick, Is.LessThanOrEqualTo(Baseline),
                    "the lifecycle history must carry the client across the gap without a full frame");
                Assert.That(_frames[0].requiresFullCheckpoint, Is.False);
                if (compareObservations)
                    Assert.That(clientLedger.observations, Is.EqualTo(serverLedger.observations),
                        "the verified client must replay every original simulation with its entering state and input");
                var history = (History<FULL_STATE<LifecycleLedgerState>>)Field(
                    typeof(DeterministicIdentity<LifecycleLedgerState>), "_stateHistory").GetValue(clientLedger);
                Assert.That(history.TryGet(FinalTick + 1, out var verified), Is.True);
                Assert.That(verified.state.sum, Is.EqualTo(serverLedger.currentState.sum));
                Assert.That(verified.state.count, Is.EqualTo(serverLedger.currentState.count));
                Assert.That(client.hierarchy.currentState.nextInstanceId,
                    Is.EqualTo(server.hierarchy.currentState.nextInstanceId),
                    "the ID allocator must include lifetimes absent from both delivered endpoints");
            }

            private PredictionManager CreateWorld(string name, bool asServer)
            {
                var network = NewObject(name + " network").AddComponent<NetworkManager>();
                _networks.Add(network);
                Set(typeof(NetworkManager), network, asServer ? "<isServer>k__BackingField" : "<isClient>k__BackingField", true);
                var ticks = new TickManager(20, network, null, asServer);
                Set(typeof(NetworkManager), network, asServer ? "_serverTickManager" : "_clientTickManager", ticks);
                var manager = NewObject(name).AddComponent<PredictionManager>();
                Set(typeof(NetworkIdentity), manager, "<networkManager>k__BackingField", network);
                manager.SetIsSpawned(true, asServer);
                Set(manager, "<cachedIsServer>k__BackingField", asServer);
                Set(manager, "<tickRate>k__BackingField", 20);
                Set(manager, "<tickDelta>k__BackingField", 1f / 20);
                Set(manager, "<localTick>k__BackingField", 20UL);
                Set(manager, "<localTickInContext>k__BackingField", 20UL);
                Set(manager, "_physicsProvider", default(PredictionPhysicsProvider));
                Set(manager, "_updateViewMode", UpdateViewMode.None);
                var hierarchy = manager.RegisterSystem<PredictedHierarchy>();
                Set(manager, "<hierarchy>k__BackingField", hierarchy);
                return manager;
            }

            private GameObject NewObject(string name)
            {
                var go = new GameObject(name);
                _objects.Add(go);
                return go;
            }

            public void Dispose()
            {
                PredictionPerformanceTelemetry.reconcileIntervalSeconds = _previousCadence;
                foreach (var manager in new[] { client, server })
                {
                    if (!manager)
                        continue;
                    if (manager.hierarchy)
                        manager.hierarchy.Cleanup();
                    var systems = Get<List<PredictedIdentity>>(manager, "_systems");
                    foreach (var identity in systems)
                        identity.ReleasePredictionStateForPool();
                    systems.Clear();
                    Get<Dictionary<PredictedComponentID, PredictedIdentity>>(manager, "_instanceMap").Clear();
                    Set(manager, "_systemsCount", 0);
                    Set(manager, "<hierarchy>k__BackingField", null);
                    Invoke(manager, "CleanupAllSystems");
                    manager.SetIsSpawned(false, true);
                    manager.SetIsSpawned(false, false);
                    var pools = Get<GameObjectPoolCollection>(manager, "_pools");
                    if (pools != null)
                    {
                        var ownedPools = (Dictionary<GameObject, GameObjectPool>)Field(
                            typeof(GameObjectPoolCollection), "_pools").GetValue(pools);
                        foreach (var pool in ownedPools.Values)
                        {
                            var objects = (Stack<GameObject>)Field(typeof(GameObjectPool), "_pool").GetValue(pool);
                            while (objects.Count > 0)
                            {
                                var pooledObject = objects.Pop();
                                if (pooledObject) Object.DestroyImmediate(pooledObject);
                            }
                        }
                    }
                    pools?.Dispose();
                    Set(manager, "_pools", null);
                    var parent = Get<GameObject>(manager, "_poolParent");
                    if (parent)
                        Object.DestroyImmediate(parent);
                }
                foreach (var network in _networks)
                {
                    Set(typeof(NetworkManager), network, "<isServer>k__BackingField", false);
                    Set(typeof(NetworkManager), network, "<isClient>k__BackingField", false);
                }
                for (var i = _objects.Count - 1; i >= 0; i--)
                    if (_objects[i]) Object.DestroyImmediate(_objects[i]);
                if (_prefabs) Object.DestroyImmediate(_prefabs);
            }
        }

        private static BitPacker Copy(BitPacker source)
        {
            int end = source.positionInBits;
            int bytes = source.ToByteData().length;
            var result = BitPackerPool.Get();
            source.ResetPositionAndMode(true);
            result.WriteBits(source, bytes * 8);
            source.SetBitPosition(end);
            return result;
        }
        private static T Get<T>(PredictionManager manager, string name) => (T)Field(typeof(PredictionManager), name).GetValue(manager);
        private static void Set(PredictionManager manager, string name, object value) => Set(typeof(PredictionManager), manager, name, value);
        private static void Set(Type type, object target, string name, object value) => Field(type, name).SetValue(target, value);
        private static FieldInfo Field(Type type, string name)
        {
            var result = type.GetField(name, Fields);
            Assert.That(result, Is.Not.Null, $"Missing field {type.Name}.{name}");
            return result;
        }
        private static object Invoke(PredictionManager manager, string name, params object[] args)
        {
            var method = typeof(PredictionManager).GetMethod(name, Fields);
            Assert.That(method, Is.Not.Null, $"Missing method PredictionManager.{name}");
            try { return method.Invoke(manager, args); }
            catch (TargetInvocationException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    public struct LifecycleInput : IPredictedData
    {
        public int amount;
        public void Dispose() { }
    }

    public struct LifecycleState : IPredictedData<LifecycleState>
    {
        public int value, starts;
        public bool suppressLedger;
        public void Dispose() { }
    }

    public struct LifecycleLedgerState : IPredictedData<LifecycleLedgerState>
    {
        public long sum;
        public int count;
        public void Dispose() { }
    }

    public readonly struct LifecycleObservation
    {
        public readonly PredictedObjectID objectId;
        public readonly ulong tick;
        public readonly int enteringValue, input, starts;
        public readonly PredictedObjectID? parent;
        public LifecycleObservation(PredictedObjectID objectId, ulong tick, int enteringValue, int input, int starts,
            PredictedObjectID? parent = null)
        {
            this.objectId = objectId; this.tick = tick; this.enteringValue = enteringValue;
            this.input = input; this.starts = starts;
            this.parent = parent;
        }
        public override string ToString() => $"{objectId}@{tick}:state={enteringValue},input={input},starts={starts},parent={parent}";
    }

    public sealed class LifecycleLedger : DeterministicIdentity<LifecycleLedgerState>
    {
        public readonly List<LifecycleObservation> observations = new();
        public readonly List<(PredictedObjectID id, ulong tick)> materializations = new();
        protected override void Simulate(ref LifecycleLedgerState state, sfloat delta) { }
    }

    public sealed class LifecycleInputProbe : PredictedIdentity<LifecycleInput, LifecycleState>
    {
        protected override void LateAwake()
            => predictionManager.GetComponent<LifecycleLedger>().materializations.Add(
                (id.objectId, predictionManager.localTickInContext));
        protected override LifecycleState GetInitialState() => new() { value = -50 };
        protected override void SimulationStart() => currentState.starts++;
        protected override void GetFinalInput(ref LifecycleInput input)
            => input.amount = (int)predictionManager.localTick;
        protected override void Simulate(LifecycleInput input, ref LifecycleState state, float delta)
        {
            var ledger = predictionManager.GetComponent<LifecycleLedger>();
            var parentIdentity = transform.parent ? transform.parent.GetComponent<PredictedIdentity>() : null;
            if (predictionManager.cachedIsServer || predictionManager.isVerifiedAndReplaying)
                ledger.observations.Add(new LifecycleObservation(id.objectId, predictionManager.localTickInContext,
                    state.value, input.amount, state.starts, parentIdentity ? parentIdentity.id.objectId : null));
            if (!state.suppressLedger)
            {
                ledger.currentState.sum += state.value * 100L + input.amount;
                ledger.currentState.count++;
            }
            state.value += input.amount;
        }
    }
}
