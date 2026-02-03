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

        [Obsolete("Use UpdateView() instead."), UsedImplicitly, MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual void UpdateView(StatelessHeSaid stateless, StatelessHeSaid? verified) => UpdateView();

        protected virtual void UpdateView() { }

        protected virtual void LateSimulate(float delta) {}

        internal override void LateSimulateTick(float delta) => LateSimulate(delta);

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate) { }

        internal override void SaveStateInHistory(ulong tick) { }

        internal override void Rollback(ulong tick) { }

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError) { }

        public override void ResetInterpolation() { }

        internal override void GetLatestUnityState() { }

        internal override void WriteFirstState(ulong tick, BitPacker packer) { }

        internal override bool WriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule) => false;

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable) { }

        internal override void ReadFirstState(ulong tick, BitPacker packer) { }

        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule) { }

        internal override void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable) { }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable) { }

        public override void WriteFirstInput(ulong localTick, BitPacker packer) { }

        public override void ReadFirstInput(ulong localTick, BitPacker packer) { }

        internal override void ClearFuture(ulong stateTick) { }
    }
}
