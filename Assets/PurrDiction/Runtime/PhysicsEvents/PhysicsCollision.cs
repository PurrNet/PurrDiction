using System;
using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction
{
    public struct PhysicsCollision : IDisposable, IDuplicate<PhysicsCollision>
    {
#if UNITY_PHYSICS_3D
        public DisposableList<PhysicsContactPoint> contacts;
        public Vector3 impulse;
        public Vector3 relativeVelocity;

        public void Dispose() => contacts.Dispose();

        public PhysicsCollision Duplicate()
        {
            return new PhysicsCollision
            {
                contacts = contacts.Duplicate(),
                impulse = impulse,
                relativeVelocity = relativeVelocity
            };
        }

        public override string ToString()
        {
            var contactsStr = contacts.isDisposed ? "<disposed>" : contacts.ToString();
            return $"{{impulse={impulse}, relVel={relativeVelocity}, contacts={contactsStr}}}";
        }
#else
        public void Dispose() {}

        public PhysicsCollision Duplicate() => default;
#endif
    }
}