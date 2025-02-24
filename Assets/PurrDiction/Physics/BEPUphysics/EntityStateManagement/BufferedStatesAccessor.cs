 
using BEPUutilities;

namespace BEPUphysics.EntityStateManagement
{
    ///<summary>
    /// Acts as an entity's view into the buffered states system.
    /// Buffered states are updated each frame and contain the latest known states.
    /// These states can also be written to.  Writes will not be immediately visible;
    /// the next frame's write buffer flush will write the changes to the entities.
    ///</summary>
    public class BufferedStatesAccessor
    {
        internal EntityBufferedStates bufferedStates;
        ///<summary>
        /// Gets and sets the states write buffer used when buffered properties are written.
        ///</summary>
        public EntityStateWriteBuffer WriteBuffer { get; set; }
        ///<summary>
        /// Constructs a new accessor.
        ///</summary>
        ///<param name="bufferedStates">The owning states system.</param>
        public BufferedStatesAccessor(EntityBufferedStates bufferedStates)
        {
            this.bufferedStates = bufferedStates;
        }

        bool IsReadBufferAccessible()
        {
            return bufferedStates.BufferedStatesManager != null && bufferedStates.BufferedStatesManager.Enabled && bufferedStates.BufferedStatesManager.ReadBuffers.Enabled;
        }

        bool IsWriteBufferAccessible()
        {
            return WriteBuffer != null && WriteBuffer.Enabled;
        }


        ///<summary>
        /// Gets or sets the buffered position of the entity.
        ///</summary>
        public FPVector3 Position
        {
            get
            {
                if (IsReadBufferAccessible())
                    return bufferedStates.BufferedStatesManager.ReadBuffers.GetState(bufferedStates.motionStateIndex).Position;
                return bufferedStates.Entity.position;
            }
            set
            {
                if (IsWriteBufferAccessible())
                    WriteBuffer.EnqueuePosition(bufferedStates.Entity, ref value);
                else
                    bufferedStates.Entity.position = value;
            }
        }

        ///<summary>
        /// Gets or sets the buffered orientation quaternion of the entity.
        ///</summary>
        public FPQuaternion Orientation
        {
            get
            {
                if (IsReadBufferAccessible())
                    return bufferedStates.BufferedStatesManager.ReadBuffers.GetState(bufferedStates.motionStateIndex).Orientation;
                return bufferedStates.Entity.orientation;
            }
            set
            {
                if (IsWriteBufferAccessible())
                    WriteBuffer.EnqueueOrientation(bufferedStates.Entity, ref value);
                else
                    bufferedStates.Entity.orientation = value;
            }
        }

        ///<summary>
        /// Gets or sets the buffered orientation matrix of the entity.
        ///</summary>
        public Matrix3x3 OrientationMatrix
        {
            get
            {
                Matrix3x3 toReturn;
                if (IsReadBufferAccessible())
                {
                    FPQuaternion o = bufferedStates.BufferedStatesManager.ReadBuffers.GetState(bufferedStates.motionStateIndex).Orientation;
                    Matrix3x3.CreateFromQuaternion(ref o, out toReturn);
                }
                else
                    Matrix3x3.CreateFromQuaternion(ref bufferedStates.Entity._orientation, out toReturn);
                return toReturn;
            }
            set
            {
                if (IsWriteBufferAccessible())
                {
                    FPQuaternion toSet = FPQuaternion.Normalize(FPQuaternion.CreateFromRotationMatrix(value));
                    WriteBuffer.EnqueueOrientation(bufferedStates.Entity, ref toSet);
                }
                else
                {
                    bufferedStates.Entity.orientationMatrix = value;
                }
            }
        }

        ///<summary>
        /// Gets or sets the buffered linear velocity of the entity.
        ///</summary>
        public FPVector3 LinearVelocity
        {
            get
            {
                if (IsReadBufferAccessible())
                    return bufferedStates.BufferedStatesManager.ReadBuffers.GetState(bufferedStates.motionStateIndex).LinearVelocity;
                return bufferedStates.Entity.linearVelocity;
            }
            set
            {
                if (IsWriteBufferAccessible())
                    WriteBuffer.EnqueueLinearVelocity(bufferedStates.Entity, ref value);
                else
                    bufferedStates.Entity.linearVelocity = value;
            }
        }


        ///<summary>
        /// Gets or sets the buffered angular velocity of the entity.
        ///</summary>
        public FPVector3 AngularVelocity
        {
            get
            {
                if (IsReadBufferAccessible())
                    return bufferedStates.BufferedStatesManager.ReadBuffers.GetState(bufferedStates.motionStateIndex).AngularVelocity;
                return bufferedStates.Entity.angularVelocity;
            }
            set
            {
                if (IsWriteBufferAccessible())
                    WriteBuffer.EnqueueAngularVelocity(bufferedStates.Entity, ref value);
                else
                    bufferedStates.Entity.angularVelocity = value;
            }
        }

        ///<summary>
        /// Gets or sets the buffered world transform of the entity.
        ///</summary>
        public Matrix WorldTransform
        {
            get
            {
                if (IsReadBufferAccessible())
                    return bufferedStates.BufferedStatesManager.ReadBuffers.GetState(bufferedStates.motionStateIndex).WorldTransform;
                return bufferedStates.Entity.localToWorld;
            }
            set
            {
                if (IsWriteBufferAccessible())
                {
                    FPVector3 translation = value.Translation;
                    FPQuaternion orientation;
                    FPQuaternion.CreateFromRotationMatrix(ref value, out orientation);
                    orientation.Normalize();
                    WriteBuffer.EnqueueOrientation(bufferedStates.Entity, ref orientation);
                    WriteBuffer.EnqueuePosition(bufferedStates.Entity, ref translation);
                }
                else
                {
                    bufferedStates.Entity.localToWorld = value;
                }
            }
        }

        ///<summary>
        /// Gets or sets the buffered motion state of the entity.
        ///</summary>
        public MotionState MotionState
        {
            get
            {
                if (IsReadBufferAccessible())
                    return bufferedStates.BufferedStatesManager.ReadBuffers.GetState(bufferedStates.motionStateIndex);
                return bufferedStates.Entity.MotionState;
            }
            set
            {
                if (IsWriteBufferAccessible())
                {
                    WriteBuffer.EnqueueLinearVelocity(bufferedStates.Entity, ref value.LinearVelocity);
                    WriteBuffer.EnqueueAngularVelocity(bufferedStates.Entity, ref value.AngularVelocity);
                    WriteBuffer.EnqueueOrientation(bufferedStates.Entity, ref value.Orientation);
                    WriteBuffer.EnqueuePosition(bufferedStates.Entity, ref value.Position);
                }
                else
                {
                    bufferedStates.Entity.MotionState = value;
                }
            }
        }


    }
}
