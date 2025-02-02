using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.StateMachine
{
    public interface IPredictedStateNodeBase
    {
        void Setup(PredictedStateMachine stateMachine);
        void Enter();
        void Exit();
    }
    
    public interface IPredictedStateNodeBase<T> : IPredictedStateNodeBase where T : struct, IPredictedData<T>
    {
        void Enter(T data);
    }

    public abstract class PredictedStateNode<T> : PredictedIdentity<T>, IPredictedStateNodeBase where T : struct, IPredictedData<T>
    {
        protected PredictedStateMachine machine { get; private set; }

        public void Setup(PredictedStateMachine stateMachine)
        {
            machine = stateMachine;
        }
        
        public virtual void Enter() {}

        public virtual void Exit() {}
    }

    public abstract class PredictedStateNode<T, TData> : PredictedStateNode<T>, IPredictedStateNodeBase<TData>
        where T : struct, IPredictedData<T> where TData : struct, IPredictedData<TData>
    {
        public virtual void Enter(TData data) {}

        public override void Enter()
        {
            PurrLogger.LogError($"Attempted to enter state {GetType().Name} without required data and no other override");
        }
    }
}
