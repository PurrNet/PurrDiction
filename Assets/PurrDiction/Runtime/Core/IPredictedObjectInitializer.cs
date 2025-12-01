using System;
using System.Collections.Generic;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Implement on a component to receive initialization callback
    /// before PredictedIdentity components are registered.
    /// Use this to add PredictedIdentity components via DI.
    /// </summary>
    public interface IPredictedObjectInitializer
    {
        /// <summary>
        /// Called after GameObject instantiation but before PredictedIdentity registration.
        /// Add your PredictedIdentity components here - they will be picked up by GetComponentsInChildren.
        /// </summary>
        void OnBeforePredictedRegister(PredictedSpawnContext context);
    }
}