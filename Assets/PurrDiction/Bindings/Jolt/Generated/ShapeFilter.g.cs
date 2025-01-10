using System;
using UnityEngine;

namespace Jolt
{
    public readonly partial struct ShapeFilter : IEquatable<ShapeFilter>
    {
        public bool Equals(ShapeFilter other)
        {
            throw new NotImplementedException();
        }

        public override bool Equals(object obj)
        {
            return obj is ShapeFilter other && Equals(other);
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}