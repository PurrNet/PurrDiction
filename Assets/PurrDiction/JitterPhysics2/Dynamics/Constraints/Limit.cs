/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using Jitter2.LinearMath;

using Real = PurrNet.Prediction.FP64;
using MathR = PurrNet.Prediction.FP64Math;

namespace Jitter2.Dynamics.Constraints
{
    public struct AngularLimit
    {
        public AngularLimit(JAngle from, JAngle to)
        {
            From = from;
            To = to;
        }

        public JAngle From { get; set; }
        public JAngle To { get; set; }

        public static readonly AngularLimit Full =
            new(JAngle.FromRadian(-Real.PI), JAngle.FromRadian(Real.PI));

        public static readonly AngularLimit Fixed =
            new(JAngle.FromRadian(+(Real)1e-6), JAngle.FromRadian(-(Real)1e-6));

        public static AngularLimit FromDegree(Real min, Real max)
        {
            return new AngularLimit(JAngle.FromDegree(min), JAngle.FromDegree(max));
        }

        public readonly void Deconstruct(out JAngle limitMin, out JAngle limitMax)
        {
            limitMin = From;
            limitMax = To;
        }
    }

    public struct LinearLimit
    {
        public LinearLimit(Real from, Real to)
        {
            From = from;
            To = to;
        }

        public Real From { get; set; }
        public Real To { get; set; }

        public static readonly LinearLimit Full =
            new(Real.MinValue, Real.MaxValue);

        public static readonly LinearLimit Fixed =
            new(1e-6, -(Real)1e-6);

        public static LinearLimit FromMinMax(Real min, Real max)
        {
            return new LinearLimit(min, max);
        }

        public readonly void Deconstruct(out Real limitMin, out Real limitMax)
        {
            limitMin = From;
            limitMax = To;
        }
    }
}
