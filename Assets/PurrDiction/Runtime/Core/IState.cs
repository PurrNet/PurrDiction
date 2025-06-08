using System;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    public interface IPredictedData : IDisposable, IPackedAuto
    {

    }

    public interface IPredictedData<T> : IPredictedData, IMath<T>
    {

    }
}
