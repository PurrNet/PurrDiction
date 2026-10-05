using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Tests.Editor
{
    // Unity reports contacts for every script class that declares a collision message, so the
    // rigidbodies must declare none themselves and attach proxies only for enabled event kinds.
    public sealed class PredictedRigidbodyEventProxyTests
    {
        private static readonly string[] Messages3D =
            { "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay", "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay" };

        private static readonly string[] Messages2D =
            { "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D", "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D" };

        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

        [Test]
        public void DefaultMaskExcludesStayEvents()
        {
            const PhysicsEventMask expected = PhysicsEventMask.CollisionEnter | PhysicsEventMask.CollisionExit |
                                              PhysicsEventMask.TriggerEnter | PhysicsEventMask.TriggerExit;
            Assert.That(PredictedRigidbody.DEFAULT_EVENT_MASK, Is.EqualTo(expected));
        }

#if UNITY_PHYSICS_3D
        [Test]
        public void RigidbodyDeclaresNoUnityCollisionMessages()
        {
            foreach (var name in Messages3D)
                Assert.That(typeof(PredictedRigidbody).GetMethod(name, Declared), Is.Null, name);
        }

        [Test]
        public void ProxiesFollowTheEventMask()
        {
            var go = new GameObject("Proxy test body");
            try
            {
                go.AddComponent<Rigidbody>();
                var body = go.AddComponent<PredictedRigidbody>();
                Assert.That(body.eventMask, Is.EqualTo(PredictedRigidbody.DEFAULT_EVENT_MASK));
                Assert.That(go.GetComponent<PredictedRigidbodyContactProxy>(), Is.Null,
                    "proxies attach at setup or on a mask change, not on component creation");

                body.SyncEventProxies();
                var contact = go.GetComponent<PredictedRigidbodyContactProxy>();
                Assert.That(contact, Is.Not.Null);
                Assert.That(contact.target, Is.SameAs(body));
                Assert.That(contact.hideFlags.HasFlag(HideFlags.HideInInspector), Is.True);
                Assert.That(go.GetComponent<PredictedRigidbodyStayProxy>(), Is.Null,
                    "Enter/Exit-only bodies must not pay for per-step Stay reporting");

                body.eventMask |= PhysicsEventMask.CollisionStay;
                var stay = go.GetComponent<PredictedRigidbodyStayProxy>();
                Assert.That(stay, Is.Not.Null);
                Assert.That(stay.target, Is.SameAs(body));

                body.eventMask = PhysicsEventMask.TriggerStay;
                Assert.That(go.GetComponent<PredictedRigidbodyContactProxy>(), Is.Null);
                Assert.That(go.GetComponent<PredictedRigidbodyStayProxy>(), Is.SameAs(stay));

                body.eventMask = PhysicsEventMask.None;
                Assert.That(go.GetComponent<PredictedRigidbodyContactProxy>(), Is.Null);
                Assert.That(go.GetComponent<PredictedRigidbodyStayProxy>(), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
#endif

#if UNITY_PHYSICS_2D
        [Test]
        public void Rigidbody2DDeclaresNoUnityCollisionMessages()
        {
            foreach (var name in Messages2D)
                Assert.That(typeof(PredictedRigidbody2D).GetMethod(name, Declared), Is.Null, name);
        }

        [Test]
        public void Proxies2DFollowTheEventMask()
        {
            var go = new GameObject("Proxy test body 2D");
            try
            {
                go.AddComponent<Rigidbody2D>();
                var body = go.AddComponent<PredictedRigidbody2D>();
                Assert.That(body.eventMask, Is.EqualTo(PredictedRigidbody.DEFAULT_EVENT_MASK));

                body.SyncEventProxies();
                Assert.That(go.GetComponent<PredictedRigidbody2DContactProxy>(), Is.Not.Null);
                Assert.That(go.GetComponent<PredictedRigidbody2DStayProxy>(), Is.Null);

                body.eventMask |= PhysicsEventMask.TriggerStay;
                Assert.That(go.GetComponent<PredictedRigidbody2DStayProxy>(), Is.Not.Null);

                body.eventMask = PhysicsEventMask.None;
                Assert.That(go.GetComponent<PredictedRigidbody2DContactProxy>(), Is.Null);
                Assert.That(go.GetComponent<PredictedRigidbody2DStayProxy>(), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
#endif
    }
}
