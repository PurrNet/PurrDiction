using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PurrNet.Transports;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class RpcDeliveryMatrixTests
    {
        private const BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void DesyncHealNotificationStaysOnTheDefaultReliableChannel()
        {
            var rpc = FindSingleRpc("NotifyDesyncToClient", typeof(TargetRpcAttribute));
            Assert.That(ChannelOf(rpc), Is.EqualTo(Channel.ReliableOrdered),
                "NotifyDesyncToClient must keep default reliable delivery; " +
                "a heal notification lost on an unreliable channel strands the desynced client");
        }

        [Test]
        public void InputUploadIsAnUnreliableImmediateServerRpc()
        {
            var rpc = FindSingleRpc("SendInputToServer", typeof(ServerRpcAttribute));
            Assert.That(ChannelOf(rpc), Is.EqualTo(Channel.Unreliable));
            Assert.That(ImmediateOf(rpc), Is.True);
        }

        [Test]
        public void FragmentedInputUploadIsAnUnreliableImmediateServerRpc()
        {
            var rpc = FindSingleRpc("SendInputToServerFragmented", typeof(ServerRpcAttribute));
            Assert.That(ChannelOf(rpc), Is.EqualTo(Channel.Unreliable));
            Assert.That(ImmediateOf(rpc), Is.True);
        }

        [Test]
        public void FramesShareOneOrderedReliableTargetRpc()
        {
            var rpc = FindSingleRpc("SendFrameToRemote", typeof(TargetRpcAttribute));
            Assert.That(ChannelOf(rpc), Is.EqualTo(Channel.ReliableOrdered),
                "every frame is a delta against the one before it, so none may be lost or reordered");
            Assert.That(ImmediateOf(rpc), Is.False,
                "an immediate lane is flushed independently and could overtake a batched frame");
        }

        [Test]
        public void MissingBaselineRequestUsesReliableDelivery()
        {
            var rpc = FindSingleRpc("RequestHistoryResync", typeof(ServerRpcAttribute));
            Assert.That(ChannelOf(rpc), Is.EqualTo(Channel.ReliableOrdered));
        }

        [Test]
        public void DesyncReportUploadIsAnUnreliableServerRpc()
        {
            var rpc = FindSingleRpc("SendDesyncReportToServer", typeof(ServerRpcAttribute));
            Assert.That(ChannelOf(rpc), Is.EqualTo(Channel.Unreliable));
        }

        // The RPC codegen renames the authored method and re-emits a publicized
        // replacement under the original name carrying the attribute blob, so the
        // lookup must scan every visibility and match by name.
        private static CustomAttributeData FindSingleRpc(string methodName, Type attributeType)
        {
            var matches = new List<CustomAttributeData>();
            foreach (var method in typeof(PredictionManager).GetMethods(AllInstance))
            {
                if (method.Name != methodName)
                    continue;

                foreach (var data in method.GetCustomAttributesData())
                {
                    if (data.AttributeType == attributeType)
                        matches.Add(data);
                }
            }

            Assert.That(matches, Has.Count.EqualTo(1),
                $"Expected exactly one [{attributeType.Name}] on PredictionManager.{methodName}");
            return matches[0];
        }

        private static object ConstructorArgument(CustomAttributeData rpc, string parameterName)
        {
            var parameters = rpc.Constructor.GetParameters();
            Assert.That(rpc.ConstructorArguments.Count, Is.EqualTo(parameters.Length),
                $"[{rpc.AttributeType.Name}] attribute data does not cover its full constructor signature");
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == parameterName)
                    return rpc.ConstructorArguments[i].Value;
            }

            Assert.Fail($"[{rpc.AttributeType.Name}] has no constructor parameter named '{parameterName}'");
            return null;
        }

        private static Channel ChannelOf(CustomAttributeData rpc)
        {
            return (Channel)Convert.ToInt32(ConstructorArgument(rpc, "channel"));
        }

        private static bool ImmediateOf(CustomAttributeData rpc)
        {
            return (bool)ConstructorArgument(rpc, "immediate");
        }
    }
}
