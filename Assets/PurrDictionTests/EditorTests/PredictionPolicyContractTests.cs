using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionPolicyContractTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(PredictedIdentityState));
        }

        [Test]
        public void DeterministicIdentityKeepsSoftCorrectionButRelaysBehaviorally()
        {
            var gameObject = new GameObject(
                nameof(DeterministicIdentityKeepsSoftCorrectionButRelaysBehaviorally));
            var managerObject = new GameObject("PredictionManager");
            try
            {
                var identity = gameObject.AddComponent<DeterministicSoftCorrectionProbe>();
                var manager = managerObject.AddComponent<PredictionManager>();

                identity.configuredPredictionPolicy = PredictionPolicy.SoftCorrection;
                AttachManager(identity, manager);
                identity.SetPredictionPolicy(identity.GetResolvedPredictionPolicy());

                Assert.That(identity.supportsSoftCorrection, Is.True,
                    "The regression probe must exercise a deterministic subclass that opts in");
                Assert.That(identity.configuredPredictionPolicy, Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(identity.GetResolvedPredictionPolicy(), Is.EqualTo(PredictionPolicy.SoftCorrection));
                Assert.That(identity.EffectivePolicy(), Is.EqualTo(PredictionPolicy.ServerRelay));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void StatelessFirstStateSynchronizesCurrentOwner()
        {
            var senderObject = new GameObject("StatelessOwnerSender");
            var receiverObject = new GameObject("StatelessOwnerReceiver");
            var senderManagerObject = new GameObject("StatelessOwnerSenderManager");
            var receiverManagerObject = new GameObject("StatelessOwnerReceiverManager");
            try
            {
                var sender = senderObject.AddComponent<StatelessOwnerProbe>();
                var receiver = receiverObject.AddComponent<StatelessOwnerProbe>();
                AttachManager(sender, senderManagerObject.AddComponent<PredictionManager>());
                AttachManager(receiver, receiverManagerObject.AddComponent<PredictionManager>());
                var expected = new PlayerID(new PackedULong(42), false);
                sender.AssignOwner(expected);

                using var packer = BitPackerPool.Get();
                sender.WriteFirstStateForTest(packer);
                packer.ResetPositionAndMode(true);
                receiver.ReadFirstStateForTest(packer);

                Assert.That(receiver.assignedOwner, Is.EqualTo(expected));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderManagerObject);
                Object.DestroyImmediate(receiverManagerObject);
            }
        }

        [Test]
        public void StaticFirstStateSynchronizesCurrentOwner()
        {
            var senderObject = new GameObject("StaticOwnerSender");
            var receiverObject = new GameObject("StaticOwnerReceiver");
            var senderManagerObject = new GameObject("StaticOwnerSenderManager");
            var receiverManagerObject = new GameObject("StaticOwnerReceiverManager");
            try
            {
                var sender = senderObject.AddComponent<StaticPredictedIdentity>();
                var receiver = receiverObject.AddComponent<StaticPredictedIdentity>();
                AttachManager(sender, senderManagerObject.AddComponent<PredictionManager>());
                AttachManager(receiver, receiverManagerObject.AddComponent<PredictionManager>());
                var expected = new PlayerID(new PackedULong(84), false);
                sender.SetOwner(expected);

                using var packer = BitPackerPool.Get();
                sender.WriteFirstState(0, packer);
                packer.ResetPositionAndMode(true);
                receiver.ReadFirstState(0, packer, 0);

                Assert.That(receiver.owner, Is.EqualTo(expected));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderManagerObject);
                Object.DestroyImmediate(receiverManagerObject);
            }
        }

        [Test]
        public void StaticCurrentStateSynchronizesPostSpawnOwnerChange()
        {
            var senderObject = new GameObject("StaticOwnerDeltaSender");
            var receiverObject = new GameObject("StaticOwnerDeltaReceiver");
            var senderManagerObject = new GameObject("StaticOwnerDeltaSenderManager");
            var receiverManagerObject = new GameObject("StaticOwnerDeltaReceiverManager");
            try
            {
                var sender = senderObject.AddComponent<StaticPredictedIdentity>();
                var receiver = receiverObject.AddComponent<StaticPredictedIdentity>();
                var senderManager = senderManagerObject.AddComponent<PredictionManager>();
                AttachManager(sender, senderManager);
                AttachManager(receiver, receiverManagerObject.AddComponent<PredictionManager>());
                var receiverPlayer = new PlayerID(new PackedULong(7), false);
                var expectedOwner = new PlayerID(new PackedULong(126), false);

                using (var initialPacker = BitPackerPool.Get())
                {
                    Assert.That(sender.WriteCurrentState(receiverPlayer, initialPacker, 0), Is.True);
                    initialPacker.ResetPositionAndMode(true);
                    receiver.ReadState(1, initialPacker, 0, 1);
                    Assert.That(receiver.owner, Is.Null);
                }

                SetLocalTick(senderManager, 10);
                sender.SetOwner(expectedOwner);

                using (var packer = BitPackerPool.Get())
                {
                    Assert.That(sender.WriteCurrentState(receiverPlayer, packer, 1), Is.True);
                    packer.ResetPositionAndMode(true);
                    receiver.ReadState(10, packer, 1, 10);

                    Assert.That(receiver.owner, Is.EqualTo(expectedOwner));
                }

                using (var unchangedPacker = BitPackerPool.Get())
                {
                    Assert.That(sender.WriteCurrentState(receiverPlayer, unchangedPacker, 10), Is.False);
                    unchangedPacker.ResetPositionAndMode(true);
                    receiver.ReadState(11, unchangedPacker, 10, 11);

                    Assert.That(receiver.owner, Is.EqualTo(expectedOwner));
                }
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderManagerObject);
                Object.DestroyImmediate(receiverManagerObject);
            }
        }

        [Test]
        public void StatelessCurrentStateSynchronizesPostSpawnOwnerChange()
        {
            var senderObject = new GameObject("StatelessOwnerDeltaSender");
            var receiverObject = new GameObject("StatelessOwnerDeltaReceiver");
            var senderManagerObject = new GameObject("StatelessOwnerDeltaSenderManager");
            var receiverManagerObject = new GameObject("StatelessOwnerDeltaReceiverManager");
            try
            {
                var sender = senderObject.AddComponent<StatelessOwnerProbe>();
                var receiver = receiverObject.AddComponent<StatelessOwnerProbe>();
                AttachManager(sender, senderManagerObject.AddComponent<PredictionManager>());
                AttachManager(receiver, receiverManagerObject.AddComponent<PredictionManager>());
                var receiverPlayer = new PlayerID(new PackedULong(8), false);
                var expectedOwner = new PlayerID(new PackedULong(127), false);

                sender.AssignOwner(expectedOwner);

                using var packer = BitPackerPool.Get();
                Assert.That(sender.WriteCurrentState(receiverPlayer, packer, 0), Is.True);
                packer.ResetPositionAndMode(true);
                receiver.ReadState(10, packer, 0, 10);

                Assert.That(receiver.assignedOwner, Is.EqualTo(expectedOwner));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderManagerObject);
                Object.DestroyImmediate(receiverManagerObject);
            }
        }

        [Test]
        public void DeterministicCurrentStateAppliesPostSpawnOwnerAtVerifiedRollback()
        {
            var senderManagerObject = new GameObject("DeterministicOwnerSenderManager");
            var receiverManagerObject = new GameObject("DeterministicOwnerReceiverManager");
            var senderObject = new GameObject("DeterministicOwnerDeltaSender");
            var receiverObject = new GameObject("DeterministicOwnerDeltaReceiver");
            try
            {
                const ulong verifiedTick = 10;
                var senderManager = senderManagerObject.AddComponent<PredictionManager>();
                var receiverManager = receiverManagerObject.AddComponent<PredictionManager>();
                var sender = senderObject.AddComponent<DeterministicOwnerProbe>();
                var receiver = receiverObject.AddComponent<DeterministicOwnerProbe>();
                var receiverPlayer = new PlayerID(new PackedULong(9), false);
                var expectedOwner = new PlayerID(new PackedULong(128), false);
                sender.AttachForTest(senderManager);
                receiver.AttachForTest(receiverManager);
                SeedDeterministicHistory(receiver, verifiedTick - 1);
                sender.AssignOwner(expectedOwner);

                using var packer = BitPackerPool.Get();
                Assert.That(sender.WriteCurrentState(receiverPlayer, packer, 0), Is.True);
                packer.ResetPositionAndMode(true);

                receiver.ClearFuture(verifiedTick);
                receiver.ReadState(verifiedTick, packer, 0, verifiedTick);

                Assert.That(receiver.assignedOwner, Is.Null,
                    "Verified deterministic metadata must remain in history until rollback applies its tick");

                receiver.Rollback(verifiedTick);

                Assert.That(receiver.assignedOwner, Is.EqualTo(expectedOwner));
            }
            finally
            {
                Object.DestroyImmediate(senderObject);
                Object.DestroyImmediate(receiverObject);
                Object.DestroyImmediate(senderManagerObject);
                Object.DestroyImmediate(receiverManagerObject);
            }
        }

        [Test]
        public void LegacyProtectedPredictionKeyRemainsAvailableToCustomModules()
        {
            Assert.That(typeof(LegacyPredictionKeyModule).GetProperty(
                nameof(LegacyPredictionKeyModule.legacyPredictionKey)), Is.Not.Null);
        }

        private static void AttachManager(PredictedIdentity identity, PredictionManager manager)
        {
            var managerField = typeof(PredictedIdentity).GetField(
                "<predictionManager>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(managerField, Is.Not.Null);
            managerField.SetValue(identity, manager);
        }

        private static void SetLocalTick(PredictionManager manager, ulong tick)
        {
            var tickField = typeof(PredictionManager).GetField(
                "<localTick>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(tickField, Is.Not.Null);
            tickField.SetValue(manager, tick);
        }

        private static void SeedDeterministicHistory(DeterministicOwnerProbe identity, ulong tick)
        {
            var history = new History<FULL_STATE<PolicyContractState>>(32);
            history.Write(tick, identity.fullPredictedState.DeepCopy());
            var historyField = typeof(DeterministicIdentity<PolicyContractState>).GetField(
                "_stateHistory",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(historyField, Is.Not.Null);
            historyField.SetValue(identity, history);
        }
    }

    public struct PolicyContractState : IPredictedData<PolicyContractState>
    {
        public void Dispose() { }
    }

    public sealed class DeterministicSoftCorrectionProbe : DeterministicIdentity<PolicyContractState>
    {
        public override bool supportsSoftCorrection => true;
    }

    public sealed class DeterministicOwnerProbe : DeterministicIdentity<PolicyContractState>
    {
        public PlayerID? assignedOwner => owner;

        public void AttachForTest(PredictionManager manager) => predictionManager = manager;

        public void AssignOwner(PlayerID? player) => SetOwner(player);
    }

    public sealed class StatelessOwnerProbe : StatelessPredictedIdentity
    {
        public PlayerID? assignedOwner => owner;

        public void AssignOwner(PlayerID? player) => SetOwner(player);

        public void WriteFirstStateForTest(BitPacker packer) => WriteFirstState(0, packer);

        public void ReadFirstStateForTest(BitPacker packer) => ReadFirstState(0, packer, 0);
    }

    public sealed class LegacyPredictionKeyModule : PredictedModule<PolicyContractState>
    {
        public LegacyPredictionKeyModule(PredictedIdentity identity) : base(identity) { }

        public ModuleDeltaKey<PredictedIdentityState> legacyPredictionKey => predictionKey;
    }
}
