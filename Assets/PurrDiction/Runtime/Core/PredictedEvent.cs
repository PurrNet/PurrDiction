using System;

namespace PurrNet.Prediction
{
    public class PredictedEvent
    {
        private readonly PredictionManager _world;
        private readonly PredictedIdentity _identity;

        private event Action onInvoke;

        public PredictedEvent(PredictionManager world, PredictedIdentity identity)
        {
            _world = world;
            _identity = identity;
        }

        public void AddListener(Action action)
        {
            onInvoke += action;
        }

        public void RemoveListener(Action action)
        {
            onInvoke -= action;
        }

        public void RemoveAllListeners()
        {
            onInvoke = null;
        }

        public void Invoke()
        {
            if (_world.cachedIsServer)
            {
                onInvoke?.Invoke();
                return;
            }

            bool isOwner = _identity.IsOwner();

            if (isOwner)
            {
                if (_world.isReplaying)
                    return;

                onInvoke?.Invoke();
                return;
            }

            if (!_world.isVerified)
                return;

            onInvoke?.Invoke();
        }
    }

    public class PredictedEvent<T>
    {
        private readonly PredictionManager _world;
        private readonly PredictedIdentity _identity;

        private event Action<T> onInvoke;

        public PredictedEvent(PredictionManager world, PredictedIdentity identity)
        {
            _world = world;
            _identity = identity;
        }

        public void AddListener(Action<T> action)
        {
            onInvoke += action;
        }

        public void RemoveListener(Action<T> action)
        {
            onInvoke -= action;
        }

        public void Invoke(T value)
        {
            if (_world.cachedIsServer)
            {
                onInvoke?.Invoke(value);
                return;
            }

            bool isOwner = _identity.IsOwner();

            if (isOwner)
            {
                if (_world.isReplaying)
                    return;

                onInvoke?.Invoke(value);
                return;
            }

            if (!_world.isVerified)
                return;

            onInvoke?.Invoke(value);
        }
    }
}
