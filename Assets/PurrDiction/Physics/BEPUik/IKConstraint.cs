using BEPUutilities;
using FixMath.NET;
using System;

namespace BEPUik
{
    public abstract class IKConstraint
    {
        protected FP softness;

        protected FP errorCorrectionFactor;


        /// <summary>
        /// The rigidity of a constraint is used to derive the stiffness and damping coefficients using a fixed stiffness:damping ratio.
        /// </summary>
        /// <remarks>
        /// This is used over independent coefficients because IK usages of the constraints don't really vary in behavior, just strength.
        /// </remarks>
        private readonly FP StiffnessOverDamping = (FP)0.25m;

        private FP rigidity = F64.C16;
        /// <summary>
        /// Gets the rigidity of the constraint. Higher values correspond to more rigid constraints, lower values to less rigid constraints. Must be positive.
        /// </summary>
        /// <remarks>
        /// Scaling up the rigidity is like preparing the constraint to handle a heavier load. If the load is proportionally heavier, the damping ratio stays the same. 
        /// If the load stays the same but the rigidity increases, the result is a more rigid joint, but with a slightly different damping ratio.
        /// In other words, modifying rigidity without modifying the effective mass of the system results in a variable damping ratio. 
        /// This isn't a huge problem in practice- there is a massive ultra-damping hack in IK bone position integration that make a little physical deviation or underdamping irrelevant.
        /// </remarks>
        public FP Rigidity
        {
            get
            {
                return rigidity;
            }
            set
            {
                if (value <= F64.C0)
                    throw new ArgumentException("Rigidity must be positive.");
                rigidity = value;
            }
        }

        protected FP maximumImpulse;
        protected FP maximumImpulseSquared;
        protected FP maximumForce = FP.MaxValue;

        /// <summary>
        /// Gets or sets the maximum force that the constraint can apply.
        /// </summary>
        public FP MaximumForce
        {
            get { return maximumForce; }
            set
            {
                maximumForce = MathHelper.Max(value, F64.C0);
            }
        }

        /// <summary>
        /// Updates the softness, bias factor, and maximum impulse based on the current time step.
        /// </summary>
        /// <param name="dt">Time step duration.</param>
        /// <param name="updateRate">Inverse time step duration.</param>
        protected internal void Preupdate(FP dt, FP updateRate)
        {
            FP stiffness = StiffnessOverDamping * rigidity;
            FP damping = rigidity;
            FP multiplier = F64.C1 / (dt * stiffness + damping);
            errorCorrectionFactor = stiffness * multiplier;
            softness = updateRate * multiplier;
            maximumImpulse = maximumForce * dt;
            maximumImpulseSquared = FP.SafeMul(maximumImpulse, maximumImpulse);

        }

        /// <summary>
        /// Update the jacobians for the latest position and orientation bone states and store a velocity bias based on the error.
        /// </summary>
        protected internal abstract void UpdateJacobiansAndVelocityBias();

        /// <summary>
        /// Computes the effective mass matrix for the constraint for the current jacobians.
        /// </summary>
        protected internal abstract void ComputeEffectiveMass();

        /// <summary>
        /// Applies the accumulated impulse to warm up the constraint solving process.
        /// </summary>
        protected internal abstract void WarmStart();

        /// <summary>
        /// Applies impulses to satisfy the velocity constraint.
        /// </summary>
        protected internal abstract void SolveVelocityIteration();

        /// <summary>
        /// Clears out the accumulated impulse.
        /// </summary>
        protected internal abstract void ClearAccumulatedImpulses();
    }
}
