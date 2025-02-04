using FixMath.NET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BEPUutilities
{    
    /// <summary>
    /// Provides XNA-like bounding sphere functionality.
    /// </summary>
    public struct FPBoundingSphere
    {
        /// <summary>
        /// Radius of the sphere.
        /// </summary>
        public FP Radius;
        /// <summary>
        /// Location of the center of the sphere.
        /// </summary>
        public FPVector3 Center;

        /// <summary>
        /// Constructs a new bounding sphere.
        /// </summary>
        /// <param name="center">Location of the center of the sphere.</param>
        /// <param name="radius">Radius of the sphere.</param>
        public FPBoundingSphere(FPVector3 center, FP radius)
        {
            this.Center = center;
            this.Radius = radius;
        }
    }
}
