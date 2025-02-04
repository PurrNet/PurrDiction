

using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.Constraints
{
    /// <summary>
    /// Implemented by solver updateables which have a one dimensional impulse.
    /// </summary>
    public interface I1DImpulseConstraint
    {
        /// <summary>
        /// Gets the current relative velocity of the constraint.
        /// Computed based on the current connection velocities and jacobians.
        /// </summary>
        FP RelativeVelocity { get; }

        /// <summary>
        /// Gets the total impulse a constraint has applied.
        /// </summary>
        FP TotalImpulse { get; }
    }

    /// <summary>
    /// Implemented by solver updateables which have a one dimensional impulse.
    /// </summary>
    public interface I1DImpulseConstraintWithError : I1DImpulseConstraint
    {
        /// <summary>
        /// Gets the current constraint error.
        /// </summary>
        FP Error { get; }
    }

    /// <summary>
    /// Implemented by solver updateables which have a two dimensional impulse.
    /// </summary>
    public interface I2DImpulseConstraint
    {
        /// <summary>
        /// Gets the current relative velocity of the constraint.
        /// Computed based on the current connection velocities and jacobians.
        /// </summary>
        FPVector2 RelativeVelocity { get; }

        /// <summary>
        /// Gets the total impulse a constraint has applied.
        /// </summary>
        FPVector2 TotalImpulse { get; }
    }

    /// <summary>
    /// Implemented by solver updateables which have a two dimensional impulse.
    /// </summary>
    public interface I2DImpulseConstraintWithError : I2DImpulseConstraint
    {
        /// <summary>
        /// Gets the current constraint error.
        /// </summary>
        FPVector2 Error { get; }
    }

    /// <summary>
    /// Implemented by solver updateables which have a three dimensional impulse.
    /// </summary>
    public interface I3DImpulseConstraint
    {
        /// <summary>
        /// Gets the current relative velocity of the constraint.
        /// Computed based on the current connection velocities and jacobians.
        /// </summary>
        FPVector3 RelativeVelocity { get; }

        /// <summary>
        /// Gets the total impulse a constraint has applied.
        /// </summary>
        FPVector3 TotalImpulse { get; }
    }

    /// <summary>
    /// Implemented by solver updateables which have a three dimensional impulse.
    /// </summary>
    public interface I3DImpulseConstraintWithError : I3DImpulseConstraint
    {
        /// <summary>
        /// Gets the current constraint error.
        /// </summary>
        FPVector3 Error { get; }
    }
}