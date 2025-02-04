using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Paths
{
    /// <summary>
    /// Wrapper around an orientation curve that specifies a specific velocity at which to travel.
    /// </summary>
    public class ConstantAngularSpeedCurve : ConstantSpeedCurve<FPQuaternion>
    {
        /// <summary>
        /// Constructs a new constant speed curve.
        /// </summary>
        /// <param name="speed">Speed to maintain while traveling around a curve.</param>
        /// <param name="curve">Curve to wrap.</param>
        public ConstantAngularSpeedCurve(FP speed, Curve<FPQuaternion> curve)
            : base(speed, curve)
        {
        }

        /// <summary>
        /// Constructs a new constant speed curve.
        /// </summary>
        /// <param name="speed">Speed to maintain while traveling around a curve.</param>
        /// <param name="curve">Curve to wrap.</param>
        /// <param name="sampleCount">Number of samples to use when constructing the wrapper curve.
        /// More samples increases the accuracy of the speed requirement at the cost of performance.</param>
        public ConstantAngularSpeedCurve(FP speed, Curve<FPQuaternion> curve, int sampleCount)
            : base(speed, curve, sampleCount)
        {
        }

        protected override FP GetDistance(FPQuaternion start, FPQuaternion end)
        {
            FPQuaternion.Conjugate(ref end, out end);
            FPQuaternion.Multiply(ref end, ref start, out end);
            return FPQuaternion.GetAngleFromQuaternion(ref end);
        }
    }
}