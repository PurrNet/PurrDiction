/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Jitter2.LinearMath;

using Real = PurrNet.Prediction.FP64;

namespace Jitter2.Collision.Shapes
{
    /// <summary>
    /// Implements a SIMD accelerated support map for a set of vertices.
    /// </summary>
    public struct VertexSupportMap : ISupportMappable, IEquatable<VertexSupportMap>
    {
        private readonly Real[] xvalues, yvalues, zvalues;
        private JVector center;

        public VertexSupportMap(IReadOnlyList<JVector> vertices)
        {
            int length = vertices.Count;

            xvalues = new Real[length];
            yvalues = new Real[length];
            zvalues = new Real[length];

            center = JVector.Zero;

            for (int i = 0; i < length; i++)
            {
                xvalues[i] = vertices[i].X;
                yvalues[i] = vertices[i].Y;
                zvalues[i] = vertices[i].Z;

                center.X += vertices[i].X;
                center.Y += vertices[i].Y;
                center.Z += vertices[i].Z;
            }

            center *= (Real)1.0 / length;
        }

        public readonly void SupportMap(in JVector direction, out JVector result)
        {
            Real maxDotProduct = Real.MinValue;
            int length = xvalues.Length;
            int index = 0;

            for (int i = 0; i < length; i++)
            {
                Real dotProduct = xvalues[i] * direction.X +
                                  yvalues[i] * direction.Y +
                                  zvalues[i] * direction.Z;

                if (dotProduct < maxDotProduct) continue;
                maxDotProduct = dotProduct;
                index = i;
            }

            result = new JVector(xvalues[index], yvalues[index], zvalues[index]);
        }

        public readonly void GetCenter(out JVector point) => point = center;

        public readonly bool Equals(VertexSupportMap other) => xvalues.Equals(other.xvalues) &&
                                                               yvalues.Equals(other.yvalues) &&
                                                               zvalues.Equals(other.zvalues);

        public readonly override bool Equals(object obj) => obj is VertexSupportMap other && Equals(other);

        public readonly override int GetHashCode() => HashCode.Combine(xvalues, yvalues, zvalues);

        public static bool operator ==(VertexSupportMap left, VertexSupportMap right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(VertexSupportMap left, VertexSupportMap right)
        {
            return !(left == right);
        }
    }
}
