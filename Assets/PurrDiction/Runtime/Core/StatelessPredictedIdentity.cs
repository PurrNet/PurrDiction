using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public struct StatelessHeSaid : IPredictedData<StatelessHeSaid>
    {
        public void Dispose() { }
    }

    public abstract class StatelessPredictedIdentity : PredictedIdentity
    {
        public sealed override bool supportsSoftCorrection => false;

        [UsedImplicitly]
        public new PlayerID? owner { get; }

        private static StatelessHeSaid _stateless;

        [UsedImplicitly]
        public new T RegisterModule<T>(T module) where T : PredictedModule => throw new NotSupportedException("StatelessPredictedIdentity does not support modules.");

        [Obsolete("Use Simulate(float delta) instead."), UsedImplicitly, MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void Simulate(ref StatelessHeSaid state, float delta) => Simulate(delta);

        protected virtual void Simulate(float delta) {}

        internal override void SimulateTick(ulong tick, float delta)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            Simulate(ref _stateless, delta);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        internal override void UpdateView(float deltaTime)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            UpdateView(default, default);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        internal override void LateUpdateView(float deltaTime)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            LateUpdateView(default, default);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        [Obsolete("Use UpdateView() instead."), UsedImplicitly, MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void UpdateView(StatelessHeSaid stateless, StatelessHeSaid? verified) => UpdateView();

        [Obsolete("Use LateUpdateView() instead."), UsedImplicitly, MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void LateUpdateView(StatelessHeSaid stateless, StatelessHeSaid? verified) => LateUpdateView();

        protected virtual void UpdateView() { }

        protected virtual void LateUpdateView() { }

        protected virtual void LateSimulate(float delta) {}

        internal override void LateSimulateTick(float delta) => LateSimulate(delta);

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate) { }

        internal override void SaveStateInHistory(ulong tick) { }

        internal override void Rollback(ulong tick) { }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }

        public override void ResetInterpolation() { }

        internal override void GetLatestUnityState() { }

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var metadata = new PredictedIdentityState { owner = base.owner };
            RefreshMetadataLedger(tick, in metadata);
            Packer<PredictedIdentityState>.Write(packer, metadata);
        }

        internal override bool WriteCurrentState(PlayerID receiver, BitPacker packer, ulong baselineTick)
        {
            var metadata = new PredictedIdentityState { owner = base.owner };
            return WritePredictionMetadata(packer, baselineTick, in metadata);
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer, ulong serverTick)
        {
            PredictedIdentityState metadata = default;
            Packer<PredictedIdentityState>.Read(packer, ref metadata);
            StoreVerifiedMetadata(serverTick, in metadata);
            SetOwner(metadata.owner);
        }

        internal override void ReadState(ulong tick, BitPacker packer, ulong baselineTick, ulong serverTick)
        {
            PredictedIdentityState metadata = default;
            ReadPredictionMetadata(packer, baselineTick, serverTick, ref metadata);
            SetOwner(metadata.owner);
        }

        internal override bool HasUnchangedStateBaseline(ulong baselineTick)
            => TryGetPredictionMetadataBaseline(baselineTick, out _);

        internal override void ReadUnchangedState(
            ulong tick,
            ulong baselineTick,
            ulong serverTick)
        {
            if (!TryGetPredictionMetadataBaseline(baselineTick, out var metadata))
                throw new InvalidOperationException($"Missing metadata baseline at tick {baselineTick}.");
            StoreVerifiedMetadata(serverTick, in metadata);
            SetOwner(metadata.owner);
        }

        internal override void QueueInput(BitPacker packer, PlayerID sender) { }

        public override void WriteFirstInput(ulong localTick, BitPacker packer) { }

        public override void ReadFirstInput(ulong localTick, BitPacker packer) { }

        internal override void ClearFuture(ulong stateTick) { }
    }
}
