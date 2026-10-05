using System;

namespace PurrNet.Prediction
{
    // A known missing acknowledged baseline can be repaired with a full snapshot.
    // Keep this distinct from malformed payloads and incompatible module rosters.
    internal sealed class MissingPredictionBaselineException : InvalidOperationException
    {
        public MissingPredictionBaselineException(string message) : base(message) { }
    }
}
