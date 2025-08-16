using System;

namespace PurrNet.Prediction
{
    [AttributeUsage(AttributeTargets.Class)]
    public class PredictionUnsafeAttribute : Attribute
    {
        // This attribute is used to mark unsafe methods or properties in the prediction system.
        // It serves as a reminder that the marked code may not be safe for all use cases.
    }
}