using BEPUphysics.CollisionRuleManagement;
using PurrNet.Logging;

namespace PurrNet.Prediction
{
    public partial class BepuRigidbody
    {
        public event BepuCollisionHandler.TriggerEventHandler onTriggerEnter;
        public event BepuCollisionHandler.TriggerEventHandler onTriggerExit;
        public event BepuCollisionHandler.CollisionEventHandler onCollisionEnter;
        public event BepuCollisionHandler.CollisionEventHandler onCollisionExit;
        private BepuCollisionHandler _collisionHandler;
        
        public bool isTrigger => _isTrigger;

        protected override void OnDestroy()
        {
            base.OnDestroy();
            
            if(_collisionHandler != null)
                _collisionHandler.UnsubscribeFromEvents(_entity.CollisionInformation);
        }

        private void InitializeCollisionHandler(PredictionManager world)
        {
            if (world == null)
            {
                PurrLogger.LogError($"Can't initialize collision handler without a prediction manager!", this);
                return;
            }
            _collisionHandler = new BepuCollisionHandler(world, _isTrigger, gameObject);
            _collisionHandler.onTriggerEnter += (go) => onTriggerEnter?.Invoke(go);
            _collisionHandler.onTriggerExit += (go) => onTriggerExit?.Invoke(go);
            _collisionHandler.onCollisionEnter += (go) => onCollisionEnter?.Invoke(go);
            _collisionHandler.onCollisionExit += (go) => onCollisionExit?.Invoke(go);
        }

        private void UpdateTriggerState()
        {
            if (_entity == null || _collisionHandler == null) return;
    
            var collidable = _entity.CollisionInformation;
            _collisionHandler.SubscribeToEvents(collidable);
    
            _entity.CollisionInformation.CollisionRules.Personal = _isTrigger ? 
                CollisionRule.NoSolver : 
                CollisionRule.Normal;
        }
    }
}