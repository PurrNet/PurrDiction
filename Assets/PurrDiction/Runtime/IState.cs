using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public interface IOptionalDispose
    {
        void Dispose() {}
    }
    
    public interface IPredictedData : IOptionalDispose, IPackedAuto
    {
        
    }
    
    public interface IPredictedData<T> : IPredictedData, IMath<T>
    {
        
    }
}