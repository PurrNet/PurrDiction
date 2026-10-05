using System;
using System.Collections;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Transports;

namespace PurrNet.Prediction.Tests.Editor
{
    /// <summary>
    /// Exercises the actual compiled transport dependency, without opening a socket.
    /// A passing loss control is required before network recovery profiles can trust their fault injection.
    /// </summary>
    public sealed class TransportSimulationDeliveryTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Test]
        public void FullReceiveLossWithLatencyNeverEnqueuesAndRecyclesPacket()
            => CheckReceiveSimulation(true);

        [Test]
        public void LatencyWithoutLossKeepsPacketOutOfThePoolWhileQueued()
            => CheckReceiveSimulation(false);

        private static void CheckReceiveSimulation(bool dropAll)
        {
            // Resolve through the SDK transport so the test uses exactly the dependency it runs with.
            var managerType = typeof(UDPTransport).GetField("_client", Fields)?.FieldType;
            Assert.That(managerType, Is.Not.Null, "UDP transport client manager type is unavailable.");
            var listenerType = managerType.Assembly.GetType("LiteNetLib.EventBasedNetListener", true);
            object listener = Activator.CreateInstance(listenerType);
            object manager = Activator.CreateInstance(managerType, new[] { listener, null });
            var transportType = managerType.BaseType;
            Assert.That(transportType?.FullName, Is.EqualTo("LiteNetLib.LiteNetManager"));
            var getPacket = transportType.GetMethod("PoolGetPacket", Fields);
            var recycle = transportType.GetMethod("PoolRecycle", Fields);
            var receive = transportType.GetMethod("OnMessageReceived", Fields);
            var queue = (IList)transportType.GetField("_pingSimulationList", Fields).GetValue(manager);
            var poolHead = transportType.GetField("_poolHead", Fields);
            var poolCount = transportType.GetProperty("PoolCount", Fields);
            object packet = getPacket.Invoke(manager, new object[] { 16 });
            byte[] bytes = (byte[])packet.GetType().GetField("RawData", Fields).GetValue(packet);
            bytes[0] = 0xC0; // Recycling must clear the header flags on this exact pooled object.
            try
            {
                transportType.GetField("SimulateLatency", Fields).SetValue(manager, true);
                transportType.GetField("SimulationMinLatency", Fields).SetValue(manager, 100);
                transportType.GetField("SimulationMaxLatency", Fields).SetValue(manager, 100);
                transportType.GetField("SimulatePacketLoss", Fields).SetValue(manager, dropAll);
                transportType.GetField("SimulationPacketLossChance", Fields).SetValue(manager, 100);
                Assert.That(queue.Count, Is.Zero);
                Assert.That(poolCount.GetValue(manager), Is.EqualTo(0));

                receive.Invoke(manager, new object[] { packet, new IPEndPoint(IPAddress.Loopback, 29123) });

                if (dropAll)
                {
                    Assert.That(queue.Count, Is.Zero,
                        "A packet rejected by loss simulation must never survive in the latency queue.");
                    Assert.That(poolCount.GetValue(manager), Is.EqualTo(1));
                    Assert.That(poolHead.GetValue(manager), Is.SameAs(packet));
                    Assert.That(bytes[0], Is.Zero, "The rejected packet must actually be recycled.");
                    object reused = getPacket.Invoke(manager, new object[] { 16 });
                    Assert.That(reused, Is.SameAs(packet));
                    Assert.That(poolCount.GetValue(manager), Is.EqualTo(0));
                }
                else
                {
                    Assert.That(queue.Count, Is.EqualTo(1),
                        "The no-loss control proves latency simulation is compiled in and active.");
                    object queued = queue[0];
                    Assert.That(queued.GetType().GetField("Data", Fields).GetValue(queued), Is.SameAs(packet));
                    Assert.That(poolCount.GetValue(manager), Is.EqualTo(0),
                        "Latency owns its queued packet until dispatch; it must not also be in the pool.");
                }
            }
            finally
            {
                // This manager never started networking; release the test-owned packet and wait handle.
                queue.Clear();
                if (!ReferenceEquals(poolHead.GetValue(manager), packet))
                    recycle.Invoke(manager, new[] { packet });
                (transportType.GetField("_updateTriggerEvent", Fields).GetValue(manager) as IDisposable)?.Dispose();
            }
        }
    }
}
