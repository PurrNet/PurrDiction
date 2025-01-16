namespace PurrNet.Prediction
{
    public interface IOptionalDispose
    {
        void Dispose() {}
    }
    
    public interface IState : IOptionalDispose
    {
        
    }
}