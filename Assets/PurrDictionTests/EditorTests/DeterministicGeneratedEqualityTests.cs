using System;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class DeterministicGeneratedEqualityTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [OneTimeSetUp]
        public void RegisterGeneratedCode()
        {
            // Use the actual postprocessed packers and generated equality; do not replace
            // PurrEquality with a test comparer or supply a custom PurrEquals implementation.
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType(typeof(DeterministicGeneratedEqualityState));
            Assert.That(typeof(IPurrEquatable<DeterministicGeneratedEqualityState>)
                .IsAssignableFrom(typeof(DeterministicGeneratedEqualityState)), Is.True,
                "The runtime fixture must be processed by PurrNet's actual IL postprocessor.");
            Assert.That(typeof(DeterministicGeneratedEqualityState).GetMethod("PurrEquals"), Is.Not.Null);
        }

        [Test]
        public void FullStateReadAppliesTheExactTinyIncomingVector()
        {
            var previous = new DeterministicGeneratedEqualityState
            {
                label = "same",
                position = Vector3.zero
            };
            var incoming = new DeterministicGeneratedEqualityState
            {
                label = "same",
                position = new Vector3(0.000001f, 0f, 0f)
            };

            Assert.That(previous.position.x, Is.Not.EqualTo(incoming.position.x));
            Assert.That(previous.position == incoming.position, Is.True,
                "This change is small enough for Unity's approximate Vector3 operator to miss.");
            Assert.That(Packer.AreEqualRef(ref previous, ref incoming), Is.False,
                "The real generated comparer must preserve the tiny serialized difference.");

            var managerObject = new GameObject("Generated equality manager");
            var identityObject = new GameObject("Generated equality deterministic identity");
            PredictionManager manager = null;
            try
            {
                manager = managerObject.AddComponent<PredictionManager>();
                Set(typeof(PredictionManager), manager, "<tickRate>k__BackingField", 60);
                var identity = identityObject.AddComponent<DeterministicGeneratedEqualityProbe>();
                identity.Attach(manager, new PredictedComponentID(new PredictedObjectID(2351), 0));
                Set(typeof(DeterministicIdentity<DeterministicGeneratedEqualityState>), identity, "myType", identity.GetType());
                var metadata = new PredictedIdentityState { wasOnSimulationStartCalled = true };
                var history = new History<FULL_STATE<DeterministicGeneratedEqualityState>>(600);
                history.Write(20, new FULL_STATE<DeterministicGeneratedEqualityState>
                {
                    state = previous,
                    prediction = metadata
                });
                // A speculative future must be discarded by the normal correction sequence.
                history.Write(21, new FULL_STATE<DeterministicGeneratedEqualityState>
                {
                    state = new DeterministicGeneratedEqualityState
                    {
                        label = "future",
                        position = new Vector3(100f, 0f, 0f)
                    },
                    prediction = metadata
                });
                Set(typeof(DeterministicIdentity<DeterministicGeneratedEqualityState>), identity, "_stateHistory", history);
                identity.fullPredictedState = history[1].DeepCopy();

                using var payload = BitPackerPool.Get();
                Packer<PredictedIdentityState>.Write(payload, metadata);
                Packer<DeterministicGeneratedEqualityState>.Write(payload, incoming);

                // Verify the ordinary full-state serializer preserves the small difference.
                // A passing test must not mistake wire quantization for the history bug.
                payload.ResetPositionAndMode(true);
                PredictedIdentityState decodedMetadata = default;
                DeterministicGeneratedEqualityState decoded = default;
                Packer<PredictedIdentityState>.Read(payload, ref decodedMetadata);
                Packer<DeterministicGeneratedEqualityState>.Read(payload, ref decoded);
                Assert.That(decoded.position.x, Is.EqualTo(incoming.position.x));
                decoded.Dispose();
                decodedMetadata.Dispose();

                // Entering states travel as deltas against the identity's initial state.
                payload.ResetPositionAndMode(false);
                DeltaPacker<PredictedIdentityState>.Write(payload, default, metadata);
                DeltaPacker<DeterministicGeneratedEqualityState>.Write(payload, default, incoming);
                payload.ResetPositionAndMode(true);
                // This is the production full-record ordering in ApplyAddressedState.
                identity.RunClearFuture(20);
                identity.ReadFirstState(20, payload, 20);
                identity.RunRollback(20);

                Assert.That(history.Read(21, out _), Is.False);
                Assert.That(identity.currentState.position.x, Is.EqualTo(incoming.position.x),
                    "A full authoritative deterministic state must replace the old state with the exact incoming value.");
                Assert.That(identity.currentState.label, Is.EqualTo(incoming.label));
                Assert.That(history.Read(20, out var corrected), Is.True);
                Assert.That(corrected.state.position.x, Is.EqualTo(incoming.position.x));
            }
            finally
            {
                Object.DestroyImmediate(identityObject);
                if (manager)
                    typeof(PredictionManager).GetMethod("ClearVerifiedStores", Fields).Invoke(manager, null);
                Object.DestroyImmediate(managerObject);
            }
        }

        static void Set(Type type, object instance, string field, object value)
        {
            var info = type.GetField(field, Fields);
            Assert.That(info, Is.Not.Null, $"Missing field {type.FullName}.{field}");
            info.SetValue(instance, value);
        }
    }

}
