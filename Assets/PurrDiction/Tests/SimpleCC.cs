using FixMath.NET;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public class SimpleCC : PredictedIdentity<SimpleWASDInput, SimpleCCState>
    {
        [SerializeField] private GameObject _projectile;
        [SerializeField] private CharacterController _controller;
        [SerializeField] private float _speed = 5;
        
        static readonly Collider[] _cache = new Collider[50];

        protected override void Simulate(SimpleWASDInput? input, ref SimpleCCState state, Fix64 delta)
        {
            var move = new Vector3(input?.horizontal ?? 0, 0, input?.vertical ?? 0);
            
            if (move.magnitude > 0)
                move.Normalize();
            
            var moveVector = move * _speed * (float)delta;
            
            if (move != Vector3.zero)
                transform.rotation = Quaternion.LookRotation(move);
            
            _controller.Move(moveVector);
            _controller.Move(Physics.gravity * (float)delta);
            
            var hitNum = Physics.OverlapSphereNonAlloc(transform.position, 4f, _cache);
            
            for (var i = 0; i < hitNum; i++)
            {
                var hit = _cache[i];
                var rb = hit.attachedRigidbody;
                
                if (rb)
                    rb.WakeUp();
            }

            if (state.wasShooting != input?.jump)
            {
                state.wasShooting = input?.jump ?? false;

                if (state.wasShooting)
                    Shoot();
            }
        }
        
        private void Shoot()
        {
            var projectileId = hierarchy.Create(_projectile);
            var projectileRb = hierarchy.GetComponent<Rigidbody>(projectileId);
            
            projectileRb.position = transform.position + transform.forward;
            projectileRb.rotation = transform.rotation;
            projectileRb.linearVelocity = transform.forward * 10;
        }

        protected override SimpleWASDInput GetInput()
        {
            return new SimpleWASDInput
            {
                horizontal = Input.GetAxisRaw("Horizontal"),
                vertical = Input.GetAxisRaw("Vertical"),
                jump = Input.GetKey(KeyCode.Space),
                dash = Input.GetKey(KeyCode.LeftShift)
            };
        }
    }
}
