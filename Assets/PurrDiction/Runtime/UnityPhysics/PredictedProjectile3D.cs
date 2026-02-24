using UnityEngine;

namespace PurrNet.Prediction
{
    public class PredictedProjectile3D : PredictedIdentity<ProjectileState3D>
    {
        [Tooltip("The gravity of the projectile.")]
        [SerializeField] private float _gravity = 0;

        public float gravity
        {
            get { return currentState.gravity; }
            set { currentState.gravity = value; }
        }
        
        [Tooltip("This is used to mimic similar behaviour to rigidbody physics material on bounce. Not supported to change at runtime")]
        [SerializeField] private PhysicsMaterial _physicsMaterial;
        
        [Tooltip("Radius of the spherical projectile shape.")]
        [SerializeField] private float _radius;

        public float radius
        {
            get { return currentState.radius; }
            set { currentState.radius = value; }
        }

        [Tooltip("Whether it acts as a trigger or collision.")]
        [SerializeField] private bool _isTrigger;

        public bool isTrigger
        {
            get { return currentState.isTrigger; }
            set { currentState.isTrigger = value; }
        }

        public event OnCollisionDelegate onCollisionEnter;
        public event OnCollisionDelegate onCollisionExit;
        public event OnCollisionDelegate onCollisionStay;

        public event OnTriggerDelegate onTriggerEnter;
        public event OnTriggerDelegate onTriggerExit;
        public event OnTriggerDelegate onTriggerStay;

        protected override ProjectileState3D GetInitialState()
        {
            return new ProjectileState3D()
            {
                gravity = _gravity,
                isTrigger = _isTrigger,
                radius = _radius
            };
        }

        protected override void Simulate(ref ProjectileState3D state, float delta)
        {
            
        }

        public void AddImpulse(Vector3 impulse)
        {
            
        }
    }
}
