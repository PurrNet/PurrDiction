

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Paths
{
    /// <summary>
    /// Wrapper around a 3d position curve that specifies a specific velocity at which to travel.
    /// </summary>
    public class ConstantLinearSpeedCurve : ConstantSpeedCurve<FPVector3>
    {
        /// <summary>
        /// Constructs a new constant speed curve.
        /// </summary>
        /// <param name="speed">Speed to maintain while traveling around a curve.</param>
        /// <param name="curve">Curve to wrap.</param>
        public ConstantLinearSpeedCurve(FP speed, Curve<FPVector3> curve)
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
        public ConstantLinearSpeedCurve(FP speed, Curve<FPVector3> curve, int sampleCount)
            : base(speed, curve, sampleCount)
        {
        }

        protected override FP GetDistance(FPVector3 start, FPVector3 end)
        {
            FP distance;
            FPVector3.Distance(ref start, ref end, out distance);
            return distance;
        }
    }
}