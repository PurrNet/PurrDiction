namespace PurrNet.Prediction.StateMachine
{
    public interface IPredictedStateNodeBase
    {
        int stateId { get; }
        void Setup(PredictedStateMachine stateMachine);
        void Enter();
        void ViewEnter(bool isVerified);
        void StateSimulate(float delta);
        void Exit();
        void ViewExit(bool isVerified);
    }

    public abstract class PredictedStateNode<T> : PredictedIdentity<T>, IPredictedStateNodeBase
        where T : struct, IPredictedData<T>
    {
        protected PredictedStateMachine machine { get; private set; }
        public int stateId => machine ? machine.GetStateId(this) : -1;

        public void Setup(PredictedStateMachine stateMachine)
        {
            machine = stateMachine;
        }

        public virtual void Enter() {}
        public virtual void ViewEnter(bool isVerified) { }
        protected virtual void StateSimulate(ref T state, float delta) {}
        protected virtual void StateUpdateView(T predictedState, T? validatedState) { }

        public void StateSimulate(float delta)
        {
            var s= currentState;
            StateSimulate(ref s, delta);
            currentState=s;
        }

        protected override void UpdateView(T vs, T? verified)
        {
            base.UpdateView(vs, verified);
            if(machine && ReferenceEquals(machine.currentStateNode, this))
                StateUpdateView(vs, verified);
        }

        public virtual void Exit() {}
        public virtual void ViewExit(bool isVerified) { }
    }

    public abstract class PredictedStateNode<TInput, T> : PredictedIdentity<TInput, T>, IPredictedStateNodeBase
        where T : struct, IPredictedData<T>
        where TInput : struct, IPredictedData
    {
        protected PredictedStateMachine machine { get; private set; }
        public int stateId => machine ? machine.GetStateId(this) : -1;

        public void Setup(PredictedStateMachine stateMachine)
        {
            machine = stateMachine;
        }

        public virtual void Enter() { }
        public virtual void ViewEnter(bool isVerified) { }
        protected virtual void StateSimulate(in TInput input, ref T state, float delta) {}
        protected virtual void StateUpdateView(T predictedState, T? validatedState) { }

        public void StateSimulate(float delta)
        {
            var s= currentState;
            var i = currentInput;
            StateSimulate(in i, ref s, delta);
            currentState=s;
        }

        protected override void UpdateView(T vs, T? verified)
        {
            base.UpdateView(vs, verified);
            if(machine && ReferenceEquals(machine.currentStateNode, this))
                StateUpdateView(vs, verified);
        }

        public virtual void Exit() { }
        public virtual void ViewExit(bool isVerified) { }
    }
}
