using System;
using PurrNet.Modules;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Attribute to mark a method as simulation only.
    /// It won't be executed unless the prediction manager is simulating.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    [UsedByIL]
    public class SimulationOnlyAttribute : Attribute
    {
        
    }
}
