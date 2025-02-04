using BEPUutilities;
using FixMath.NET;

namespace BEPUphysics.CollisionTests.CollisionAlgorithms.GJK
{

    ///<summary>
    /// GJK simplex supporting ray-based tests.
    ///</summary>
    public struct RaySimplex
    {
        ///<summary>
        /// First vertex in the simplex.
        ///</summary>
        public FPVector3 A;
        /// <summary>
        /// Second vertex in the simplex.
        /// </summary>
        public FPVector3 B;
        /// <summary>
        /// Third vertex in the simplex.
        /// </summary>
        public FPVector3 C;
        /// <summary>
        /// Fourth vertex in the simplex.
        /// </summary>
        public FPVector3 D;
        /// <summary>
        /// Current state of the simplex.
        /// </summary>
        public SimplexState State;



        ///<summary>
        /// Gets the point on the simplex that is closest to the origin.
        ///</summary>
        ///<param name="simplex">Simplex to test.</param>
        ///<param name="point">Closest point on the simplex.</param>
        ///<returns>Whether or not the simplex contains the origin.</returns>
        public bool GetPointClosestToOrigin(ref RaySimplex simplex, out FPVector3 point)
        {
            //This method finds the closest point on the simplex to the origin.
            //Barycentric coordinates are assigned to the MinimumNormCoordinates as necessary to perform the inclusion calculation.
            //If the simplex is a tetrahedron and found to be overlapping the origin, the function returns true to tell the caller to terminate.
            //Elements of the simplex that are not used to determine the point of minimum norm are removed from the simplex.

            switch (State)
            {

                case SimplexState.Point:
                    point = A;
                    break;
                case SimplexState.Segment:
                    GetPointOnSegmentClosestToOrigin(ref simplex, out point);
                    break;
                case SimplexState.Triangle:
                    GetPointOnTriangleClosestToOrigin(ref simplex, out point);
                    break;
                case SimplexState.Tetrahedron:
                    return GetPointOnTetrahedronClosestToOrigin(ref simplex, out point);
                default:
                    point = Toolbox.ZeroVector;
                    break;


            }
            return false;
        }


        ///<summary>
        /// Finds the point on the segment to the origin.
        ///</summary>
        ///<param name="simplex">Simplex to test.</param>
        ///<param name="point">Closest point.</param>
        public void GetPointOnSegmentClosestToOrigin(ref RaySimplex simplex, out FPVector3 point)
        {
            FPVector3 segmentDisplacement;
            FPVector3.Subtract(ref B, ref A, out segmentDisplacement);

            FP dotA;
            FPVector3.Dot(ref segmentDisplacement, ref A, out dotA);
            if (dotA > F64.C0)
            {
                //'Behind' segment.  This can't happen in a boolean version,
                //but with closest points warmstarting or raycasts, it will.
                simplex.State = SimplexState.Point;

                point = A;
                return;
            }
            FP dotB;
            FPVector3.Dot(ref segmentDisplacement, ref B, out dotB);
            if (dotB > F64.C0)
            {
                //Inside segment.
                FP V = -dotA / segmentDisplacement.LengthSquared();
                FPVector3.Multiply(ref segmentDisplacement, V, out point);
                FPVector3.Add(ref point, ref A, out point);
                return;

            }

            //It should be possible in the warmstarted closest point calculation/raycasting to be outside B.
            //It is not possible in a 'boolean' GJK, where it early outs as soon as a separating axis is found.

            //Outside B.
            //Remove current A; we're becoming a point.
            simplex.A = simplex.B;
            simplex.State = SimplexState.Point;

            point = A;

        }

        ///<summary>
        /// Gets the point on the triangle that is closest to the origin.
        ///</summary>
        ///<param name="simplex">Simplex to test.</param>
        ///<param name="point">Closest point to origin.</param>
        public void GetPointOnTriangleClosestToOrigin(ref RaySimplex simplex, out FPVector3 point)
        {
            FPVector3 ab, ac;
            FPVector3.Subtract(ref B, ref A, out ab);
            FPVector3.Subtract(ref C, ref A, out ac);
            //The point we are comparing against the triangle is 0,0,0, so instead of storing an "A->P" vector,
            //just use -A.
            //Same for B->P, C->P...

            //Check to see if it's outside A.
            //TODO: Note that in a boolean-style GJK, it shouldn't be possible to be outside A.
            FP AdotAB, AdotAC;
            FPVector3.Dot(ref ab, ref A, out AdotAB);
            FPVector3.Dot(ref ac, ref A, out AdotAC);
            AdotAB = -AdotAB;
            AdotAC = -AdotAC;
            if (AdotAC <= F64.C0 && AdotAB <= F64.C0)
            {
                //It is A!
                simplex.State = SimplexState.Point;
                point = A;
                return;
            }

            //Check to see if it's outside B.
            //TODO: Note that in a boolean-style GJK, it shouldn't be possible to be outside B.
            FP BdotAB, BdotAC;
            FPVector3.Dot(ref ab, ref B, out BdotAB);
            FPVector3.Dot(ref ac, ref B, out BdotAC);
            BdotAB = -BdotAB;
            BdotAC = -BdotAC;
            if (BdotAB >= F64.C0 && BdotAC <= BdotAB)
            {
                //It is B!
                simplex.State = SimplexState.Point;
                simplex.A = simplex.B;

                point = B;
                return;
            }

            //Check to see if it's outside AB.
            FP vc = AdotAB * BdotAC - BdotAB * AdotAC;
            if (vc <= F64.C0 && AdotAB > F64.C0 && BdotAB < F64.C0)//Note > and < instead of => <=; avoids possibly division by zero
            {
                simplex.State = SimplexState.Segment;
                FP V = AdotAB / (AdotAB - BdotAB);

                FPVector3.Multiply(ref ab, V, out point);
                FPVector3.Add(ref point, ref A, out point);
                return;
            }

            //Check to see if it's outside C.
            //TODO: Note that in a boolean-style GJK, it shouldn't be possible to be outside C.
            FP CdotAB, CdotAC;
            FPVector3.Dot(ref ab, ref C, out CdotAB);
            FPVector3.Dot(ref ac, ref C, out CdotAC);
            CdotAB = -CdotAB;
            CdotAC = -CdotAC;
            if (CdotAC >= F64.C0 && CdotAB <= CdotAC)
            {
                //It is C!
                simplex.State = SimplexState.Point;
                simplex.A = simplex.C;
                point = A;
                return;
            }

            //Check if it's outside AC.            
            //Fix64 AdotAB, AdotAC;
            //Vector3.Dot(ref ab, ref A, out AdotAB);
            //Vector3.Dot(ref ac, ref A, out AdotAC);
            //AdotAB = -AdotAB;
            //AdotAC = -AdotAC;
            FP vb = CdotAB * AdotAC - AdotAB * CdotAC;
            if (vb <= F64.C0 && AdotAC > F64.C0 && CdotAC < F64.C0)//Note > instead of >= and < instead of <=; prevents bad denominator
            {
                //Get rid of B.  Compress C into B.
                simplex.State = SimplexState.Segment;
                simplex.B = simplex.C;
                FP V = AdotAC / (AdotAC - CdotAC);
                FPVector3.Multiply(ref ac, V, out point);
                FPVector3.Add(ref point, ref A, out point);
                return;
            }

            //Check if it's outside BC.
            //Fix64 BdotAB, BdotAC;
            //Vector3.Dot(ref ab, ref B, out BdotAB);
            //Vector3.Dot(ref ac, ref B, out BdotAC);
            //BdotAB = -BdotAB;
            //BdotAC = -BdotAC;
            FP va = BdotAB * CdotAC - CdotAB * BdotAC;
            FP d3d4;
            FP d6d5;
            if (va <= F64.C0 && (d3d4 = BdotAC - BdotAB) > F64.C0 && (d6d5 = CdotAB - CdotAC) > F64.C0)//Note > instead of >= and < instead of <=; prevents bad denominator
            {
                //Throw away A.  C->A.
                //TODO: Does B->A, C->B work better?
                simplex.State = SimplexState.Segment;
                simplex.A = simplex.C;
                FP U = d3d4 / (d3d4 + d6d5);

                FPVector3 bc;
                FPVector3.Subtract(ref C, ref B, out bc);
                FPVector3.Multiply(ref bc, U, out point);
                FPVector3.Add(ref point, ref B, out point);
                return;
            }


            //On the face of the triangle.
            FP denom = F64.C1 / (va + vb + vc);
            FP v = vb * denom;
            FP w = vc * denom;
            FPVector3.Multiply(ref ab, v, out point);
            FPVector3 acw;
            FPVector3.Multiply(ref ac, w, out acw);
            FPVector3.Add(ref A, ref point, out point);
            FPVector3.Add(ref point, ref acw, out point);




        }

        ///<summary>
        /// Gets the point closest to the origin on the tetrahedron.
        ///</summary>
        ///<param name="simplex">Simplex to test.</param>
        ///<param name="point">Closest point.</param>
        ///<returns>Whether or not the tetrahedron encloses the origin.</returns>
        public bool GetPointOnTetrahedronClosestToOrigin(ref RaySimplex simplex, out FPVector3 point)
        {

            //Thanks to the fact that D is new and that we know that the origin is within the extruded
            //triangular prism of ABC (and on the "D" side of ABC),
            //we can immediately ignore voronoi regions:
            //A, B, C, AC, AB, BC, ABC
            //and only consider:
            //D, DA, DB, DC, DAC, DCB, DBA

            //There is some overlap of calculations in this method, since DAC, DCB, and DBA are tested fully.
            
            //When this method is being called, we don't care about the state of 'this' simplex.  It's just a temporary shifted simplex.
            //The one that needs to be updated is the simplex being passed in.
            
            var minimumSimplex = new RaySimplex();
            point = new FPVector3();
            FP minimumDistance = FP.MaxValue;


            RaySimplex candidate;
            FP candidateDistance;
            FPVector3 candidatePoint;
            if (TryTetrahedronTriangle(ref A, ref C, ref D,
                                       ref simplex.A, ref simplex.C, ref simplex.D,
                                       ref B, out candidate, out candidatePoint))
            {
                point = candidatePoint;
                minimumSimplex = candidate;
                minimumDistance = candidatePoint.LengthSquared();
            }

            if (TryTetrahedronTriangle(ref C, ref B, ref D,
                                       ref simplex.C, ref simplex.B, ref simplex.D,
                                       ref A, out candidate, out candidatePoint) &&
                (candidateDistance = candidatePoint.LengthSquared()) < minimumDistance)
            {
                point = candidatePoint;
                minimumSimplex = candidate;
                minimumDistance = candidateDistance;
            }

            if (TryTetrahedronTriangle(ref B, ref A, ref D,
                                       ref simplex.B, ref simplex.A, ref simplex.D,
                                       ref C, out candidate, out candidatePoint) &&
                (candidateDistance = candidatePoint.LengthSquared()) < minimumDistance)
            {
                point = candidatePoint;
                minimumSimplex = candidate;
                minimumDistance = candidateDistance;
            }

            if (TryTetrahedronTriangle(ref A, ref B, ref C,
                                       ref simplex.A, ref simplex.B, ref simplex.C,
                                       ref D, out candidate, out candidatePoint) &&
                (candidateDistance = candidatePoint.LengthSquared()) < minimumDistance)
            {
                point = candidatePoint;
                minimumSimplex = candidate;
                minimumDistance = candidateDistance;
            }


            if (minimumDistance < FP.MaxValue)
            {
                simplex = minimumSimplex;
                return false;
            }
            return true;
        }


        private static bool TryTetrahedronTriangle(ref FPVector3 A, ref FPVector3 B, ref FPVector3 C,
                                                   ref FPVector3 simplexA, ref FPVector3 simplexB, ref FPVector3 simplexC,
                                                   ref FPVector3 otherPoint, out RaySimplex simplex, out FPVector3 point)
        {
            //Note that there may be some extra terms that can be removed from this process.
            //Some conditions could use less parameters, since it is known that the origin
            //is not 'behind' BC or AC.

            simplex = new RaySimplex();
            point = new FPVector3();


            FPVector3 ab, ac;
            FPVector3.Subtract(ref B, ref A, out ab);
            FPVector3.Subtract(ref C, ref A, out ac);
            FPVector3 normal;
            FPVector3.Cross(ref ab, ref ac, out normal);
            FP AdotN, ADdotN;
            FPVector3 AD;
            FPVector3.Subtract(ref otherPoint, ref A, out AD);
            FPVector3.Dot(ref A, ref normal, out AdotN);
            FPVector3.Dot(ref AD, ref normal, out ADdotN);

            //If (-A * N) * (AD * N) < 0, D and O are on opposite sides of the triangle.
            if (AdotN * ADdotN >= F64.C0)
            {
                //The point we are comparing against the triangle is 0,0,0, so instead of storing an "A->P" vector,
                //just use -A.
                //Same for B->, C->P...

                //Check to see if it's outside A.
                //TODO: Note that in a boolean-style GJK, it shouldn't be possible to be outside A.
                FP AdotAB, AdotAC;
                FPVector3.Dot(ref ab, ref A, out AdotAB);
                FPVector3.Dot(ref ac, ref A, out AdotAC);
                AdotAB = -AdotAB;
                AdotAC = -AdotAC;
                if (AdotAC <= F64.C0 && AdotAB <= F64.C0)
                {
                    //It is A!
                    simplex.State = SimplexState.Point;
                    simplex.A = simplexA;
                    point = A;
                    return true;
                }

                //Check to see if it's outside B.
                //TODO: Note that in a boolean-style GJK, it shouldn't be possible to be outside B.
                FP BdotAB, BdotAC;
                FPVector3.Dot(ref ab, ref B, out BdotAB);
                FPVector3.Dot(ref ac, ref B, out BdotAC);
                BdotAB = -BdotAB;
                BdotAC = -BdotAC;
                if (BdotAB >= F64.C0 && BdotAC <= BdotAB)
                {
                    //It is B!
                    simplex.State = SimplexState.Point;
                    simplex.A = simplexB;
                    point = B;
                    return true;
                }

                //Check to see if it's outside AB.
                FP vc = AdotAB * BdotAC - BdotAB * AdotAC;
                if (vc <= F64.C0 && AdotAB > F64.C0 && BdotAB < F64.C0) //Note > and < instead of => <=; avoids possibly division by zero
                {
                    simplex.State = SimplexState.Segment;
                    simplex.A = simplexA;
                    simplex.B = simplexB;
                    FP V = AdotAB / (AdotAB - BdotAB);

                    FPVector3.Multiply(ref ab, V, out point);
                    FPVector3.Add(ref point, ref A, out point);
                    return true;
                }

                //Check to see if it's outside C.
                //TODO: Note that in a boolean-style GJK, it shouldn't be possible to be outside C.
                FP CdotAB, CdotAC;
                FPVector3.Dot(ref ab, ref C, out CdotAB);
                FPVector3.Dot(ref ac, ref C, out CdotAC);
                CdotAB = -CdotAB;
                CdotAC = -CdotAC;
                if (CdotAC >= F64.C0 && CdotAB <= CdotAC)
                {
                    //It is C!
                    simplex.State = SimplexState.Point;
                    simplex.A = simplexC;
                    point = C;
                    return true;
                }

                //Check if it's outside AC.            
                //Fix64 AdotAB, AdotAC;
                //Vector3.Dot(ref ab, ref A, out AdotAB);
                //Vector3.Dot(ref ac, ref A, out AdotAC);
                //AdotAB = -AdotAB;
                //AdotAC = -AdotAC;
                FP vb = CdotAB * AdotAC - AdotAB * CdotAC;
                if (vb <= F64.C0 && AdotAC > F64.C0 && CdotAC < F64.C0) //Note > instead of >= and < instead of <=; prevents bad denominator
                {
                    simplex.State = SimplexState.Segment;
                    simplex.A = simplexA;
                    simplex.B = simplexC;
                    FP V = AdotAC / (AdotAC - CdotAC);
                    FPVector3.Multiply(ref ac, V, out point);
                    FPVector3.Add(ref point, ref A, out point);
                    return true;
                }

                //Check if it's outside BC.
                //Fix64 BdotAB, BdotAC;
                //Vector3.Dot(ref ab, ref B, out BdotAB);
                //Vector3.Dot(ref ac, ref B, out BdotAC);
                //BdotAB = -BdotAB;
                //BdotAC = -BdotAC;
                FP va = BdotAB * CdotAC - CdotAB * BdotAC;
                FP d3d4;
                FP d6d5;
                if (va <= F64.C0 && (d3d4 = BdotAC - BdotAB) > F64.C0 && (d6d5 = CdotAB - CdotAC) > F64.C0)//Note > instead of >= and < instead of <=; prevents bad denominator
                {
                    simplex.State = SimplexState.Segment;
                    simplex.A = simplexB;
                    simplex.B = simplexC;
                    FP V = d3d4 / (d3d4 + d6d5);

                    FPVector3 bc;
                    FPVector3.Subtract(ref C, ref B, out bc);
                    FPVector3.Multiply(ref bc, V, out point);
                    FPVector3.Add(ref point, ref B, out point);
                    return true;
                }


                //On the face of the triangle.
                simplex.State = SimplexState.Triangle;
                simplex.A = simplexA;
                simplex.B = simplexB;
                simplex.C = simplexC;
                FP denom = F64.C1 / (va + vb + vc);
                FP w = vc * denom;
                FP v = vb * denom;
                FPVector3.Multiply(ref ab, v, out point);
                FPVector3 acw;
                FPVector3.Multiply(ref ac, w, out acw);
                FPVector3.Add(ref A, ref point, out point);
                FPVector3.Add(ref point, ref acw, out point);
                return true;
            }
            return false;
        }



        ///<summary>
        /// Adds a new point to the simplex.
        ///</summary>
        ///<param name="point">Point to add.</param>
        ///<param name="hitLocation">Current ray hit location.</param>
        ///<param name="shiftedSimplex">Simplex shifted with the hit location.</param>
        public void AddNewSimplexPoint(ref FPVector3 point, ref FPVector3 hitLocation, out RaySimplex shiftedSimplex)
        {
            shiftedSimplex = new RaySimplex();
            switch (State)
            {
                case SimplexState.Empty:
                    State = SimplexState.Point;
                    A = point;

                    FPVector3.Subtract(ref hitLocation, ref A, out shiftedSimplex.A);
                    break;
                case SimplexState.Point:
                    State = SimplexState.Segment;
                    B = point;

                    FPVector3.Subtract(ref hitLocation, ref A, out shiftedSimplex.A);
                    FPVector3.Subtract(ref hitLocation, ref B, out shiftedSimplex.B);
                    break;
                case SimplexState.Segment:
                    State = SimplexState.Triangle;
                    C = point;

                    FPVector3.Subtract(ref hitLocation, ref A, out shiftedSimplex.A);
                    FPVector3.Subtract(ref hitLocation, ref B, out shiftedSimplex.B);
                    FPVector3.Subtract(ref hitLocation, ref C, out shiftedSimplex.C);
                    break;
                case SimplexState.Triangle:
                    State = SimplexState.Tetrahedron;
                    D = point;

                    FPVector3.Subtract(ref hitLocation, ref A, out shiftedSimplex.A);
                    FPVector3.Subtract(ref hitLocation, ref B, out shiftedSimplex.B);
                    FPVector3.Subtract(ref hitLocation, ref C, out shiftedSimplex.C);
                    FPVector3.Subtract(ref hitLocation, ref D, out shiftedSimplex.D);
                    break;
            }
            shiftedSimplex.State = State;
        }

        /// <summary>
        /// Gets the error tolerance for the simplex.
        /// </summary>
        /// <param name="rayOrigin">Origin of the ray.</param>
        /// <returns>Error tolerance of the simplex.</returns>
        public FP GetErrorTolerance(ref FPVector3 rayOrigin)
        {
            switch (State)
            {
                case SimplexState.Point:
                    FP distanceA;
                    FPVector3.DistanceSquared(ref A, ref rayOrigin, out distanceA);
                    return distanceA;
                case SimplexState.Segment:
                    FP distanceB;
                    FPVector3.DistanceSquared(ref A, ref rayOrigin, out distanceA);
                    FPVector3.DistanceSquared(ref B, ref rayOrigin, out distanceB);
                    return MathHelper.Max(distanceA, distanceB);
                case SimplexState.Triangle:
                    FP distanceC;
                    FPVector3.DistanceSquared(ref A, ref rayOrigin, out distanceA);
                    FPVector3.DistanceSquared(ref B, ref rayOrigin, out distanceB);
                    FPVector3.DistanceSquared(ref C, ref rayOrigin, out distanceC);
                    return MathHelper.Max(distanceA, MathHelper.Max(distanceB, distanceC));
                case SimplexState.Tetrahedron:
                    FP distanceD;
                    FPVector3.DistanceSquared(ref A, ref rayOrigin, out distanceA);
                    FPVector3.DistanceSquared(ref B, ref rayOrigin, out distanceB);
                    FPVector3.DistanceSquared(ref C, ref rayOrigin, out distanceC);
                    FPVector3.DistanceSquared(ref D, ref rayOrigin, out distanceD);
                    return MathHelper.Max(distanceA, MathHelper.Max(distanceB, MathHelper.Max(distanceC, distanceD)));
            }
            return F64.C0;
        }

    }

}
