using System;

namespace PurrNet.Prediction
{
    public abstract class PredictedDelegate<TDelegate> where TDelegate : Delegate
    {
        private readonly PredictionManager _world;
        private readonly PredictedIdentity _identity;

        protected TDelegate onInvoke;

        protected PredictedDelegate(PredictionManager world, PredictedIdentity identity)
        {
            _world = world;
            _identity = identity;
        }

        public void AddListener(TDelegate action)
        {
            onInvoke = (TDelegate) Delegate.Combine(onInvoke, action);
        }

        public void RemoveListener(TDelegate action)
        {
            onInvoke = (TDelegate) Delegate.Remove(onInvoke, action);
        }

        public void RemoveAllListeners()
        {
            onInvoke = null;
        }

        protected bool ShouldInvoke()
        {
            if (_world.isCatchingUpFrames)
            {
                return false;
            }

            if (_world.cachedIsServer)
            {
                return true;
            }

            if (_identity.IsOwner())
            {
                return !_world.isReplaying;
            }

            return _world.isVerified;
        }
    }

    public sealed class PredictedEvent : PredictedDelegate<Action>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke()
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke();
            }
        }
    }

    public sealed class PredictedEvent<T> : PredictedDelegate<Action<T>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T value)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(value);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2> : PredictedDelegate<Action<T1, T2>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3> : PredictedDelegate<Action<T1, T2, T3>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4> : PredictedDelegate<Action<T1, T2, T3, T4>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5> : PredictedDelegate<Action<T1, T2, T3, T4, T5>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> : PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13,
            T14 arg14)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13,
            T14 arg14,
            T15 arg15)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15);
            }
        }
    }

    public sealed class PredictedEvent<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16> :
        PredictedDelegate<Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16>>
    {
        public PredictedEvent(PredictionManager world, PredictedIdentity identity) : base(world, identity) { }

        public void Invoke(T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6,
            T7 arg7,
            T8 arg8,
            T9 arg9,
            T10 arg10,
            T11 arg11,
            T12 arg12,
            T13 arg13,
            T14 arg14,
            T15 arg15,
            T16 arg16)
        {
            if (ShouldInvoke())
            {
                onInvoke?.Invoke(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13, arg14, arg15, arg16);
            }
        }
    }
}