using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class UnchangedStateOmissionTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterPackers()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(OmittedState));
            Hasher.PrepareType(typeof(OmittedAlphaModule));
            Hasher.PrepareType(typeof(OmittedBetaModule));
            Packer<OmittedState>.RegisterWriter(
                (packer, value) => Packer<int>.Write(packer, value.value));
            Packer<OmittedState>.RegisterReader(
                (BitPacker packer, ref OmittedState value) =>
                    value.value = Packer<int>.Read(packer));
        }

        [Test]
        public void AddressedSectionOmitsStableRecordAndReceiverCarriesItForward()
        {
            var senderManagerObject = new GameObject("Sparse sender manager");
            var receiverManagerObject = new GameObject("Sparse receiver manager");
            var senderObject = new GameObject("Sparse sender identity");
            var receiverObject = new GameObject("Sparse receiver identity");
            try
            {
                var id = new PredictedComponentID(new PredictedObjectID(190), 0);
                var senderManager = CreateManager(senderManagerObject);
                var sender = senderObject.AddComponent<OmittedStateIdentity>();
                var senderVerified =
                    InitializeIdentity(sender, senderManager, id, 100);
                senderVerified.Write(10, FullState(100));
                sender.fullPredictedState = FullState(100);
                SetLocalTick(senderManager, 12);
                RegisterSystem(senderManager, sender);

                using var frame = BitPackerPool.Get();
                WriteAddressedStateSection(
                    senderManager,
                    new PlayerVisibilityTimeline(),
                    frame,
                    tick: 12,
                    baselineTick: 10,
                    fullFrame: false);

                frame.ResetPositionAndMode(true);
                PackedUInt count = default;
                Packer<PackedUInt>.Read(frame, ref count);
                Assert.That(count.value, Is.Zero,
                    "the unchanged supported identity remained in the section");

                var receiverManager = CreateManager(receiverManagerObject);
                var receiver = receiverObject.AddComponent<OmittedStateIdentity>();
                var receiverVerified =
                    InitializeIdentity(receiver, receiverManager, id, 900);
                receiverVerified.Write(10, FullState(100));
                receiverVerified.Write(11, FullState(200));
                var receiverPredicted =
                    GetField<History<FULL_STATE<OmittedState>>>(
                        typeof(PredictedIdentity<OmittedState>),
                        receiver,
                        "_stateHistory");
                receiverPredicted.Write(12, FullState(900));
                receiver.fullPredictedState = FullState(900);
                receiver.lastVerifiedTick = 11;
                RegisterSystem(receiverManager, receiver);

                frame.ResetPositionAndMode(true);
                ReadAddressedStateSection(
                    receiverManager,
                    frame,
                    stateTick: 12,
                    baselineTick: 10,
                    serverTick: 12);

                Assert.That(receiver.currentState.value, Is.EqualTo(100));
                Assert.That(
                    receiverVerified.ReadOrPrevious(12, out var carried),
                    Is.True);
                Assert.That(carried.state.value, Is.EqualTo(100));
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void NewlyAttachedDynamicModuleUsesDefaultBaselineInsideAnExistingParentDelta()
        {
            var senderManagerObject = new GameObject("New module sender manager");
            var receiverManagerObject = new GameObject("New module receiver manager");
            var senderObject = new GameObject("Existing sender parent");
            var receiverObject = new GameObject("Existing receiver parent");
            try
            {
                var id = new PredictedComponentID(new PredictedObjectID(193), 0);
                var senderManager = CreateManager(senderManagerObject);
                var receiverManager = CreateManager(receiverManagerObject);
                var sender = senderObject.AddComponent<OmittedStateIdentity>();
                var receiver = receiverObject.AddComponent<OmittedStateIdentity>();
                InitializeIdentity(sender, senderManager, id, 100).Write(10, FullState(100));
                InitializeIdentity(receiver, receiverManager, id, 900).Write(10, FullState(100));
                sender.lastVerifiedTick = receiver.lastVerifiedTick = 10;
                SetLocalTick(senderManager, 12);
                SetLocalTick(receiverManager, 12);
                RegisterSystem(senderManager, sender);
                RegisterSystem(receiverManager, receiver);
                foreach (var identity in new[] { sender, receiver })
                {
                    SetField(typeof(PredictedIdentity), identity, "_moduleHistory",
                        new History<DisposableList<uint>>(200));
                }

                // Attach through the normal dynamic registration branch after the parent
                // baseline, rather than seeding a fake module baseline to satisfy the guard.
                SetField(typeof(PredictionManager), senderManager, "<localTickInContext>k__BackingField", 12UL);
                SetField(typeof(PredictionManager), senderManager, "<isSimulating>k__BackingField", true);
                var added = new OmittedAlphaModule(sender);
                SetField(typeof(PredictionManager), senderManager, "<isSimulating>k__BackingField", false);
                added.currentState.value = 731;
                Assert.That(added.registeredAtTick, Is.EqualTo(12));
                Assert.That(added.HasUnchangedStateBaselineInternal(10), Is.False);
                Assert.That(sender.HasUnchangedStateBaseline(10), Is.True);
                Assert.That(sender.RunCanOmitUnchangedState(10), Is.False,
                    "the new module must prevent aggregate omission without forcing a full parent record");
                Assert.That(receiver.modules, Is.Empty);
                Assert.That(receiver.HasUnchangedStateBaseline(10), Is.True);
                Assert.That(receiverManager.GetVerifiedHistory<MODULE_STATE<OmittedState>>(id, 1, out _).Count,
                    Is.Zero, "the receiver must decode the new module without a prior module snapshot");

                using var frame = BitPackerPool.Get();
                WriteAddressedStateSection(senderManager, new PlayerVisibilityTimeline(), frame,
                    tick: 12, baselineTick: 10, fullFrame: false);
                AssertSingleRecord(frame, id, expectedFullState: false);
                frame.ResetPositionAndMode(true);
                ReadAddressedStateSection(receiverManager, frame, stateTick: 12, baselineTick: 10, serverTick: 12);

                Assert.That(receiver.modules.Count, Is.EqualTo(1));
                Assert.That(receiver.modules[0], Is.TypeOf<OmittedAlphaModule>());
                var received = (OmittedAlphaModule)receiver.modules[0];
                Assert.That(received.currentState.value, Is.EqualTo(731));
                Assert.That(receiver.currentState.value, Is.EqualTo(100));
                Assert.That(receiver.lastVerifiedTick, Is.EqualTo(12));
                var moduleHistory = receiverManager.GetVerifiedHistory<MODULE_STATE<OmittedState>>(id, 1, out _);
                Assert.That(moduleHistory.ReadOrPrevious(10, out _), Is.False,
                    "the test must not create a historical baseline as a side effect of attachment");
                Assert.That(moduleHistory.ReadOrPrevious(12, out var verifiedModule), Is.True);
                Assert.That(verifiedModule.state.value, Is.EqualTo(731));
                Assert.That(GetField<bool>(typeof(PredictionManager), receiverManager,
                    "_frameApplyHadRecordFailure"), Is.False);
                Assert.That(GetField<bool>(typeof(PredictionManager), receiverManager,
                    "_historyResyncPending"), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void UnknownRecordDoesNotPreventKnownOmissionCarryForward()
        {
            var managerObject = new GameObject("Unknown plus omission manager");
            var identityObject = new GameObject("Known omitted identity");
            try
            {
                var manager = CreateManager(managerObject);
                var knownId =
                    new PredictedComponentID(new PredictedObjectID(191), 0);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var verified =
                    InitializeIdentity(identity, manager, knownId, 900);
                verified.Write(10, FullState(100));
                verified.Write(11, FullState(200));
                var predicted =
                    GetField<History<FULL_STATE<OmittedState>>>(
                        typeof(PredictedIdentity<OmittedState>),
                        identity,
                        "_stateHistory");
                predicted.Write(12, FullState(900));
                identity.fullPredictedState = FullState(900);
                identity.lastVerifiedTick = 11;
                RegisterSystem(manager, identity);

                using var frame = BitPackerPool.Get();
                using var unknownPayload = BitPackerPool.Get();
                unknownPayload.WriteBits(0b101u, 3);
                AddressedPredictionRecords.WriteSectionCount(1, frame);
                AddressedPredictionRecords.WriteRecord(
                    frame,
                    new PredictedComponentID(new PredictedObjectID(999), 7),
                    false,
                    unknownPayload);
                frame.ResetPositionAndMode(true);

                ReadAddressedStateSection(
                    manager,
                    frame,
                    stateTick: 12,
                    baselineTick: 10,
                    serverTick: 12);

                Assert.That(identity.currentState.value, Is.EqualTo(100));
                Assert.That(verified.ReadOrPrevious(12, out var carried), Is.True);
                Assert.That(carried.state.value, Is.EqualTo(100));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void FullFrameKeepsUnchangedRecordExplicit()
        {
            var managerObject = new GameObject("Full sparse manager");
            var identityObject = new GameObject("Full sparse identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(192), 0);
                var verified = InitializeIdentity(identity, manager, id, 100);
                verified.Write(10, FullState(100));
                identity.fullPredictedState = FullState(100);
                RegisterSystem(manager, identity);

                using var frame = BitPackerPool.Get();
                WriteAddressedStateSection(
                    manager,
                    new PlayerVisibilityTimeline(),
                    frame,
                    tick: 12,
                    baselineTick: 10,
                    fullFrame: true);

                AssertSingleRecord(frame, id, expectedFullState: true);
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }
        [Test]
        public void OmittedStateUsesAckBaselineAndAnchorsTheNewVerifiedTick()
        {
            var managerObject = new GameObject("Omitted state manager");
            var identityObject = new GameObject("Omitted state identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(200), 0);
                var verified = InitializeIdentity(identity, manager, id, 900);
                verified.Write(10, FullState(100));
                verified.Write(11, FullState(200));

                var predictedHistory = GetField<History<FULL_STATE<OmittedState>>>(
                    typeof(PredictedIdentity<OmittedState>),
                    identity,
                    "_stateHistory");
                predictedHistory.Write(12, FullState(900));
                identity.fullPredictedState = FullState(900);
                identity.lastVerifiedTick = 11;

                Assert.That(identity.RunCanReadUnchangedState(10), Is.True);

                identity.RunClearFuture(12);
                identity.RunReadUnchangedState(12, 10, 12);
                identity.RunRollback(12);
                identity.lastVerifiedTick = 12;

                Assert.That(identity.currentState.value, Is.EqualTo(100),
                    "the unacknowledged tick-11 value was used instead of ACK tick 10");
                Assert.That(verified.ReadOrPrevious(12, out var anchored), Is.True);
                Assert.That(anchored.state.value, Is.EqualTo(100),
                    "the carried state was not re-anchored at tick 12");

                predictedHistory.Write(13, FullState(901));
                identity.fullPredictedState = FullState(901);
                identity.RunClearFuture(13);
                identity.RunReadUnchangedState(13, 12, 13);
                identity.RunRollback(13);
                identity.lastVerifiedTick = 13;

                Assert.That(identity.currentState.value, Is.EqualTo(100),
                    "advancing the ACK to an omitted tick resurrected tick 11");
                Assert.That(verified.ReadOrPrevious(13, out var followUp), Is.True);
                Assert.That(followUp.state.value, Is.EqualTo(100));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ReturnedToAckBaselineWritesNoIdentityDelta()
        {
            var managerObject = new GameObject("Returned state manager");
            var identityObject = new GameObject("Returned state identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(201), 0);
                var verified = InitializeIdentity(identity, manager, id, 100);
                verified.Write(10, FullState(100));
                verified.Write(11, FullState(200));
                identity.fullPredictedState = FullState(100);
                SetLocalTick(manager, 12);

                using var packer = BitPackerPool.Get();
                Assert.That(
                    identity.WriteCurrentState(default, packer, 10),
                    Is.False,
                    "A→B→A should compare against the explicit ACK baseline, not latest");

                Assert.That(verified.ReadOrPrevious(12, out var anchored), Is.True);
                Assert.That(anchored.state.value, Is.EqualTo(100));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void OmittedDynamicTopologyStagesAckTopologyBeforeReadingModules()
        {
            var managerObject = new GameObject("Omitted topology manager");
            var identityObject = new GameObject("Omitted topology identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(202), 0);
                var identityVerified = InitializeIdentity(identity, manager, id, 2);
                identityVerified.Write(10, FullState(1));
                identityVerified.Write(11, FullState(2));
                identity.lastVerifiedTick = 11;

                var alpha = new OmittedAlphaModule(identity);
                uint alphaHash = alpha.typeHash;
                MarkAllModulesDynamic(identity);
                alpha.Dispose();

                var beta = new OmittedBetaModule(identity);
                uint betaHash = beta.typeHash;
                beta.currentState.value = 200;

                var moduleSets =
                    manager.GetVerifiedHistory<DisposableList<uint>>(id, out _);
                moduleSets.Write(10, ModuleSet(alphaHash));
                moduleSets.Write(11, ModuleSet(betaHash));

                var localTopology = new History<DisposableList<uint>>(200);
                localTopology.Write(10, ModuleSet(alphaHash));
                localTopology.Write(11, ModuleSet(betaHash));
                SetField(
                    typeof(PredictedIdentity),
                    identity,
                    "_moduleHistory",
                    localTopology);

                var moduleStates =
                    manager.GetVerifiedHistory<MODULE_STATE<OmittedState>>(
                        id,
                        1,
                        out _);
                moduleStates.Write(10, ModuleState(100));
                moduleStates.Write(11, ModuleState(200));

                Assert.That(identity.modules[0], Is.SameAs(beta));
                Assert.That(identity.RunCanReadUnchangedState(10), Is.True,
                    "the receiver precheck must not validate the unacknowledged B topology");

                identity.RunReadUnchangedState(12, 10, 12);

                Assert.That(identity.modules.Count, Is.EqualTo(1));
                Assert.That(identity.modules[0], Is.TypeOf<OmittedAlphaModule>());
                var restored = (OmittedAlphaModule)identity.modules[0];
                Assert.That(restored.currentState.value, Is.EqualTo(100));

                Assert.That(moduleSets.ReadOrPrevious(12, out var topologyAt12), Is.True);
                Assert.That(topologyAt12.Count, Is.EqualTo(1));
                Assert.That(topologyAt12[0], Is.EqualTo(alphaHash));
                Assert.That(moduleStates.ReadOrPrevious(12, out var moduleAt12), Is.True);
                Assert.That(moduleAt12.state.value, Is.EqualTo(100));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void OmittedDynamicTopologyRejectsMissingAckBaseline()
        {
            var managerObject = new GameObject("Missing topology manager");
            var identityObject = new GameObject("Missing topology identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(207), 0);
                var identityVerified = InitializeIdentity(identity, manager, id, 1);
                identityVerified.Write(10, FullState(1));
                identity.lastVerifiedTick = 10;

                _ = new OmittedAlphaModule(identity);
                MarkAllModulesDynamic(identity);

                // The receiver precheck must itself reject an omission whose dynamic module
                // topology has no acknowledged baseline, rather than relying on a deeper,
                // less specific throw inside RunReadUnchangedState to catch it.
                Assert.That(identity.RunCanReadUnchangedState(10), Is.False);

                // RunReadUnchangedState remains defensively safe even if a caller bypasses the
                // precheck above and invokes it directly.
                var exception = Assert.Throws<InvalidOperationException>(
                    () => identity.RunReadUnchangedState(12, 10, 12));
                Assert.That(
                    exception.Message,
                    Does.Contain("Missing dynamic module topology baseline at tick 10"));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void OmittedTopologyForIdentityWithoutDynamicsIsACompleteNoOp()
        {
            var managerObject = new GameObject("No dynamics topology manager");
            var identityObject = new GameObject("No dynamics topology identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(208), 0);
                identity.AttachForTest(manager, id);
                var localTopology = new History<DisposableList<uint>>(200);
                SetField(
                    typeof(PredictedIdentity),
                    identity,
                    "_moduleHistory",
                    localTopology);

                identity.ReadUnchangedDynamicModuleSnapshot(12, 10, 12);

                Assert.That(
                    GetField<History<DisposableList<uint>>>(
                        typeof(PredictedIdentity),
                        identity,
                        "_moduleSetVerified"),
                    Is.Null,
                    "an identity without dynamic modules must not allocate the verified store");
                Assert.That(localTopology.Count, Is.Zero,
                    "an identity without dynamic modules must not record topology history");
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void SenderWithoutDynamicsDoesNotCreateReceiverVerifiedStore()
        {
            var managerObject = new GameObject("No dynamics sender manager");
            var identityObject = new GameObject("No dynamics sender identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(209), 0);
                identity.AttachForTest(manager, id);

                using (var payload = BitPackerPool.Get())
                {
                    Packer<bool>.Write(payload, false);
                    payload.ResetPositionAndMode(true);
                    identity.ReadDynamicModuleSnapshot(12, payload, 10, 12);
                }

                using (var firstPayload = BitPackerPool.Get())
                {
                    Packer<PackedUInt>.Write(firstPayload, (uint)0);
                    Packer<bool>.Write(firstPayload, false);
                    firstPayload.ResetPositionAndMode(true);
                    identity.ReadFirstDynamicModuleSnapshot(12, firstPayload, 12);
                }

                Assert.That(
                    GetField<History<DisposableList<uint>>>(
                        typeof(PredictedIdentity),
                        identity,
                        "_moduleSetVerified"),
                    Is.Null,
                    "a dynamics-free wire payload must not allocate the verified store");
                Assert.That(
                    GetField<History<DisposableList<uint>>>(
                        typeof(PredictedIdentity),
                        identity,
                        "_moduleHistory"),
                    Is.Null,
                    "a dynamics-free wire payload must not allocate topology history");
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void SenderWithoutDynamicsStillAnchorsEmptyTopologyForDynamicReceiver()
        {
            var managerObject = new GameObject("Dynamic receiver anchor manager");
            var identityObject = new GameObject("Dynamic receiver anchor identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(210), 0);
                identity.AttachForTest(manager, id);
                MarkAllModulesDynamic(identity);
                var localTopology = new History<DisposableList<uint>>(200);
                SetField(
                    typeof(PredictedIdentity),
                    identity,
                    "_moduleHistory",
                    localTopology);

                using var payload = BitPackerPool.Get();
                Packer<bool>.Write(payload, false);
                payload.ResetPositionAndMode(true);
                identity.ReadDynamicModuleSnapshot(12, payload, 10, 12);

                var store = GetField<History<DisposableList<uint>>>(
                    typeof(PredictedIdentity),
                    identity,
                    "_moduleSetVerified");
                Assert.That(store, Is.Not.Null,
                    "a receiver with dynamic topology must anchor the empty set");
                Assert.That(store.ReadOrPrevious(12, out var anchored), Is.True);
                Assert.That(anchored.Count, Is.Zero);
                Assert.That(localTopology.ReadOrPrevious(12, out var applied), Is.True);
                Assert.That(applied.Count, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void OmittedMetadataUsesAckBaselineAndAnchorsTheNewTick()
        {
            var managerObject = new GameObject("Omitted metadata manager");
            var identityObject = new GameObject("Omitted metadata identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<StaticPredictedIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(203), 0);
                AttachIdentity(identity, manager, id);
                var ownerA = new PlayerID(new PackedULong(10), false);
                var ownerB = new PlayerID(new PackedULong(11), false);
                var metadata =
                    manager.GetVerifiedHistory<PredictedIdentityState>(id, out _);
                metadata.Write(10, new PredictedIdentityState { owner = ownerA });
                metadata.Write(11, new PredictedIdentityState { owner = ownerB });
                identity.SetOwner(ownerB);
                identity.lastVerifiedTick = 11;

                identity.RunReadUnchangedState(12, 10, 12);

                Assert.That(identity.owner, Is.EqualTo(ownerA));
                Assert.That(metadata.ReadOrPrevious(12, out var metadataAt12), Is.True);
                Assert.That(metadataAt12.owner, Is.EqualTo(ownerA));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void CustomIdentitySerializerParticipatesInUnchangedShortcut()
        {
            var managerObject = new GameObject("Custom serializer manager");
            var identityObject = new GameObject("Custom serializer identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<CustomOmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(204), 0);
                var verified = InitializeIdentity(identity, manager, id, 100);
                verified.Write(10, FullState(100));
                verified.Write(11, FullState(200));
                identity.fullPredictedState = FullState(100);
                SetLocalTick(manager, 12);

                using var packer = BitPackerPool.Get();
                Assert.That(identity.WriteCurrentState(default, packer, 10), Is.False,
                    "state equal to the acknowledged baseline must take the unchanged shortcut");
                Assert.That(identity.writeDeltaCalls, Is.Zero,
                    "the shortcut applies regardless of who owns the delta serializer");

                identity.fullPredictedState = FullState(300);
                using var changedPacker = BitPackerPool.Get();
                Assert.That(identity.WriteCurrentState(default, changedPacker, 10), Is.True);
                Assert.That(identity.writeDeltaCalls, Is.EqualTo(1),
                    "changed state must still flow through the custom serializer");
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ModuleWithoutVerifiedBaselineBlocksAggregateOmission()
        {
            var managerObject = new GameObject("Custom module manager");
            var identityObject = new GameObject("Custom module identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(205), 0);
                var verified = InitializeIdentity(identity, manager, id, 100);
                verified.Write(10, FullState(100));
                var module = new CustomOmittedModule(identity);

                Assert.That(identity.RunCanOmitUnchangedState(10), Is.False,
                    "a module with no acknowledged baseline must retain the aggregate record");

                var moduleVerified = manager.GetVerifiedHistory<MODULE_STATE<OmittedState>>(
                    identity.id,
                    1 + module.moduleIndex,
                    out _);
                moduleVerified.Write(10, default);

                Assert.That(identity.RunCanOmitUnchangedState(10), Is.True,
                    "once every ledger holds the baseline the record becomes omittable");
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ReceiverOnlyIdentityIsNotTreatedAsAnOmittedServerRecord()
        {
            var managerObject = new GameObject("Receiver-only manager");
            var identityObject = new GameObject("Receiver-only identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                AttachIdentity(
                    identity,
                    manager,
                    new PredictedComponentID(new PredictedObjectID(206), 0));

                var systems = GetField<List<PredictedIdentity>>(
                    typeof(PredictionManager),
                    manager,
                    "_systems");
                systems.Add(identity);
                SetField(
                    typeof(PredictionManager),
                    manager,
                    "_systemsCount",
                    systems.Count);

                using var frame = BitPackerPool.Get();
                AddressedPredictionRecords.WriteSectionCount(0, frame);
                frame.ResetPositionAndMode(true);

                var readSection = typeof(PredictionManager).GetMethod(
                    "ReadAddressedStateSection",
                    InstanceFields);
                Assert.That(readSection, Is.Not.Null);
                Assert.DoesNotThrow(() =>
                    readSection.Invoke(
                        manager,
                        new object[] { frame, 12UL, 10UL, 12UL, false, false }));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void NewPieceUnderExistingRootRequiresFullEntryState()
        {
            var managerObject = new GameObject("Piece baseline manager");
            var hierarchyObject = new GameObject("Piece baseline hierarchy");
            var identityObject = new GameObject("Piece baseline identity");
            try
            {
                var manager = CreateManager(managerObject);
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var root = new PredictedObjectID(300);
                var piece = new PredictedObjectID(301);
                AttachHierarchy(
                    manager,
                    hierarchy,
                    new InstanceDetails(
                        1, 0, root, Vector3.zero, Quaternion.identity, null, null),
                    new InstanceDetails(
                        1, 1, piece, Vector3.zero, Quaternion.identity, null, null));

                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(piece, 0);
                InitializeIdentity(identity, manager, id, 100);
                SetLocalTick(manager, 12);
                var baselineRoots = new HashSet<PredictedObjectID> { root };
                var baselinePieces = new HashSet<PredictedObjectID> { root };

                var requiresFull = typeof(PredictionManager).GetMethod(
                    "RequiresFullEntryState",
                    InstanceFields);
                Assert.That(requiresFull, Is.Not.Null);
                var timeline = new PlayerVisibilityTimeline();

                Assert.That(
                    (bool)requiresFull.Invoke(
                        manager,
                        new object[] { timeline, identity, 10UL, true, baselineRoots, baselinePieces }),
                    Is.True);

                baselinePieces.Add(piece);
                Assert.That(
                    (bool)requiresFull.Invoke(
                        manager,
                        new object[] { timeline, identity, 10UL, true, baselineRoots, baselinePieces }),
                    Is.False);

                RecordHierarchyVerifiedState(
                    manager,
                    hierarchy,
                    10,
                    new InstanceDetails(
                        1, 0, root, Vector3.zero, Quaternion.identity, null, null));
                RegisterSystem(manager, identity);
                using var frame = BitPackerPool.Get();
                WriteAddressedStateSection(
                    manager,
                    timeline,
                    frame,
                    tick: 12,
                    baselineTick: 10,
                    fullFrame: false);
                AssertSingleRecord(frame, id, expectedFullState: true);
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void VisibilityReentryKeepsStateExplicit()
        {
            var managerObject = new GameObject("Reentry state manager");
            var hierarchyObject = new GameObject("Reentry state hierarchy");
            var identityObject = new GameObject("Reentry state identity");
            try
            {
                var manager = CreateManager(managerObject);
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var root = new PredictedObjectID(310);
                var record = new InstanceDetails(
                    1, 0, root, Vector3.zero, Quaternion.identity, null, null);
                AttachHierarchy(manager, hierarchy, record);
                RecordHierarchyVerifiedState(manager, hierarchy, 10, record);

                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(root, 0);
                var verified = InitializeIdentity(identity, manager, id, 100);
                verified.Write(10, FullState(100));
                identity.fullPredictedState = FullState(100);
                SetLocalTick(manager, 12);
                RegisterSystem(manager, identity);

                var timeline = new PlayerVisibilityTimeline();
                timeline.SetVisible(11, root, false);
                timeline.SetVisible(12, root, true);
                using var frame = BitPackerPool.Get();
                WriteAddressedStateSection(
                    manager,
                    timeline,
                    frame,
                    tick: 12,
                    baselineTick: 10,
                    fullFrame: false);

                AssertSingleRecord(frame, id, expectedFullState: true);
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void DesyncHealForcesSingleFullDeterministicRecord()
        {
            var managerObject = new GameObject("Deterministic heal manager");
            var identityObject = new GameObject("Deterministic heal identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity =
                    identityObject.AddComponent<DeterministicOmittedIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(320), 0);
                identity.AttachForTest(manager, id);
                identity.fullPredictedState = FullState(100);
                var predicted = new History<FULL_STATE<OmittedState>>(200);
                predicted.Write(0, FullState(100));
                SetField(
                    typeof(DeterministicIdentity<OmittedState>),
                    identity,
                    "_stateHistory",
                    predicted);
                var metadata =
                    manager.GetVerifiedHistory<PredictedIdentityState>(id, out _);
                metadata.Write(10, default);
                SetLocalTick(manager, 12);
                RegisterSystem(manager, identity);

                using (var noHeal = BitPackerPool.Get())
                {
                    WriteAddressedStateSection(
                        manager,
                        new PlayerVisibilityTimeline(),
                        noHeal,
                        tick: 12,
                        baselineTick: 10,
                        fullFrame: false);
                    noHeal.ResetPositionAndMode(true);
                    PackedUInt count = default;
                    Packer<PackedUInt>.Read(noHeal, ref count);
                    Assert.That(count.value, Is.Zero);
                }

                var heals = GetField<Dictionary<PlayerID, HashSet<PredictedComponentID>>>(
                    typeof(PredictionManager),
                    manager,
                    "_pendingDesyncHeals");
                heals[default] = new HashSet<PredictedComponentID> { id };

                using (var healed = BitPackerPool.Get())
                {
                    WriteAddressedStateSection(
                        manager,
                        new PlayerVisibilityTimeline(),
                        healed,
                        tick: 12,
                        baselineTick: 10,
                        fullFrame: false);
                    AssertSingleRecord(healed, id, expectedFullState: true);
                }

                Assert.That(heals[default].Contains(id), Is.True,
                    "serialization alone must not consume a heal that has not been sent");
                using (var retry = BitPackerPool.Get())
                {
                    WriteAddressedStateSection(
                        manager,
                        new PlayerVisibilityTimeline(),
                        retry,
                        tick: 12,
                        baselineTick: 10,
                        fullFrame: false);
                    AssertSingleRecord(retry, id, expectedFullState: true);
                }

                // Represent transport-success bookkeeping explicitly. The checkpoint
                // delivery fixture separately runs the real oversized no-send branch.
                var commit = typeof(PredictionManager).GetMethod(
                    "CommitPreparedDesyncHeals", InstanceFields);
                Assert.That(commit, Is.Not.Null);
                commit.Invoke(manager, new object[] { default(PlayerID) });
                Assert.That(heals[default].Contains(id), Is.False);

                using var consumed = BitPackerPool.Get();
                WriteAddressedStateSection(
                    manager,
                    new PlayerVisibilityTimeline(),
                    consumed,
                    tick: 12,
                    baselineTick: 10,
                    fullFrame: false);
                consumed.ResetPositionAndMode(true);
                PackedUInt afterCount = default;
                Packer<PackedUInt>.Read(consumed, ref afterCount);
                Assert.That(afterCount.value, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void ResetStateClearsOmissionLifetimeEligibility()
        {
            var managerObject = new GameObject("Reset omission manager");
            var identityObject = new GameObject("Reset omission identity");
            try
            {
                var manager = CreateManager(managerObject);
                var identity = identityObject.AddComponent<OmittedStateIdentity>();
                var id = new PredictedComponentID(new PredictedObjectID(330), 0);
                var verified = InitializeIdentity(identity, manager, id, 100);
                verified.Write(10, FullState(100));
                identity.lastVerifiedTick = 10;

                Assert.That(identity.RunCanReadUnchangedState(10), Is.True);
                identity.ResetState();

                Assert.That(identity.lastVerifiedTick, Is.Null);
                Assert.That(identity.RunCanReadUnchangedState(10), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void SoftCorrectionOmissionUsesCallbackAndMissingHistorySnaps()
        {
            var managerObject = new GameObject("Soft omission manager");
            var callbackObject = new GameObject("Soft callback identity");
            var snapObject = new GameObject("Soft snap identity");
            try
            {
                var manager = CreateManager(managerObject);
                var callback =
                    callbackObject.AddComponent<SoftOmittedStateIdentity>();
                var callbackId =
                    new PredictedComponentID(new PredictedObjectID(340), 0);
                var callbackVerified =
                    InitializeIdentity(callback, manager, callbackId, 900);
                callbackVerified.Write(10, FullState(100));
                callbackVerified.Write(11, FullState(200));
                var callbackHistory =
                    GetField<History<FULL_STATE<OmittedState>>>(
                        typeof(PredictedIdentity<OmittedState>),
                        callback,
                        "_stateHistory");
                callbackHistory.Write(12, FullState(900));
                callback.fullPredictedState = FullState(900);
                callback.lastVerifiedTick = 11;
                callback.SetPredictionPolicyOverride(PredictionPolicy.SoftCorrection);

                callback.RunReadUnchangedState(12, 10, 12);

                Assert.That(callback.callbackCount, Is.EqualTo(1));
                Assert.That(callback.callbackPredicted, Is.EqualTo(900));
                Assert.That(callback.callbackVerified, Is.EqualTo(100));
                Assert.That(callback.currentState.value, Is.EqualTo(900),
                    "the live soft-correction timeline should not be rolled back");
                Assert.That(
                    callbackVerified.ReadOrPrevious(12, out var callbackAt12),
                    Is.True);
                Assert.That(callbackAt12.state.value, Is.EqualTo(100));

                var snap = snapObject.AddComponent<SoftOmittedStateIdentity>();
                var snapId =
                    new PredictedComponentID(new PredictedObjectID(341), 0);
                var snapVerified = InitializeIdentity(snap, manager, snapId, 900);
                snapVerified.Write(10, FullState(100));
                snapVerified.Write(11, FullState(200));
                var snapHistory =
                    GetField<History<FULL_STATE<OmittedState>>>(
                        typeof(PredictedIdentity<OmittedState>),
                        snap,
                        "_stateHistory");
                snapHistory.Clear();
                snap.fullPredictedState = FullState(900);
                snap.lastVerifiedTick = 11;
                snap.SetPredictionPolicyOverride(PredictionPolicy.SoftCorrection);

                snap.RunReadUnchangedState(12, 10, 12);

                Assert.That(snap.callbackCount, Is.Zero);
                Assert.That(snap.currentState.value, Is.EqualTo(100),
                    "without a predicted sample the verified baseline must snap live state");
            }
            finally
            {
                Object.DestroyImmediate(snapObject);
                Object.DestroyImmediate(callbackObject);
                Object.DestroyImmediate(managerObject);
            }
        }
        [Test]
        public void PassThroughWaitsForRestoredVisibilityToBeAcknowledged()
        {
            var timeline = new PlayerVisibilityTimeline();
            var root = new PredictedObjectID(400);

            Assert.That(timeline.isPassThrough, Is.True);
            timeline.SetVisible(10, root, false);
            Assert.That(timeline.isPassThrough, Is.False);
            timeline.SetVisible(11, root, true);
            Assert.That(timeline.currentExceptionCount, Is.Zero);
            Assert.That(timeline.isPassThrough, Is.False);

            timeline.PruneThrough(10);
            Assert.That(timeline.isPassThrough, Is.False);
            timeline.PruneThrough(11);
            Assert.That(timeline.isPassThrough, Is.True);
        }

        [Test]
        public void BaselineTickZeroWritesEveryRecordAndReadSkipsCarryForward()
        {
            var senderManagerObject = new GameObject("Zero baseline sender manager");
            var receiverManagerObject = new GameObject("Zero baseline receiver manager");
            var senderObject = new GameObject("Zero baseline sender identity");
            var receiverObject = new GameObject("Zero baseline receiver identity");
            try
            {
                var id = new PredictedComponentID(new PredictedObjectID(500), 0);
                var senderManager = CreateManager(senderManagerObject);
                var sender = senderObject.AddComponent<OmittedStateIdentity>();
                InitializeIdentity(sender, senderManager, id, 100);
                sender.fullPredictedState = FullState(100);
                SetLocalTick(senderManager, 12);
                RegisterSystem(senderManager, sender);

                using var frame = BitPackerPool.Get();
                WriteAddressedStateSection(
                    senderManager,
                    new PlayerVisibilityTimeline(),
                    frame,
                    tick: 12,
                    baselineTick: 0,
                    fullFrame: false);

                // With no acknowledged baseline there is nothing to carry forward from, so the
                // record must stay explicit even though the state never changed.
                AssertSingleRecord(frame, id, expectedFullState: false);

                var receiverManager = CreateManager(receiverManagerObject);
                var receiver = receiverObject.AddComponent<OmittedStateIdentity>();
                var receiverVerified =
                    InitializeIdentity(receiver, receiverManager, id, 900);
                receiver.fullPredictedState = FullState(900);
                RegisterSystem(receiverManager, receiver);

                frame.ResetPositionAndMode(true);
                Assert.DoesNotThrow(() => ReadAddressedStateSection(
                    receiverManager,
                    frame,
                    stateTick: 12,
                    baselineTick: 0,
                    serverTick: 12));

                Assert.That(receiver.currentState.value, Is.EqualTo(100));
                Assert.That(receiverVerified.ReadOrPrevious(12, out var applied), Is.True);
                Assert.That(applied.state.value, Is.EqualTo(100));
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        [Test]
        public void ReadSkipsCarryForwardForSystemsResetByReconnect()
        {
            var managerObject = new GameObject("Reconnect skip manager");
            var presentObject = new GameObject("Reconnect present identity");
            var resetObject = new GameObject("Reconnect reset identity");
            try
            {
                var manager = CreateManager(managerObject);
                var presentId = new PredictedComponentID(new PredictedObjectID(510), 0);
                var resetId = new PredictedComponentID(new PredictedObjectID(511), 0);

                var present = presentObject.AddComponent<OmittedStateIdentity>();
                var presentVerified = InitializeIdentity(present, manager, presentId, 100);
                presentVerified.Write(10, FullState(100));
                present.fullPredictedState = FullState(100);
                present.lastVerifiedTick = 10;
                RegisterSystem(manager, present);

                // A reconnected/pooled occupant whose history was cleared: still registered, but
                // with no acknowledged baseline it must be skipped, not carry-forwarded.
                var reset = resetObject.AddComponent<OmittedStateIdentity>();
                InitializeIdentity(reset, manager, resetId, 700);
                reset.fullPredictedState = FullState(700);
                reset.ResetState();
                RegisterSystem(manager, reset);
                Assert.That(reset.lastVerifiedTick, Is.Null);

                using var frame = BitPackerPool.Get();
                AddressedPredictionRecords.WriteSectionCount(0, frame);
                frame.ResetPositionAndMode(true);

                Assert.DoesNotThrow(() => ReadAddressedStateSection(
                    manager,
                    frame,
                    stateTick: 12,
                    baselineTick: 10,
                    serverTick: 12));

                // The still-acknowledged system carries forward; the reset one is left alone.
                Assert.That(present.currentState.value, Is.EqualTo(100));
                Assert.That(present.lastVerifiedTick, Is.EqualTo(12));
                Assert.That(reset.lastVerifiedTick, Is.Null,
                    "a system without an acknowledged baseline must not be carry-forwarded");
            }
            finally
            {
                Object.DestroyImmediate(resetObject);
                Object.DestroyImmediate(presentObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void VisibilityReentryFullStateRoundTripsIntoAReceiverWithoutBaseline()
        {
            var senderManagerObject = new GameObject("Reentry rt sender manager");
            var receiverManagerObject = new GameObject("Reentry rt receiver manager");
            var hierarchyObject = new GameObject("Reentry rt hierarchy");
            var senderObject = new GameObject("Reentry rt sender identity");
            var receiverObject = new GameObject("Reentry rt receiver identity");
            try
            {
                var senderManager = CreateManager(senderManagerObject);
                var hierarchy = hierarchyObject.AddComponent<PredictedHierarchy>();
                var root = new PredictedObjectID(520);
                var record = new InstanceDetails(
                    1, 0, root, Vector3.zero, Quaternion.identity, null, null);
                AttachHierarchy(senderManager, hierarchy, record);
                RecordHierarchyVerifiedState(senderManager, hierarchy, 10, record);

                var id = new PredictedComponentID(root, 0);
                var sender = senderObject.AddComponent<OmittedStateIdentity>();
                var senderVerified = InitializeIdentity(sender, senderManager, id, 100);
                senderVerified.Write(10, FullState(100));
                sender.fullPredictedState = FullState(100);
                SetLocalTick(senderManager, 12);
                RegisterSystem(senderManager, sender);

                var timeline = new PlayerVisibilityTimeline();
                timeline.SetVisible(11, root, false);
                timeline.SetVisible(12, root, true);

                using var frame = BitPackerPool.Get();
                WriteAddressedStateSection(
                    senderManager,
                    timeline,
                    frame,
                    tick: 12,
                    baselineTick: 10,
                    fullFrame: false);

                AssertSingleRecord(frame, id, expectedFullState: true);

                // The receiver models a client that lost this root while it was culled: it is
                // registered again but holds no verified baseline, so only the full-state entry
                // the writer was forced to emit can resynchronize it.
                var receiverManager = CreateManager(receiverManagerObject);
                var receiver = receiverObject.AddComponent<OmittedStateIdentity>();
                var receiverVerified =
                    InitializeIdentity(receiver, receiverManager, id, 900);
                receiver.fullPredictedState = FullState(900);
                RegisterSystem(receiverManager, receiver);

                frame.ResetPositionAndMode(true);
                Assert.DoesNotThrow(() => ReadAddressedStateSection(
                    receiverManager,
                    frame,
                    stateTick: 12,
                    baselineTick: 10,
                    serverTick: 12));

                Assert.That(receiver.currentState.value, Is.EqualTo(100));
                Assert.That(receiver.lastVerifiedTick, Is.EqualTo(12));
                Assert.That(receiverVerified.ReadOrPrevious(12, out var resynced), Is.True);
                Assert.That(resynced.state.value, Is.EqualTo(100));
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(hierarchyObject);
                Object.DestroyImmediate(receiverManagerObject);
                Object.DestroyImmediate(senderManagerObject);
            }
        }

        private static void RegisterSystem(
            PredictionManager manager,
            PredictedIdentity identity)
        {
            var systems = GetField<List<PredictedIdentity>>(
                typeof(PredictionManager),
                manager,
                "_systems");
            systems.Add(identity);
            SetField(
                typeof(PredictionManager),
                manager,
                "_systemsCount",
                systems.Count);
            var instanceMap =
                GetField<Dictionary<PredictedComponentID, PredictedIdentity>>(
                    typeof(PredictionManager),
                    manager,
                    "_instanceMap");
            instanceMap[identity.id] = identity;
        }

        private static void WriteAddressedStateSection(
            PredictionManager manager,
            PlayerVisibilityTimeline timeline,
            BitPacker frame,
            ulong tick,
            ulong baselineTick,
            bool fullFrame)
        {
            var method = typeof(PredictionManager).GetMethod(
                "WriteAddressedStateSection",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(
                manager,
                new object[]
                {
                    default(PlayerID),
                    timeline,
                    frame,
                    tick,
                    baselineTick,
                    fullFrame,
                    false
                });
        }

        private static void ReadAddressedStateSection(
            PredictionManager manager,
            BitPacker frame,
            ulong stateTick,
            ulong baselineTick,
            ulong serverTick)
        {
            var method = typeof(PredictionManager).GetMethod(
                "ReadAddressedStateSection",
                InstanceFields);
            Assert.That(method, Is.Not.Null);
            method.Invoke(
                manager,
                new object[]
                {
                    frame,
                    stateTick,
                    baselineTick,
                    serverTick,
                    false,
                    false
                });
        }

        private static void AssertSingleRecord(
            BitPacker frame,
            PredictedComponentID expectedId,
            bool expectedFullState)
        {
            frame.ResetPositionAndMode(true);
            int count = 0;
            AddressedPredictionRecords.ReadSection(
                source: frame,
                readRecord: (id, isFullState, _, _) =>
                {
                    count++;
                    Assert.That(id, Is.EqualTo(expectedId));
                    Assert.That(isFullState, Is.EqualTo(expectedFullState));
                });
            Assert.That(count, Is.EqualTo(1));
        }
        private static PredictionManager CreateManager(GameObject managerObject)
        {
            var manager = managerObject.AddComponent<PredictionManager>();
            SetField(
                typeof(PredictionManager),
                manager,
                "<tickRate>k__BackingField",
                20);
            return manager;
        }

        private static History<FULL_STATE<OmittedState>> InitializeIdentity(
            OmittedStateIdentity identity,
            PredictionManager manager,
            PredictedComponentID id,
            int initialValue)
        {
            identity.AttachForTest(manager, id);
            identity.fullPredictedState = FullState(initialValue);

            var predicted = new History<FULL_STATE<OmittedState>>(200);
            predicted.Write(0, identity.fullPredictedState.DeepCopy());
            SetField(
                typeof(PredictedIdentity<OmittedState>),
                identity,
                "_stateHistory",
                predicted);

            var verified =
                manager.GetVerifiedHistory<FULL_STATE<OmittedState>>(id, out _);
            SetField(
                typeof(PredictedIdentity<OmittedState>),
                identity,
                "_verifiedHistory",
                verified);
            return verified;
        }

        private static void AttachIdentity(
            PredictedIdentity identity,
            PredictionManager manager,
            PredictedComponentID id)
        {
            identity.id = id;
            SetField(
                typeof(PredictedIdentity),
                identity,
                "<predictionManager>k__BackingField",
                manager);
        }

        private static void RecordHierarchyVerifiedState(
            PredictionManager manager,
            PredictedHierarchy hierarchy,
            ulong tick,
            params InstanceDetails[] records)
        {
            var spawned = DisposableList<InstanceDetails>.Create(records.Length);
            for (var i = 0; i < records.Length; i++)
                spawned.Add(records[i]);
            var state = new PredictedHierarchyState(
                spawned,
                DisposableList<PredictedObjectID>.Create(0),
                1000);
            try
            {
                var verified = manager.GetVerifiedHistory<
                    FULL_STATE<PredictedHierarchyState>>(hierarchy.id, out _);
                SetField(
                    typeof(PredictedIdentity<PredictedHierarchyState>),
                    hierarchy,
                    "_verifiedHistory",
                    verified);
                verified.Write(
                    tick,
                    new FULL_STATE<PredictedHierarchyState>
                    {
                        state = state.Duplicate()
                    });
            }
            finally
            {
                state.Dispose();
            }
        }
        private static void AttachHierarchy(
            PredictionManager manager,
            PredictedHierarchy hierarchy,
            params InstanceDetails[] records)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<hierarchy>k__BackingField",
                hierarchy);
            var spawned = GetField<List<InstanceDetails>>(
                typeof(PredictedHierarchy),
                hierarchy,
                "_spawnedPrefabs");
            var recordsById =
                GetField<Dictionary<PredictedObjectID, InstanceDetails>>(
                    typeof(PredictedHierarchy),
                    hierarchy,
                    "_recordsById");
            foreach (var record in records)
            {
                spawned.Add(record);
                recordsById.Add(record.instanceId, record);
            }
        }

        private static void MarkAllModulesDynamic(PredictedIdentity identity)
        {
            SetField(
                typeof(PredictedIdentity),
                identity,
                "_staticModuleCount",
                0);
        }

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            SetField(
                typeof(PredictionManager),
                manager,
                "<localTick>k__BackingField",
                tick);
        }

        private static FULL_STATE<OmittedState> FullState(int value)
        {
            return new FULL_STATE<OmittedState>
            {
                state = new OmittedState { value = value }
            };
        }

        private static MODULE_STATE<OmittedState> ModuleState(int value)
        {
            return new MODULE_STATE<OmittedState>
            {
                state = new OmittedState { value = value }
            };
        }

        private static DisposableList<uint> ModuleSet(uint hash)
        {
            var result = DisposableList<uint>.Create(1);
            result.Add(hash);
            return result;
        }

        private static T GetField<T>(
            Type declaringType,
            object target,
            string fieldName)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            return (T)field.GetValue(target);
        }

        private static void SetField(
            Type declaringType,
            object target,
            string fieldName,
            object value)
        {
            var field = declaringType.GetField(fieldName, InstanceFields);
            Assert.That(field, Is.Not.Null,
                $"Missing field {declaringType.FullName}.{fieldName}");
            field.SetValue(target, value);
        }
    }

    public struct OmittedState : IPredictedData<OmittedState>
    {
        public int value;

        public void Dispose() { }
    }

    public class OmittedStateIdentity : PredictedIdentity<OmittedState>
    {
        public void AttachForTest(
            PredictionManager manager,
            PredictedComponentID componentId)
        {
            predictionManager = manager;
            id = componentId;
            myType = GetType();
        }
    }

    public sealed class DeterministicOmittedIdentity
        : DeterministicIdentity<OmittedState>
    {
        public void AttachForTest(
            PredictionManager manager,
            PredictedComponentID componentId)
        {
            predictionManager = manager;
            id = componentId;
        }
    }

    public sealed class SoftOmittedStateIdentity : OmittedStateIdentity
    {
        public override bool supportsSoftCorrection => true;

        public int callbackCount;
        public int callbackPredicted;
        public int callbackVerified;

        protected override void OnVerifiedStateReceived(
            ulong tick,
            in OmittedState predicted,
            in OmittedState verified)
        {
            callbackCount++;
            callbackPredicted = predicted.value;
            callbackVerified = verified.value;
        }
    }
    public sealed class CustomOmittedStateIdentity : OmittedStateIdentity
    {
        public int writeDeltaCalls;

        protected override void WriteDeltaState(
            BitPacker packer,
            in OmittedState baseline,
            in OmittedState current)
        {
            writeDeltaCalls++;
            Packer<int>.Write(packer, current.value);
        }

        protected override void ReadDeltaState(
            BitPacker packer,
            in OmittedState baseline,
            ref OmittedState state)
        {
            state.value = Packer<int>.Read(packer);
        }
    }

    public sealed class OmittedAlphaModule : PredictedModule<OmittedState>
    {
        public OmittedAlphaModule(PredictedIdentity identity) : base(identity) { }
    }

    public sealed class OmittedBetaModule : PredictedModule<OmittedState>
    {
        public OmittedBetaModule(PredictedIdentity identity) : base(identity) { }
    }

    public sealed class CustomOmittedModule : PredictedModule<OmittedState>
    {
        public CustomOmittedModule(PredictedIdentity identity) : base(identity) { }

        protected override bool WriteState(
            PlayerID receiver,
            BitPacker packer,
            ulong baselineTick)
        {
            Packer<int>.Write(packer, 123);
            return false;
        }

        protected override void ReadState(
            ulong tick,
            BitPacker packer,
            ulong baselineTick,
            ulong serverTick)
        {
            Packer<int>.Read(packer);
        }
    }
}
