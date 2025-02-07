using FixMath.NET;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction.StateMachine
{
    public interface IPredictedStateNodeBase
    {
        void Setup(PredictedStateMachine stateMachine);
        void Enter();
        void ViewEnter(bool isVerified);
        void StateSimulate(FP delta);
        void Exit();
        void ViewExit(bool isVerified);
    }

    public abstract class PredictedStateNode<T> : PredictedIdentity<T>, IPredictedStateNodeBase 
        where T : struct, IPredictedData<T>
    {
        protected PredictedStateMachine machine { get; private set; }

        public void Setup(PredictedStateMachine stateMachine)
        {
            machine = stateMachine;
        }
        
        public virtual void Enter() {}
        public virtual void ViewEnter(bool isVerified) { }
        public virtual void StateSimulate(FP delta) { }
        public virtual void Exit() {}
        public virtual void ViewExit(bool isVerified) { }
    }
    
    public abstract class PredictedStateNode<TInput, T> : PredictedIdentity<TInput, T>, IPredictedStateNodeBase 
        where T : struct, IPredictedData<T> 
        where TInput : struct, IPredictedData
    {
        protected PredictedStateMachine machine { get; private set; }
        public void Setup(PredictedStateMachine stateMachine)
        {
            machine = stateMachine;
        }

        public virtual void Enter() { }
        public virtual void ViewEnter(bool isVerified) { }
        public virtual void StateSimulate(FP delta) { }
        public virtual void Exit() { }
        public virtual void ViewExit(bool isVerified) { }
    }
}
