using System.Collections.Generic;
using BEPUphysics.BroadPhaseEntries;
using BEPUphysics.BroadPhaseEntries.MobileCollidables;
using BEPUphysics.CollisionRuleManagement;
using BEPUphysics.Entities;
using BEPUphysics.NarrowPhaseSystems.Pairs;
using PurrNet.Logging;
using UnityEngine;

namespace PurrNet.Prediction
{
    public class BepuCollisionHandler
    {
        public delegate void TriggerEventHandler(GameObject other);
        public delegate void CollisionEventHandler(GameObject other);

        private readonly HashSet<Entity> _currentTriggerContacts = new HashSet<Entity>();
        private readonly HashSet<Entity> _currentCollisionContacts = new HashSet<Entity>();
        private readonly PredictionManager _predictionManager;
        private readonly bool _isTrigger;

        public event TriggerEventHandler onTriggerEnter;
        public event TriggerEventHandler onTriggerExit;
        public event CollisionEventHandler onCollisionEnter;
        public event CollisionEventHandler onCollisionExit;

        private readonly IBepuCollisionEnter[] _collisionEnterHandlers;
        private readonly IBepuCollisionExit[] _collisionExitHandlers;
        private readonly IBepuTriggerEnter[] _triggerEnterHandlers;
        private readonly IBepuTriggerExit[] _triggerExitHandlers;

        public BepuCollisionHandler(PredictionManager predictionManager, bool isTrigger, GameObject gameObject)
        {
            if(predictionManager == null)
                PurrLogger.LogError($"Attempted to create a collision handler without a prediction manager!");

            _predictionManager = predictionManager;
            _isTrigger = isTrigger;

            _collisionEnterHandlers = gameObject.GetComponents<IBepuCollisionEnter>();
            _collisionExitHandlers = gameObject.GetComponents<IBepuCollisionExit>();
            _triggerEnterHandlers = gameObject.GetComponents<IBepuTriggerEnter>();
            _triggerExitHandlers = gameObject.GetComponents<IBepuTriggerExit>();

            onCollisionEnter += HandleCollisionEnter;
            onCollisionExit += HandleCollisionExit;
            onTriggerEnter += HandleTriggerEnter;
            onTriggerExit += HandleTriggerExit;
        }

        public void SubscribeToEvents(EntityCollidable collidable)
        {
            collidable.Events.InitialCollisionDetected += HandleInitialEntityCollision;
            collidable.Events.CollisionEnded += HandleEntityCollisionEnd;
        }

        public void UnsubscribeFromEvents(EntityCollidable collidable)
        {
            collidable.Events.InitialCollisionDetected -= HandleInitialEntityCollision;
            collidable.Events.CollisionEnded -= HandleEntityCollisionEnd;
        }

        public void SubscribeToEvents(StaticMesh staticMesh)
        {
            staticMesh.Events.InitialCollisionDetected += HandleInitialStaticCollision;
            staticMesh.Events.CollisionEnded += HandleStaticCollisionEnd;
        }

        public void UnsubscribeFromEvents(StaticMesh staticMesh)
        {
            staticMesh.Events.InitialCollisionDetected -= HandleInitialStaticCollision;
            staticMesh.Events.CollisionEnded -= HandleStaticCollisionEnd;
        }

        private void HandleInitialEntityCollision(EntityCollidable sender, Collidable other, CollidablePairHandler pair)
        {
            HandleCollision(other);
        }

        private void HandleEntityCollisionEnd(EntityCollidable sender, Collidable other, CollidablePairHandler pair)
        {
            HandleCollisionEnd(other);
        }

        private void HandleInitialStaticCollision(StaticMesh sender, Collidable other, CollidablePairHandler pair)
        {
            HandleCollision(other);
        }

        private void HandleStaticCollisionEnd(StaticMesh sender, Collidable other, CollidablePairHandler pair)
        {
            HandleCollisionEnd(other);
        }

        private void HandleCollision(Collidable other)
        {
            if (!_predictionManager.isSimulating) return;

            GameObject otherGo = null;
            Entity otherEntity = null;

            if (other is EntityCollidable entityCollidable)
            {
                otherEntity = entityCollidable.Entity;
                if (otherEntity != null)
                    otherGo = otherEntity.Tag as GameObject;
            }
            else if (other is StaticMesh staticMesh)
            {
                otherGo = staticMesh.Tag as GameObject;
            }

            if (otherGo != null)
            {
                bool isTriggerCollision = _isTrigger ||
                                          other.CollisionRules.Personal == CollisionRule.NoSolver;

                if (isTriggerCollision)
                {
                    if (otherEntity != null)
                        _currentTriggerContacts.Add(otherEntity);

                    onTriggerEnter?.Invoke(otherGo);
                    return;
                }

                if (otherEntity != null)
                    _currentCollisionContacts.Add(otherEntity);

                onCollisionEnter?.Invoke(otherGo);
            }
        }

        private void HandleCollisionEnd(Collidable other)
        {
            if (!_predictionManager.isSimulating) return;

            GameObject otherGo = null;
            Entity otherEntity = null;

            if (other is EntityCollidable entityCollidable)
            {
                otherEntity = entityCollidable.Entity;
                if (otherEntity != null)
                    otherGo = otherEntity.Tag as GameObject;
            }
            else if (other is StaticMesh staticMesh)
            {
                otherGo = staticMesh.Tag as GameObject;
            }

            if (otherGo != null)
            {
                bool isTriggerCollision = _isTrigger ||
                                          other.CollisionRules.Personal == CollisionRule.NoSolver;

                if (isTriggerCollision)
                {
                    if (otherEntity != null)
                        _currentTriggerContacts.Remove(otherEntity);

                    onTriggerExit?.Invoke(otherGo);
                    return;
                }

                if (otherEntity != null)
                    _currentCollisionContacts.Remove(otherEntity);

                onCollisionExit?.Invoke(otherGo);
            }
        }

        private void HandleCollisionEnter(GameObject other)
        {
            if (!_predictionManager.isSimulating)
                return;

            for (int i = 0; i < _collisionEnterHandlers.Length; i++)
                _collisionEnterHandlers[i].OnBepuCollisionEnter(other);
        }

        private void HandleCollisionExit(GameObject other)
        {
            if (!_predictionManager.isSimulating)
                return;

            for (int i = 0; i < _collisionExitHandlers.Length; i++)
                _collisionExitHandlers[i].OnBepuCollisionExit(other);
        }

        private void HandleTriggerEnter(GameObject other)
        {
            if (!_predictionManager.isSimulating)
                return;

            for (int i = 0; i < _triggerEnterHandlers.Length; i++)
                _triggerEnterHandlers[i].OnBepuTriggerEnter(other);
        }

        private void HandleTriggerExit(GameObject other)
        {
            if (!_predictionManager.isSimulating)
                return;

            for (int i = 0; i < _triggerExitHandlers.Length; i++)
                _triggerExitHandlers[i].OnBepuTriggerExit(other);
        }
    }
}
