using System;
using PurrNet.Packing;

namespace PurrNet.Prediction.Tests
{
    public struct SimpleWASDInput : IPredictedData, IEquatable<SimpleWASDInput>
    {
        public NormalizedFloat horizontal;
        public NormalizedFloat vertical;
        public bool jump;
        public bool dash;

        public void Dispose() { }

        public override string ToString()
        {
            return $"horizontal: {horizontal}\nvertical: {vertical}\njump: {jump}\ndash: {dash})";
        }

        public bool Equals(SimpleWASDInput other)
        {
            return horizontal.value == other.horizontal.value &&
                   vertical.value == other.vertical.value
                   && jump == other.jump && dash == other.dash;
        }

        public override bool Equals(object obj)
        {
            return obj is SimpleWASDInput other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(horizontal.value, vertical.value, jump, dash);
        }
    }

    public struct SimpleCCState : IPredictedData<SimpleCCState>
    {
        public float rotation;

        public void Dispose() { }

        public override string ToString()
        {
            return $"rotation: {rotation}";
        }
    }
}
