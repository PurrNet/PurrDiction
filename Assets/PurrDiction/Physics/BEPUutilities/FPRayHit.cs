using FixMath.NET;

namespace BEPUutilities
{
    ///<summary>
    /// Contains ray hit data.
    ///</summary>
    public struct FPRayHit
    {
        ///<summary>
        /// Location of the ray hit.
        ///</summary>
        public FPVector3 Location;
        ///<summary>
        /// Normal of the ray hit.
        ///</summary>
        public FPVector3 Normal;
        ///<summary>
        /// T parameter of the ray hit.  
        /// The ray hit location is equal to the ray origin added to the ray direction multiplied by T.
        ///</summary>
        public FP T;
    }
}
