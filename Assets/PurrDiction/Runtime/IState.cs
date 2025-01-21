using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public interface IOptionalDispose : IDisposable
    {
        void IDisposable.Dispose() {}
    }
    
    public interface IPredictedData : IOptionalDispose, IPackedAuto
    {
        
    }
    
    public interface IPredictedData<T> : IPredictedData, IMath<T>
    {
        
    }
}