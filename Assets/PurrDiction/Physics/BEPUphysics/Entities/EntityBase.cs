using System;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.BroadPhaseSystems;
using BEPUphysics.BroadPhaseEntries.MobileCollidables;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.DeactivationManagement;
using BEPUphysics.EntityStateManagement;
using BEPUphysics.OtherSpaceStages;
using BEPUphysics.PositionUpdating;
using BEPUphysics.Settings;

using BEPUutilities;
using BEPUphysics.Materials;
using BEPUphysics.CollisionShapes;
using BEPUphysics.CollisionRuleManagement;
using MathChecker = BEPUutilities.MathChecker;
using FixMath.NET;

namespace BEPUphysics.Entities
{
    ///<summary>
    /// Superclass of movable rigid bodies.  Contains information for
    /// both dynamic and kinematic simulation.
    ///</summary>
    public class Entity :
        IBroadPhaseEntryOwner,
        IDeferredEventCreatorOwner,
        ISimulationIslandMemberOwner,
        ICCDPositionUpdateable,
        IForceUpdateable,
        ISpaceObject,
        IMaterialOwner,
        ICollisionRulesOwner,
        IEquatable<Entity>
    {
        internal FPVector3 _position;
        internal FPQuaternion _orientation = FPQuaternion.Identity;
        internal Matrix3x3 _orientationMatrix = Matrix3x3.Identity;
        internal FPVector3 _linearVelocity;
        internal FPVector3 _angularVelocity;
#if CONSERVE
        internal Vector3 angularMomentum;
#endif
        internal bool _isDynamic;



        ///<summary>
        /// Gets or sets the position of the Entity.  This Position acts
        /// as the center of mass for dynamic entities.
        ///</summary>
        public FPVector3 position
        {
            get
            {
                return _position;
            }
            set
            {
                _position = value;
                activityInformation.Activate();

                MathChecker.Validate(_position);
            }
        }
        ///<summary>
        /// Gets or sets the orientation quaternion of the entity.
        ///</summary>
        public FPQuaternion orientation
        {
            get
            {
                return _orientation;
            }
            set
            {
                FPQuaternion.Normalize(ref value, out _orientation);
                Matrix3x3.CreateFromQuaternion(ref _orientation, out _orientationMatrix);
                //Update inertia tensors for consistency.
                Matrix3x3 multiplied;
                Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensorInverse, out multiplied);
                Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensorInverse);
                Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensor, out multiplied);
                Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensor);
                activityInformation.Activate();

                MathChecker.Validate(_orientation);
            }
        }
        /// <summary>
        /// Gets or sets the orientation matrix of the entity.
        /// </summary>
        public Matrix3x3 orientationMatrix
        {
            get
            {
                return _orientationMatrix;
            }
            set
            {
                FPQuaternion.CreateFromRotationMatrix(ref value, out _orientation);
                orientation = _orientation; //normalizes and sets.
            }
        }
        ///<summary>
        /// Gets or sets the world transform of the entity.
        /// The upper left 3x3 part is the Orientation, and the translation is the Position.
        /// When setting this property, ensure that the rotation matrix component does not include
        /// any scaling or shearing.
        ///</summary>
        public Matrix localToWorld
        {
            get
            {
                Matrix worldTransform;
                Matrix3x3.ToMatrix4X4(ref _orientationMatrix, out worldTransform);
                worldTransform.Translation = _position;
                return worldTransform;
            }
            set
            {
                FPQuaternion.CreateFromRotationMatrix(ref value, out _orientation);
                orientation = _orientation; //normalizes and sets.
                _position = value.Translation;
                activityInformation.Activate();

                MathChecker.Validate(_position);
            }

        }
        /// <summary>
        /// Gets or sets the angular velocity of the entity.
        /// </summary>
        public FPVector3 angularVelocity
        {
            get
            {
                return _angularVelocity;
            }
            set
            {
                _angularVelocity = value;
                MathChecker.Validate(_angularVelocity);
#if CONSERVE
                Matrix3x3.Transform(ref value, ref inertiaTensor, out angularMomentum);
                MathChecker.Validate(angularMomentum);
#endif
                activityInformation.Activate();

            }
        }
        /// <summary>
        /// Gets or sets the angular momentum of the entity.
        /// </summary>
        public FPVector3 AngularMomentum
        {
            get
            {
#if CONSERVE
                return angularMomentum;
#else
                FPVector3 v;
                Matrix3x3.Transform(ref _angularVelocity, ref inertiaTensor, out v);
                return v;
#endif
            }
            set
            {
#if CONSERVE
                angularMomentum = value;
                MathChecker.Validate(angularMomentum);
#else
                Matrix3x3.Transform(ref value, ref inertiaTensorInverse, out _angularVelocity);
                activityInformation.Activate();
#endif
                MathChecker.Validate(_angularVelocity);
            }
        }
        /// <summary>
        /// Gets or sets the linear velocity of the entity.
        /// </summary>
        public FPVector3 linearVelocity
        {
            get
            {
                return _linearVelocity;
            }
            set
            {
                _linearVelocity = value;
                activityInformation.Activate();

                MathChecker.Validate(_linearVelocity);
            }
        }
        /// <summary>
        /// Gets or sets the linear momentum of the entity.
        /// </summary>
        public FPVector3 LinearMomentum
        {
            get
            {
                FPVector3 momentum;
                FPVector3.Multiply(ref _linearVelocity, mass, out momentum);
                return momentum;
            }
            set
            {
                FPVector3.Multiply(ref value, inverseMass, out _linearVelocity);
                activityInformation.Activate();

                MathChecker.Validate(_linearVelocity);
            }
        }
        /// <summary>
        /// Gets or sets the position, orientation, linear velocity, and angular velocity of the entity.
        /// </summary>
        public MotionState MotionState
        {
            get
            {
                MotionState toReturn;
                toReturn.Position = _position;
                toReturn.Orientation = _orientation;
                toReturn.LinearVelocity = _linearVelocity;
                toReturn.AngularVelocity = _angularVelocity;
                return toReturn;
            }
            set
            {
                position = value.Position;
                orientation = value.Orientation;
                linearVelocity = value.LinearVelocity;
                angularVelocity = value.AngularVelocity;
            }
        }

        /// <summary>
        /// Gets whether or not the entity is dynamic.
        /// Dynamic entities have finite mass and respond
        /// to collisions.  Kinematic (non-dynamic) entities
        /// have infinite mass and inertia and will plow through anything.
        /// </summary>
        public bool IsDynamic
        {
            get
            {
                return _isDynamic;
            }
        }



        bool hasPersonalGravity;
        private FPVector3 personalGravity;
        ///<summary>
        /// Gets or sets the entity's personal gravity. If null, the ForceUpdater's gravity is used instead. Defaults to null.
        ///</summary>
        public FPVector3? Gravity
        {
            get
            {
                if (hasPersonalGravity)
                {
                    return personalGravity;
                }
                return null;
            }
            set
            {
                if (value != null)
                {
                    if (!hasPersonalGravity || personalGravity != value.Value)
                    {
                        //Personal gravity either turned on or changed; wake up to handle it.
                        activityInformation.Activate();
                    }
                    hasPersonalGravity = true;
                    personalGravity = value.Value;
                }
                else
                {
                    if (hasPersonalGravity)
                    {
                        //Personal gravity turned off; wake up to handle it.
                        activityInformation.Activate();
                    }
                    hasPersonalGravity = false;
                    personalGravity = new FPVector3();
                }
            }
        }

        ///<summary>
        /// Gets the buffered states of the entity.  If the Space.BufferedStates manager is enabled,
        /// this property provides access to the buffered and interpolated states of the entity.
        /// Buffered states are the most recent completed update values, while interpolated states are the previous values blended
        /// with the current frame's values.  Interpolated states are helpful when updating the engine with internal time stepping,
        /// giving entity motion a smooth appearance even when updates aren't occurring consistently every frame.
        /// Both are buffered for asynchronous access.
        ///</summary>
        public EntityBufferedStates BufferedStates { get; private set; }

        internal Matrix3x3 inertiaTensorInverse;
        ///<summary>
        /// Gets the world space inertia tensor inverse of the entity.
        ///</summary>
        public Matrix3x3 InertiaTensorInverse
        {
            get
            {
                return inertiaTensorInverse;
            }
        }
        internal Matrix3x3 inertiaTensor;
        ///<summary>
        /// Gets the world space inertia tensor of the entity.
        ///</summary>
        public Matrix3x3 InertiaTensor
        {
            get { return inertiaTensor; }
        }

        internal Matrix3x3 localInertiaTensor;
        ///<summary>
        /// Gets or sets the local inertia tensor of the entity.
        ///</summary>
        public Matrix3x3 LocalInertiaTensor
        {
            get
            {
                return localInertiaTensor;
            }
            set
            {
                localInertiaTensor = value;
                Matrix3x3.AdaptiveInvert(ref localInertiaTensor, out localInertiaTensorInverse);
                Matrix3x3 multiplied;
                Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensorInverse, out multiplied);
                Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensorInverse);
                Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensor, out multiplied);
                Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensor);

                localInertiaTensor.Validate();
                localInertiaTensorInverse.Validate();
            }
        }
        internal Matrix3x3 localInertiaTensorInverse;
        /// <summary>
        /// Gets or sets the local inertia tensor inverse of the entity.
        /// </summary>
        public Matrix3x3 LocalInertiaTensorInverse
        {
            get
            {
                return localInertiaTensorInverse;
            }
            set
            {
                localInertiaTensorInverse = value;
                Matrix3x3.AdaptiveInvert(ref localInertiaTensorInverse, out localInertiaTensor);
                //Update the world space versions.
                Matrix3x3 multiplied;
                Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensorInverse, out multiplied);
                Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensorInverse);
                Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensor, out multiplied);
                Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensor);

                localInertiaTensor.Validate();
                localInertiaTensorInverse.Validate();
            }
        }

        internal FP mass;
        ///<summary>
        /// Gets or sets the mass of the entity.  Setting this to an invalid value, such as a non-positive number, NaN, or infinity, makes the entity kinematic.
        /// Setting it to a valid positive number will also scale the inertia tensor if it was already dynamic, or force the calculation of a new inertia tensor
        /// if it was previously kinematic.
        ///</summary>
        public FP Mass
        {
            get
            {
                return mass;
            }
            set
            {
				// Removed IsNan, Infinity check
                // if (value <= 0 || F64.IsNaN(value) || Fix64.IsInfinity(value))
				if (value <= F64.C0)
					BecomeKinematic();
                else
                {
                    if (_isDynamic)
                    {
                        //If it's already dynamic, then we don't need to recompute the inertia tensor.
                        //Instead, scale the one we have already.
                        Matrix3x3 newInertia;
                        Matrix3x3.Multiply(ref localInertiaTensor, value * inverseMass, out newInertia);
                        BecomeDynamic(value, newInertia);
                    }
                    else
                    {
                        BecomeDynamic(value);
                    }
                }
            }
        }

        internal FP inverseMass;
        /// <summary>
        /// Gets or sets the inverse mass of the entity.
        /// </summary>
        public FP InverseMass
        {
            get
            {
                return inverseMass;
            }
            set
            {
                if (value > F64.C0)
                    Mass = F64.C1 / value;
                else
                    Mass = F64.C0;
            }
        }



        ///<summary>
        /// Fires when the entity's position and orientation is updated.
        ///</summary>
        public event Action<Entity> PositionUpdated;



        protected EntityCollidable collisionInformation;
        ///<summary>
        /// Gets the collidable used by the entity.
        ///</summary>
        public EntityCollidable CollisionInformation
        {
            get { return collisionInformation; }
            protected set
            {
                if (collisionInformation != null)
                    collisionInformation.Shape.ShapeChanged -= shapeChangedDelegate;
                collisionInformation = value;
                if (collisionInformation != null)
                    collisionInformation.Shape.ShapeChanged += shapeChangedDelegate;
                //Entity constructors do their own initialization when the collision information changes.
                //Might be able to condense it up here, but don't really need it right now.
                //ShapeChangedHandler(collisionInformation.shape);
            }
        }

        //protected internal object locker = new object();
        /////<summary>
        ///// Gets the synchronization object used by systems that need
        ///// exclusive access to the entity's properties.
        /////</summary>
        //public object Locker
        //{
        //    get
        //    {
        //        return locker;
        //    }
        //}

        protected internal SpinLock locker = new SpinLock();
        ///<summary>
        /// Gets the synchronization object used by systems that need
        /// exclusive access to the entity's properties.
        ///</summary>
        public SpinLock Locker
        {
            get
            {
                return locker;
            }
        }

        internal Material material;
        //NOT thread safe due to material change pair update.
        ///<summary>
        /// Gets or sets the material used by the entity.
        ///</summary>
        public Material Material
        {
            get
            {
                return material;
            }
            set
            {
                if (material != null)
                    material.MaterialChanged -= materialChangedDelegate;
                material = value;
                if (material != null)
                    material.MaterialChanged += materialChangedDelegate;
                OnMaterialChanged(material);
            }
        }

        Action<Material> materialChangedDelegate;
        void OnMaterialChanged(Material newMaterial)
        {
            for (int i = 0; i < collisionInformation.pairs.Count; i++)
            {
                collisionInformation.pairs[i].UpdateMaterialProperties();
            }
        }


        ///<summary>
        /// Gets all the EntitySolverUpdateables associated with this entity.
        ///</summary>
        public EntitySolverUpdateableCollection SolverUpdateables
        {
            get
            {
                return new EntitySolverUpdateableCollection(activityInformation.connections);
            }
        }

        ///<summary>
        /// Gets the two-entity constraints associated with this entity (a subset of the solver updateables).
        ///</summary>
        public EntityConstraintCollection Constraints
        {
            get
            {
                return new EntityConstraintCollection(activityInformation.connections);
            }
        }

        #region Construction

        protected Entity()
        {
            InitializeId();

            BufferedStates = new EntityBufferedStates(this);

            material = new Material();
            materialChangedDelegate = OnMaterialChanged;
            material.MaterialChanged += materialChangedDelegate;

            shapeChangedDelegate = OnShapeChanged;

            activityInformation = new SimulationIslandMember(this);


        }

        ///<summary>
        /// Constructs a new kinematic entity.
        ///</summary>
        ///<param name="collisionInformation">Collidable to use with the entity.</param>
        public Entity(EntityCollidable collisionInformation)
            : this()
        {
            Initialize(collisionInformation);
        }

        ///<summary>
        /// Constructs a new entity.
        ///</summary>
        ///<param name="collisionInformation">Collidable to use with the entity.</param>
        ///<param name="mass">Mass of the entity. If positive, the entity will be dynamic. Otherwise, it will be kinematic.</param>
        public Entity(EntityCollidable collisionInformation, FP mass)
            : this()
        {
            Initialize(collisionInformation, mass);
        }

        ///<summary>
        /// Constructs a new entity.
        ///</summary>
        ///<param name="collisionInformation">Collidable to use with the entity.</param>
        ///<param name="mass">Mass of the entity. If positive, the entity will be dynamic. Otherwise, it will be kinematic.</param>
        /// <param name="inertiaTensor">Inertia tensor of the entity. Only used for a dynamic entity.</param>
        public Entity(EntityCollidable collisionInformation, FP mass, Matrix3x3 inertiaTensor)
            : this()
        {
            Initialize(collisionInformation, mass, inertiaTensor);
        }

        ///<summary>
        /// Constructs a new kinematic entity.
        ///</summary>
        ///<param name="shape">Shape to use with the entity.</param>
        public Entity(EntityShape shape)
            : this()
        {
            Initialize(shape.GetCollidableInstance());
        }

        ///<summary>
        /// Constructs a new entity.
        ///</summary>
        ///<param name="shape">Shape to use with the entity.</param>
        ///<param name="mass">Mass of the entity. If positive, the entity will be dynamic. Otherwise, it will be kinematic.</param>
        public Entity(EntityShape shape, FP mass)
            : this()
        {
            Initialize(shape.GetCollidableInstance(), mass);
        }

        ///<summary>
        /// Constructs a new entity.
        ///</summary>
        ///<param name="shape">Shape to use with the entity.</param>
        ///<param name="mass">Mass of the entity. If positive, the entity will be dynamic. Otherwise, it will be kinematic.</param>
        /// <param name="inertiaTensor">Inertia tensor of the entity. Only used for a dynamic entity.</param>
        public Entity(EntityShape shape, FP mass, Matrix3x3 inertiaTensor)
            : this()
        {
            Initialize(shape.GetCollidableInstance(), mass, inertiaTensor);
        }





        //These initialize methods make it easier to construct some Entity prefab types.
        protected internal void Initialize(EntityCollidable collisionInformation)
        {
            CollisionInformation = collisionInformation;
            BecomeKinematic();
            collisionInformation.Entity = this;
        }

        protected internal void Initialize(EntityCollidable collisionInformation, FP mass)
        {
            CollisionInformation = collisionInformation;

            if (mass > F64.C0)
            {
                BecomeDynamic(mass, collisionInformation.Shape.VolumeDistribution * (mass * InertiaHelper.InertiaTensorScale));
            }
            else
            {
                BecomeKinematic();
            }

            collisionInformation.Entity = this;
        }

        protected internal void Initialize(EntityCollidable collisionInformation, FP mass, Matrix3x3 inertiaTensor)
        {
            CollisionInformation = collisionInformation;

            if (mass > F64.C0)
                BecomeDynamic(mass, inertiaTensor);
            else
                BecomeKinematic();

            collisionInformation.Entity = this;
        }


        #endregion

        #region IDeferredEventCreatorOwner Members

        IDeferredEventCreator IDeferredEventCreatorOwner.EventCreator
        {
            get { return CollisionInformation.Events; }
        }

        #endregion

        internal SimulationIslandMember activityInformation;
        public SimulationIslandMember ActivityInformation
        {
            get
            {
                return activityInformation;
            }
        }

        bool IForceUpdateable.IsActive
        {
            get
            {
                return activityInformation.IsActive;
            }
        }
        bool IPositionUpdateable.IsActive
        {
            get
            {
                return activityInformation.IsActive;
            }
        }



        ///<summary>
        /// Applies an impulse to the entity.
        ///</summary>
        ///<param name="location">Location to apply the impulse.</param>
        ///<param name="impulse">Impulse to apply.</param>
        public void ApplyImpulse(FPVector3 location, FPVector3 impulse)
        {
            ApplyImpulse(ref location, ref impulse);
        }

        ///<summary>
        /// Applies an impulse to the entity.
        ///</summary>
        ///<param name="location">Location to apply the impulse.</param>
        ///<param name="impulse">Impulse to apply.</param>
        public void ApplyImpulse(ref FPVector3 location, ref FPVector3 impulse)
        {
            if (_isDynamic)
            {
                ApplyImpulseWithoutActivating(ref location, ref impulse);
                activityInformation.Activate();
            }
        }

        ///<summary>
        /// Applies an impulse to the entity without activating the entity.
        ///</summary>
        ///<param name="location">Location to apply the impulse.</param>
        ///<param name="impulse">Impulse to apply.</param>
        public void ApplyImpulseWithoutActivating(ref FPVector3 location, ref FPVector3 impulse)
        {
            ApplyLinearImpulse(ref impulse);
#if WINDOWS
            Vector3 positionDifference;
#else
            FPVector3 positionDifference = new FPVector3();
#endif
            positionDifference.x = location.x - _position.x;
            positionDifference.y = location.y - _position.y;
            positionDifference.z = location.z - _position.z;

            FPVector3 cross;
            FPVector3.Cross(ref positionDifference, ref impulse, out cross);
            ApplyAngularImpulse(ref cross);

        }


        //These methods are very direct and quick.  They don't activate the object or anything.
        /// <summary>
        /// Applies a linear velocity change to the entity using the given impulse.
        /// This method does not wake up the object or perform any other nonessential operation;
        /// it is meant to be used for performance-sensitive constraint solving.
        /// Consider equivalently adding to the LinearMomentum property for convenience instead.
        /// </summary>
        /// <param name="impulse">Impulse to apply.</param>
        public void ApplyLinearImpulse(ref FPVector3 impulse)
        {
            _linearVelocity.x += impulse.x * inverseMass;
            _linearVelocity.y += impulse.y * inverseMass;
            _linearVelocity.z += impulse.z * inverseMass;
            MathChecker.Validate(_linearVelocity);

        }
        public void ApplyLinearImpulse(FPVector3 impulse)
        {
            _linearVelocity.x += impulse.x * inverseMass;
            _linearVelocity.y += impulse.y * inverseMass;
            _linearVelocity.z += impulse.z * inverseMass;
            MathChecker.Validate(_linearVelocity);

        }
        /// <summary>
        /// Applies an angular velocity change to the entity using the given impulse.
        /// This method does not wake up the object or perform any other nonessential operation;
        /// it is meant to be used for performance-sensitive constraint solving.
        /// Consider equivalently adding to the AngularMomentum property for convenience instead.
        /// </summary>
        /// <param name="impulse">Impulse to apply.</param>
        public void ApplyAngularImpulse(ref FPVector3 impulse)
        {
            //There's some room here for SIMD-friendliness.  However, since the phone doesn't accelerate non-XNA types, the matrix3x3 operations don't gain much.
#if CONSERVE
            angularMomentum.X += impulse.X;
            angularMomentum.Y += impulse.Y;
            angularMomentum.Z += impulse.Z;
            angularVelocity.X = angularMomentum.X * inertiaTensorInverse.M11 + angularMomentum.Y * inertiaTensorInverse.M21 + angularMomentum.Z * inertiaTensorInverse.M31;
            angularVelocity.Y = angularMomentum.X * inertiaTensorInverse.M12 + angularMomentum.Y * inertiaTensorInverse.M22 + angularMomentum.Z * inertiaTensorInverse.M32;
            angularVelocity.Z = angularMomentum.X * inertiaTensorInverse.M13 + angularMomentum.Y * inertiaTensorInverse.M23 + angularMomentum.Z * inertiaTensorInverse.M33;

            MathChecker.Validate(angularMomentum);
#else
            _angularVelocity.x += impulse.x * inertiaTensorInverse.M11 + impulse.y * inertiaTensorInverse.M21 + impulse.z * inertiaTensorInverse.M31;
            _angularVelocity.y += impulse.x * inertiaTensorInverse.M12 + impulse.y * inertiaTensorInverse.M22 + impulse.z * inertiaTensorInverse.M32;
            _angularVelocity.z += impulse.x * inertiaTensorInverse.M13 + impulse.y * inertiaTensorInverse.M23 + impulse.z * inertiaTensorInverse.M33;
#endif

            MathChecker.Validate(_angularVelocity);
        }

        /// <summary>
        /// Gets or sets whether or not to ignore shape changes.  When true, changing the entity's collision shape will not update the volume, density, or inertia tensor.
        /// </summary>
        public bool IgnoreShapeChanges { get; set; }

        Action<CollisionShape> shapeChangedDelegate;
        protected void OnShapeChanged(CollisionShape shape)
        {
            if (!IgnoreShapeChanges)
            {
                //When the shape changes, force the entity awake so that it performs any necessary updates.
                activityInformation.Activate();
                if (_isDynamic)
                {
                    LocalInertiaTensor = collisionInformation.Shape.VolumeDistribution * (InertiaHelper.InertiaTensorScale * mass);
                }
                else
                {
                    LocalInertiaTensorInverse = new Matrix3x3();
                }
            }
        }


        //TODO: Include warnings about multithreading.  These modify things outside of the entity and use single-thread-only helpers.
        ///<summary>
        /// Forces the entity to become kinematic.  Kinematic entities have infinite mass and inertia.
        ///</summary>
        public void BecomeKinematic()
        {
            bool previousState = _isDynamic;
            _isDynamic = false;
            LocalInertiaTensorInverse = new Matrix3x3();
            mass = F64.C0;
            inverseMass = F64.C0;

            //Notify simulation island of the change.
            if (previousState)
            {
                if (activityInformation.DeactivationManager != null)
                    activityInformation.DeactivationManager.RemoveSimulationIslandFromMember(activityInformation);

                if (((IForceUpdateable)this).ForceUpdater != null)
                    ((IForceUpdateable)this).ForceUpdater.ForceUpdateableBecomingKinematic(this);
            }
            //Change the collision group if it was using the default.
            if (collisionInformation.CollisionRules.Group == CollisionRules.DefaultDynamicCollisionGroup ||
                collisionInformation.CollisionRules.Group == null)
                collisionInformation.CollisionRules.Group = CollisionRules.DefaultKinematicCollisionGroup;

            activityInformation.Activate();

            //Preserve velocity and reinitialize momentum for new state.
            linearVelocity = _linearVelocity;
            angularVelocity = _angularVelocity;
        }


        ///<summary>
        /// Forces the entity to become dynamic.  Dynamic entities respond to collisions and have finite mass and inertia.
        ///</summary>
        ///<param name="mass">Mass to use for the entity.</param>
        public void BecomeDynamic(FP mass)
        {
            BecomeDynamic(mass, collisionInformation.Shape.VolumeDistribution * (mass * InertiaHelper.InertiaTensorScale));
        }

        ///<summary>
        /// Forces the entity to become dynamic.  Dynamic entities respond to collisions and have finite mass and inertia.
        ///</summary>
        ///<param name="mass">Mass to use for the entity.</param>
        /// <param name="localInertiaTensor">Inertia tensor to use for the entity.</param>
        public void BecomeDynamic(FP mass, Matrix3x3 localInertiaTensor)
        {
			// if (mass <= 0) || Fix64.IsInfinity(mass) || Fix64.IsNaN(mass))
			if (mass <= F64.C0)
                throw new InvalidOperationException("Cannot use a mass of " + mass + " for a dynamic entity.  Consider using a kinematic entity instead.");
            bool previousState = _isDynamic;
            _isDynamic = true;
            LocalInertiaTensor = localInertiaTensor;
            this.mass = mass;
            this.inverseMass = F64.C1 / mass;

            //Notify simulation island system of the change.
            if (!previousState)
            {
                if (activityInformation.DeactivationManager != null)
                    activityInformation.DeactivationManager.AddSimulationIslandToMember(activityInformation);

                if (((IForceUpdateable)this).ForceUpdater != null)
                    ((IForceUpdateable)this).ForceUpdater.ForceUpdateableBecomingDynamic(this);
            }
            //Change the group if it was using the defaults.
            if (collisionInformation.CollisionRules.Group == CollisionRules.DefaultKinematicCollisionGroup ||
                collisionInformation.CollisionRules.Group == null)
                collisionInformation.CollisionRules.Group = CollisionRules.DefaultDynamicCollisionGroup;

            activityInformation.Activate();


            //Preserve velocity and reinitialize momentum for new state.
            linearVelocity = _linearVelocity;
            angularVelocity = _angularVelocity;

        }


        void IForceUpdateable.UpdateForForces(FP dt)
        {

            //Apply gravity.
            if (hasPersonalGravity)
            {
                FPVector3 gravityDt;
                FPVector3.Multiply(ref personalGravity, dt, out gravityDt);
                FPVector3.Add(ref gravityDt, ref _linearVelocity, out _linearVelocity);
            }
            else
            {
                FPVector3.Add(ref forceUpdater.gravityDt, ref _linearVelocity, out _linearVelocity);
            }

            //Boost damping at very low velocities.  This is a strong stabilizer; removes a ton of energy from the system.
            if (activityInformation.DeactivationManager.useStabilization && activityInformation.allowStabilization &&
                (activityInformation.isSlowing || activityInformation.velocityTimeBelowLimit > activityInformation.DeactivationManager.lowVelocityTimeMinimum))
            {
                FP energy = _linearVelocity.LengthSquared() + _angularVelocity.LengthSquared();
                if (energy < activityInformation.DeactivationManager.velocityLowerLimitSquared)
                {
                    FP boost = F64.C1 - FP.Sqrt(energy) / (F64.C2 * activityInformation.DeactivationManager.velocityLowerLimit);
                    ModifyAngularDamping(boost);
                    ModifyLinearDamping(boost);
                }
            }

            //Damping
            FP linear = LinearDamping + linearDampingBoost;
            if (linear > F64.C0)
            {
                FPVector3.Multiply(ref _linearVelocity, FP.Pow(MathHelper.Clamp(F64.C1 - linear, F64.C0, F64.C1), dt), out _linearVelocity);
            }
            //When applying angular damping, the momentum or velocity is damped depending on the conservation setting.
            FP angular = AngularDamping + angularDampingBoost;
            if (angular > F64.C0)
            {
#if CONSERVE
                Vector3.Multiply(ref angularMomentum, Fix64.Pow(MathHelper.Clamp(1 - angular, 0, 1), dt), out angularMomentum);
#else
				FPVector3.Multiply(ref _angularVelocity, FP.Pow(MathHelper.Clamp(F64.C1 - angular, F64.C0, F64.C1), dt), out _angularVelocity);
#endif
            }

            linearDampingBoost = F64.C0;
            angularDampingBoost = F64.C0;

            //Update world inertia tensors.
            Matrix3x3 multiplied;
            Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensorInverse, out multiplied);
            Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensorInverse);
            Matrix3x3.MultiplyTransposed(ref _orientationMatrix, ref localInertiaTensor, out multiplied);
            Matrix3x3.Multiply(ref multiplied, ref _orientationMatrix, out inertiaTensor);

#if CONSERVE
            //Update angular velocity.
            //Note that this doesn't play nice with singular inertia tensors.
            //Locked tensors result in zero angular velocity.
            Matrix3x3.Transform(ref angularMomentum, ref inertiaTensorInverse, out angularVelocity);
            MathChecker.Validate(angularMomentum);
#endif
            MathChecker.Validate(_linearVelocity);
            MathChecker.Validate(_angularVelocity);


        }

        private ForceUpdater forceUpdater;
        ForceUpdater IForceUpdateable.ForceUpdater
        {
            get
            {
                return forceUpdater;
            }
            set
            {
                forceUpdater = value;
            }
        }

        #region ISpaceObject

        Space space;
        Space ISpaceObject.Space
        {
            get
            {
                return space;
            }
            set
            {
                space = value;
            }
        }
        ///<summary>
        /// Gets the space that owns the entity.
        ///</summary>
        public Space Space
        {
            get
            {
                return space;
            }
        }


        void ISpaceObject.OnAdditionToSpace(Space newSpace)
        {
            OnAdditionToSpace(newSpace);
        }

        protected virtual void OnAdditionToSpace(Space newSpace)
        {
        }

        void ISpaceObject.OnRemovalFromSpace(Space oldSpace)
        {
            OnRemovalFromSpace(oldSpace);
        }

        protected virtual void OnRemovalFromSpace(Space oldSpace)
        {
        }
        #endregion


        #region ICCDPositionUpdateable

        PositionUpdater IPositionUpdateable.PositionUpdater
        {
            get;
            set;
        }

        PositionUpdateMode positionUpdateMode = MotionSettings.DefaultPositionUpdateMode;
        ///<summary>
        /// Gets the position update mode of the entity.
        ///</summary>
        public PositionUpdateMode PositionUpdateMode
        {
            get
            {
                return positionUpdateMode;
            }
            set
            {
                var previous = positionUpdateMode;
                positionUpdateMode = value;
                //Notify our owner of the change, if needed.
                if (positionUpdateMode != previous &&
                    ((IPositionUpdateable)this).PositionUpdater != null &&
                    (((IPositionUpdateable)this).PositionUpdater as ContinuousPositionUpdater) != null)
                {
                    (((IPositionUpdateable)this).PositionUpdater as ContinuousPositionUpdater).UpdateableModeChanged(this, previous);
                }

            }
        }

        void ICCDPositionUpdateable.UpdateTimesOfImpact(FP dt)
        {
            //I am a continuous object.  If I am in a pair with another object, even if I am inactive,
            //I must order the pairs to compute a time of impact.

            //The pair method works in such a way that, when this method is run asynchronously, there will be no race conditions.
            for (int i = 0; i < collisionInformation.pairs.Count; i++)
            {
                //Only perform CCD if we're either supposed to test against no solver pairs or if this isn't a no solver pair.
                if (MotionSettings.CCDFilter(collisionInformation.pairs.Elements[i]))
                    collisionInformation.pairs.Elements[i].UpdateTimeOfImpact(collisionInformation, dt);
            }
        }
        void ICCDPositionUpdateable.ResetTimesOfImpact()
        {
            //Reset all of the times of impact to 1, allowing the entity to move all the way through its velocity-defined motion.
            for (int i = 0; i < collisionInformation.pairs.Count; i++)
            {
                collisionInformation.pairs.Elements[i].timeOfImpact = F64.C1;
            }
        }

        void ICCDPositionUpdateable.UpdatePositionContinuously(FP dt)
        {
            FP minimumToi = F64.C1;
            for (int i = 0; i < collisionInformation.pairs.Count; i++)
            {
                if (collisionInformation.pairs.Elements[i].timeOfImpact < minimumToi)
                    minimumToi = collisionInformation.pairs.Elements[i].timeOfImpact;
            }

            //The orientation was already updated by the PreUpdatePosition.
            //However, to be here, this object is not a discretely updated object.
            //That means we still need to update the linear motion.

            FPVector3 increment;
            FPVector3.Multiply(ref _linearVelocity, dt * minimumToi, out increment);
            FPVector3.Add(ref _position, ref increment, out _position);

            collisionInformation.UpdateWorldTransform(ref _position, ref _orientation);

            if (PositionUpdated != null)
                PositionUpdated(this);

            MathChecker.Validate(_linearVelocity);
            MathChecker.Validate(_angularVelocity);
            MathChecker.Validate(_position);
            MathChecker.Validate(_orientation);
#if CONSERVE
            MathChecker.Validate(angularMomentum);
#endif
        }

        void IPositionUpdateable.PreUpdatePosition(FP dt)
        {
            FPVector3 increment;

            FPVector3.Multiply(ref _angularVelocity, dt * F64.C0p5, out increment);
            var multiplier = new FPQuaternion(increment.x, increment.y, increment.z, F64.C0);
            FPQuaternion.Multiply(ref multiplier, ref _orientation, out multiplier);
            FPQuaternion.Add(ref _orientation, ref multiplier, out _orientation);
            _orientation.Normalize();

            Matrix3x3.CreateFromQuaternion(ref _orientation, out _orientationMatrix);

            //Only do the linear motion if this object doesn't obey CCD.
            if (PositionUpdateMode == PositionUpdateMode.Discrete)
            {
                FPVector3.Multiply(ref _linearVelocity, dt, out increment);
                FPVector3.Add(ref _position, ref increment, out _position);

                collisionInformation.UpdateWorldTransform(ref _position, ref _orientation);
                //The position update is complete if this is a discretely updated object.
                if (PositionUpdated != null)
                    PositionUpdated(this);
            }

            MathChecker.Validate(_linearVelocity);
            MathChecker.Validate(_angularVelocity);
            MathChecker.Validate(_position);
            MathChecker.Validate(_orientation);
#if CONSERVE
            MathChecker.Validate(angularMomentum);
#endif
        }



        #endregion



        FP linearDampingBoost, angularDampingBoost;
        FP angularDamping = (FP).15m;
        FP linearDamping = (FP).03m;
        ///<summary>
        /// Gets or sets the angular damping of the entity.
        /// Values range from 0 to 1, corresponding to a fraction of angular momentum removed
        /// from the entity over a unit of time.
        ///</summary>
        public FP AngularDamping
        {
            get
            {
                return angularDamping;
            }
            set
            {
                angularDamping = MathHelper.Clamp(value, F64.C0, F64.C1);
            }
        }
        ///<summary>
        /// Gets or sets the linear damping of the entity.
        /// Values range from 0 to 1, corresponding to a fraction of linear momentum removed
        /// from the entity over a unit of time.
        ///</summary>
        public FP LinearDamping
        {
            get
            {
                return linearDamping;
            }

            set
            {
                linearDamping = MathHelper.Clamp(value, F64.C0, F64.C1);
            }
        }

        /// <summary>
        /// Temporarily adjusts the linear damping by an amount.  After the value is used, the
        /// damping returns to the base value.
        /// </summary>
        /// <param name="damping">Damping to add.</param>
        public void ModifyLinearDamping(FP damping)
        {
            FP totalDamping = LinearDamping + linearDampingBoost;
            FP remainder = F64.C1 - totalDamping;
            linearDampingBoost += damping * remainder;
        }
        /// <summary>
        /// Temporarily adjusts the angular damping by an amount.  After the value is used, the
        /// damping returns to the base value.
        /// </summary>
        /// <param name="damping">Damping to add.</param>
        public void ModifyAngularDamping(FP damping)
        {
            FP totalDamping = AngularDamping + angularDampingBoost;
            FP remainder = F64.C1 - totalDamping;
            angularDampingBoost += damping * remainder;
        }

        /// <summary>
        /// Gets or sets the user data associated with the entity.
        /// This is separate from the entity's collidable's tag.
        /// If a tag needs to be accessed from within the collision
        /// detection pipeline, consider using the entity.CollisionInformation.Tag.
        /// </summary>
        public object Tag { get; set; }



        CollisionRules ICollisionRulesOwner.CollisionRules
        {
            get
            {
                return collisionInformation.collisionRules;
            }
            set
            {
                collisionInformation.CollisionRules = value;
            }
        }

        BroadPhaseEntry IBroadPhaseEntryOwner.Entry
        {
            get { return collisionInformation; }
        }

        public override string ToString()
        {
            if (Tag == null)
                return base.ToString();
            else
                return base.ToString() + ", " + Tag;
        }


#if WINDOWS_PHONE
        static int idCounter;
        /// <summary>
        /// Gets the entity's unique instance id.
        /// </summary>
        public int InstanceId { get; private set; }
#else
        static long idCounter;
        /// <summary>
        /// Gets the entity's unique instance id.
        /// </summary>
        public long InstanceId { get; private set; }
#endif
        void InitializeId()
        {
            InstanceId = System.Threading.Interlocked.Increment(ref idCounter);

            hashCode = (int)((((ulong)InstanceId) * 4294967311UL) % 4294967296UL);
        }


        int hashCode;
        public override int GetHashCode()
        {
            return hashCode;
        }


        public bool Equals(Entity other)
        {
            return other == this;
        }

        public override bool Equals(object obj)
        {
            return Equals((Entity)obj);
        }
    }
}
