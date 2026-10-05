using System;
using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class AuthoritativeStateEqualityTests
    {
        private WriteFunc<DeterministicGeneratedEqualityState> _previousManagedWriter;
        private WriteFunc<DeterministicGeneratedEqualityState> _previousManagedDirectWriter;
        private WriteFunc<AuthorityEqualityUnmanagedProbe> _previousUnmanagedWriter;
        private WriteFunc<AuthorityEqualityUnmanagedProbe> _previousUnmanagedDirectWriter;

        [SetUp]
        public void SetUp()
        {
            NetworkManager.CallAllRegisters();
            Assert.That(typeof(IPurrEquatable<DeterministicGeneratedEqualityState>)
                .IsAssignableFrom(typeof(DeterministicGeneratedEqualityState)), Is.True,
                "Managed state comparison must exercise the real generated comparer.");
            _previousManagedWriter = Packer<DeterministicGeneratedEqualityState>.WriteFunc;
            _previousManagedDirectWriter = Packer<DeterministicGeneratedEqualityState>.DirectWrite;
            _previousUnmanagedWriter = Packer<AuthorityEqualityUnmanagedProbe>.WriteFunc;
            _previousUnmanagedDirectWriter = Packer<AuthorityEqualityUnmanagedProbe>.DirectWrite;
            // Registration intentionally ignores an existing writer. Override these delegates
            // directly so a comparison that serializes cannot silently pass this regression.
            Packer<DeterministicGeneratedEqualityState>.WriteFunc =
                Packer<DeterministicGeneratedEqualityState>.DirectWrite = (packer, value) =>
                    throw new InvalidOperationException("managed state equality must not serialize");
            Packer<AuthorityEqualityUnmanagedProbe>.WriteFunc =
                Packer<AuthorityEqualityUnmanagedProbe>.DirectWrite = (packer, value) =>
                    throw new InvalidOperationException("unmanaged equality must not serialize");
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousManagedWriter != null)
                Packer<DeterministicGeneratedEqualityState>.WriteFunc = _previousManagedWriter;
            if (_previousManagedDirectWriter != null)
                Packer<DeterministicGeneratedEqualityState>.DirectWrite = _previousManagedDirectWriter;
            if (_previousUnmanagedWriter != null)
                Packer<AuthorityEqualityUnmanagedProbe>.WriteFunc = _previousUnmanagedWriter;
            if (_previousUnmanagedDirectWriter != null)
                Packer<AuthorityEqualityUnmanagedProbe>.DirectWrite = _previousUnmanagedDirectWriter;
        }

        [Test]
        public void FullStateEqualityDetectsTinyManagedValueChangesWithoutSerialization()
        {
            var left = new FULL_STATE<DeterministicGeneratedEqualityState> { state = Value(0f) };
            var right = left;
            right.state.label = new string(left.state.label.ToCharArray());
            Assert.That(left.HasSameContents(ref right), Is.True);
            right.state.position.x = 0.000001f;
            Assert.That(Packer.AreEqualRef(ref left.state, ref right.state), Is.False);
            Assert.That(left.HasSameContents(ref right), Is.False);
            right.state = Value(0f, "different");
            Assert.That(left.HasSameContents(ref right), Is.False);
        }

        [Test]
        public void ModuleStateEqualityDetectsTinyManagedValueChangesWithoutSerialization()
        {
            var left = new MODULE_STATE<DeterministicGeneratedEqualityState> { state = Value(0f) };
            var right = left;
            right.state.label = new string(left.state.label.ToCharArray());
            Assert.That(left.HasSameContents(ref right), Is.True);
            right.state.position.x = 0.000001f;
            Assert.That(left.HasSameContents(ref right), Is.False);
            right.state = Value(0f, "different");
            Assert.That(left.HasSameContents(ref right), Is.False);
        }

        [Test]
        public void FullStateEqualityIncludesPredictionMetadata()
        {
            var left = new FULL_STATE<DeterministicGeneratedEqualityState> { state = Value(0f) };
            var right = left;
            right.prediction.wasOnSimulationStartCalled = true;
            Assert.That(left.HasSameContents(ref right), Is.False);
        }

        [Test]
        public void ModuleStateEqualityIncludesPredictionMetadata()
        {
            var left = new MODULE_STATE<DeterministicGeneratedEqualityState> { state = Value(0f) };
            var right = left;
            right.prediction.wasOnSimulationStartCalled = true;
            Assert.That(left.HasSameContents(ref right), Is.False);
        }

        [Test]
        public void UnmanagedFullAndModuleStatesCompareWithoutSerialization()
        {
            var leftFull = new FULL_STATE<AuthorityEqualityUnmanagedProbe>
                { state = new AuthorityEqualityUnmanagedProbe { value = 31 } };
            var rightFull = leftFull;
            Assert.That(leftFull.HasSameContents(ref rightFull), Is.True);
            rightFull.state.value++;
            Assert.That(leftFull.HasSameContents(ref rightFull), Is.False);

            var leftModule = new MODULE_STATE<AuthorityEqualityUnmanagedProbe> { state = leftFull.state };
            var rightModule = leftModule;
            Assert.That(leftModule.HasSameContents(ref rightModule), Is.True);
            rightModule.state.value++;
            Assert.That(leftModule.HasSameContents(ref rightModule), Is.False);
        }

        private static DeterministicGeneratedEqualityState Value(float x, string label = "same")
            => new() { label = label, position = new Vector3(x, 0f, 0f) };
    }

    public struct AuthorityEqualityUnmanagedProbe : IPredictedData<AuthorityEqualityUnmanagedProbe>
    {
        public int value;
        public void Dispose() { }
    }
}
