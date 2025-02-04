

using BEPUutilities;
namespace BEPUphysics.DataStructures
{
    ///<summary>
    /// Superclass of the data used to create triangle mesh bounding box trees.
    ///</summary>
    public abstract class MeshBoundingBoxTreeData
    {
        internal int[] indices;
        ///<summary>
        /// Gets or sets the indices of the triangle mesh.
        ///</summary>
        public int[] Indices
        {
            get
            {
                return indices;
            }
            set
            {
                indices = value;
            }
        }

        internal FPVector3[] vertices;
        ///<summary>
        /// Gets or sets the vertices of the triangle mesh.
        ///</summary>
        public FPVector3[] Vertices
        {
            get
            {
                return vertices;
            }
            set
            {
                vertices = value;
            }
        }

        /// <summary>
        /// Gets the bounding box of an element in the data.
        /// </summary>
        /// <param name="triangleIndex">Index of the triangle in the data.</param>
        /// <param name="boundingBox">Bounding box of the triangle.</param>
        public void GetBoundingBox(int triangleIndex, out FPBoundingBox boundingBox)
        {
            FPVector3 v1, v2, v3;
            GetTriangle(triangleIndex, out v1, out v2, out v3);
            FPVector3.Min(ref v1, ref v2, out boundingBox.Min);
            FPVector3.Min(ref boundingBox.Min, ref v3, out boundingBox.Min);
            FPVector3.Max(ref v1, ref v2, out boundingBox.Max);
            FPVector3.Max(ref boundingBox.Max, ref v3, out boundingBox.Max);

        }
        ///<summary>
        /// Gets the triangle vertex positions at a given index.
        ///</summary>
        ///<param name="triangleIndex">First index of a triangle's vertices in the index buffer.</param>
        ///<param name="v1">First vertex of the triangle.</param>
        ///<param name="v2">Second vertex of the triangle.</param>
        ///<param name="v3">Third vertex of the triangle.</param>
        public abstract void GetTriangle(int triangleIndex, out FPVector3 v1, out FPVector3 v2, out FPVector3 v3);
        ///<summary>
        /// Gets the position of a vertex in the data.
        ///</summary>
        ///<param name="i">Index of the vertex.</param>
        ///<param name="vertex">Position of the vertex.</param>
        public abstract void GetVertexPosition(int i, out FPVector3 vertex);
    }
}
