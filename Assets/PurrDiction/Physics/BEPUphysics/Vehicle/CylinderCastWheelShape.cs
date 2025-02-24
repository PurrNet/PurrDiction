using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.BroadPhaseEntries.MobileCollidables;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.Entities;
using BEPUphysics.CollisionRuleManagement;
using BEPUutilities;
using BEPUphysics.Materials;
using FixMath.NET;

namespace BEPUphysics.Vehicle
{
    /// <summary>
    /// Uses a cylinder cast as the shape of a wheel.
    /// </summary>
    public class CylinderCastWheelShape : WheelShape
    {
        private CylinderShape shape;

        private FPQuaternion localWheelOrientation;
        /// <summary>
        /// Gets or sets the unsteered orientation of the wheel in the vehicle's local space.
        /// </summary>
        public FPQuaternion LocalWheelOrientation
        {
            get { return localWheelOrientation; }
            set { localWheelOrientation = value; }
        }

        /// <summary>
        /// Creates a new cylinder cast based wheel shape.
        /// </summary>
        /// <param name="radius">Radius of the wheel.</param>
        /// <param name="width">Width of the wheel.</param>
        /// <param name="localWheelOrientation">Unsteered orientation of the wheel in the vehicle's local space.</param>
        /// <param name="localGraphicTransform">Local graphic transform of the wheel shape.
        /// This transform is applied first when creating the shape's worldTransform.</param>
        /// <param name="includeSteeringTransformInCast">Whether or not to include the steering transform in the wheel shape cast. If false, the casted wheel shape will always point straight forward.
        /// If true, it will rotate with steering. Sometimes, setting this to false is helpful when the cast shape would otherwise become exposed when steering.</param>
        public CylinderCastWheelShape(FP radius, FP width, FPQuaternion localWheelOrientation, Matrix localGraphicTransform, bool includeSteeringTransformInCast)
        {
            shape = new CylinderShape(width, radius);
            this.LocalWheelOrientation = localWheelOrientation;
            LocalGraphicTransform = localGraphicTransform;
            this.IncludeSteeringTransformInCast = includeSteeringTransformInCast;
        }

        /// <summary>
        /// Gets or sets the radius of the wheel.
        /// </summary>
        public override sealed FP Radius
        {
            get { return shape.Radius; }
            set
            {
                shape.Radius = MathHelper.Max(value, F64.C0);
                Initialize();
            }
        }

        /// <summary>
        /// Gets or sets the width of the wheel.
        /// </summary>
        public FP Width
        {
            get { return shape.Height; }
            set
            {
                shape.Height = MathHelper.Max(value, F64.C0);
                Initialize();
            }
        }

        /// <summary>
        /// Gets or sets whether or not to include the rotation from steering in the cast. If false, the casted wheel shape will always point straight forward.
        /// If true, it will rotate with steering. Sometimes, setting this to false is helpful when the cast shape would otherwise become exposed when steering.
        /// </summary>
        public bool IncludeSteeringTransformInCast { get; set; }

        /// <summary>
        /// Updates the wheel's world transform for graphics.
        /// Called automatically by the owning wheel at the end of each frame.
        /// If the engine is updating asynchronously, you can call this inside of a space read buffer lock
        /// and update the wheel transforms safely.
        /// </summary>
        public override void UpdateWorldTransform()
        {
#if !WINDOWS
            FPVector3 newPosition = new FPVector3();
#else
            Vector3 newPosition;
#endif
            FPVector3 worldAttachmentPoint;
            FPVector3 localAttach;
            FPVector3.Add(ref wheel.suspension.localAttachmentPoint, ref wheel.vehicle.Body.CollisionInformation.localPosition, out localAttach);
            worldTransform = Matrix3x3.ToMatrix4X4(wheel.vehicle.Body.BufferedStates.InterpolatedStates.OrientationMatrix);

            Matrix.TransformNormal(ref localAttach, ref worldTransform, out worldAttachmentPoint);
            worldAttachmentPoint += wheel.vehicle.Body.BufferedStates.InterpolatedStates.Position;

            FPVector3 worldDirection;
            Matrix.Transform(ref wheel.suspension.localDirection, ref worldTransform, out worldDirection);

            FP length = wheel.suspension.currentLength;
            newPosition.x = worldAttachmentPoint.x + worldDirection.x * length;
            newPosition.y = worldAttachmentPoint.y + worldDirection.y * length;
            newPosition.z = worldAttachmentPoint.z + worldDirection.z * length;

            Matrix spinTransform;

            FPVector3 localSpinAxis;
            FPQuaternion.Transform(ref Toolbox.UpVector, ref localWheelOrientation, out localSpinAxis);
            Matrix.CreateFromAxisAngle(ref localSpinAxis, spinAngle, out spinTransform);


            Matrix localTurnTransform;
            Matrix.Multiply(ref localGraphicTransform, ref spinTransform, out localTurnTransform);
            Matrix.Multiply(ref localTurnTransform, ref steeringTransform, out localTurnTransform);
            //Matrix.Multiply(ref localTurnTransform, ref spinTransform, out localTurnTransform);
            Matrix.Multiply(ref localTurnTransform, ref worldTransform, out worldTransform);
            worldTransform.Translation += newPosition;
        }

        /// <summary>
        /// Finds a supporting entity, the contact location, and the contact normal.
        /// </summary>
        /// <param name="location">Contact point between the wheel and the support.</param>
        /// <param name="normal">Contact normal between the wheel and the support.</param>
        /// <param name="suspensionLength">Length of the suspension at the contact.</param>
        /// <param name="supportingCollidable">Collidable supporting the wheel, if any.</param>
        /// <param name="entity">Supporting object.</param>
        /// <param name="material">Material of the wheel.</param>
        /// <returns>Whether or not any support was found.</returns>
        protected internal override bool FindSupport(out FPVector3 location, out FPVector3 normal, out FP suspensionLength, out Collidable supportingCollidable, out Entity entity, out Material material)
        {
            suspensionLength = FP.MaxValue;
            location = Toolbox.NoVector;
            supportingCollidable = null;
            entity = null;
            normal = Toolbox.NoVector;
            material = null;

            Collidable testCollidable;
            FPRayHit rayHit;

            bool hit = false;

            FPQuaternion localSteeringTransform;
            FPQuaternion.CreateFromAxisAngle(ref wheel.suspension.localDirection, steeringAngle, out localSteeringTransform);
            var startingTransform = new RigidTransform
            {
                Position = wheel.suspension.worldAttachmentPoint,
                Orientation = FPQuaternion.Concatenate(FPQuaternion.Concatenate(LocalWheelOrientation, IncludeSteeringTransformInCast ? localSteeringTransform : FPQuaternion.Identity), wheel.vehicle.Body._orientation)
            };
            FPVector3 sweep;
            FPVector3.Multiply(ref wheel.suspension.worldDirection, wheel.suspension.restLength, out sweep);

            for (int i = 0; i < detector.CollisionInformation.pairs.Count; i++)
            {
                var pair = detector.CollisionInformation.pairs[i];
                testCollidable = (pair.BroadPhaseOverlap.entryA == detector.CollisionInformation ? pair.BroadPhaseOverlap.entryB : pair.BroadPhaseOverlap.entryA) as Collidable;
                if (testCollidable != null)
                {
                    if (CollisionRules.CollisionRuleCalculator(this, testCollidable) == CollisionRule.Normal &&
                        testCollidable.ConvexCast(shape, ref startingTransform, ref sweep, out rayHit) &&
                        rayHit.T * wheel.suspension.restLength < suspensionLength)
                    {
                        suspensionLength = rayHit.T * wheel.suspension.restLength;
                        EntityCollidable entityCollidable;
                        if ((entityCollidable = testCollidable as EntityCollidable) != null)
                        {
                            entity = entityCollidable.Entity;
                            material = entityCollidable.Entity.Material;
                        }
                        else
                        {
                            entity = null;
                            supportingCollidable = testCollidable;
                            var materialOwner = testCollidable as IMaterialOwner;
                            if (materialOwner != null)
                                material = materialOwner.Material;
                        }
                        location = rayHit.Location;
                        normal = rayHit.Normal;
                        hit = true;
                    }
                }
            }
            if (hit)
            {
                if (suspensionLength > F64.C0)
                {
                    FP dot;
                    FPVector3.Dot(ref normal, ref wheel.suspension.worldDirection, out dot);
                    if (dot > F64.C0)
                    {
                        //The cylinder cast produced a normal which is opposite of what we expect.
                        FPVector3.Negate(ref normal, out normal);
                    }
                    normal.Normalize();
                }
                else
                    FPVector3.Negate(ref wheel.suspension.worldDirection, out normal);
                return true;
            }
            return false;
        }


        /// <summary>
        /// Initializes the detector entity and any other necessary logic.
        /// </summary>
        protected internal override void Initialize()
        {
            //Setup the dimensions of the detector.
            var initialTransform = new RigidTransform { Orientation = LocalWheelOrientation };
            FPBoundingBox boundingBox;
            shape.GetBoundingBox(ref initialTransform, out boundingBox);
            var expansion = wheel.suspension.localDirection * wheel.suspension.restLength;
            if (expansion.x > F64.C0)
                boundingBox.Max.x += expansion.x;
            else if (expansion.x < F64.C0)
                boundingBox.Min.x += expansion.x;

            if (expansion.y > F64.C0)
                boundingBox.Max.y += expansion.y;
            else if (expansion.y < F64.C0)
                boundingBox.Min.y += expansion.y;

            if (expansion.z > F64.C0)
                boundingBox.Max.z += expansion.z;
            else if (expansion.z < F64.C0)
                boundingBox.Min.z += expansion.z;


            detector.Width = boundingBox.Max.x - boundingBox.Min.x;
            detector.Height = boundingBox.Max.y - boundingBox.Min.y;
            detector.Length = boundingBox.Max.z - boundingBox.Min.z;
        }

        /// <summary>
        /// Updates the position of the detector before each step.
        /// </summary>
        protected internal override void UpdateDetectorPosition()
        {
#if !WINDOWS
            FPVector3 newPosition = new FPVector3();
#else
            Vector3 newPosition;
#endif

            newPosition.x = wheel.suspension.worldAttachmentPoint.x + wheel.suspension.worldDirection.x * wheel.suspension.restLength * F64.C0p5;
            newPosition.y = wheel.suspension.worldAttachmentPoint.y + wheel.suspension.worldDirection.y * wheel.suspension.restLength * F64.C0p5;
            newPosition.z = wheel.suspension.worldAttachmentPoint.z + wheel.suspension.worldDirection.z * wheel.suspension.restLength * F64.C0p5;

            detector.position = newPosition;
            if (IncludeSteeringTransformInCast)
            {
                FPQuaternion localSteeringTransform;
                FPQuaternion.CreateFromAxisAngle(ref wheel.suspension.localDirection, steeringAngle, out localSteeringTransform);

                detector.orientation = FPQuaternion.Concatenate(localSteeringTransform, wheel.Vehicle.Body._orientation);
            }
            else
            {
                detector.orientation = wheel.Vehicle.Body._orientation;
            }
            FPVector3 linearVelocity;
            FPVector3.Subtract(ref newPosition, ref wheel.vehicle.Body._position, out linearVelocity);
            FPVector3.Cross(ref linearVelocity, ref wheel.vehicle.Body._angularVelocity, out linearVelocity);
            FPVector3.Add(ref linearVelocity, ref wheel.vehicle.Body._linearVelocity, out linearVelocity);
            detector.linearVelocity = linearVelocity;
            detector.angularVelocity = wheel.vehicle.Body._angularVelocity;
        }
    }
}