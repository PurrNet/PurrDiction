using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    public static class TestDeltaPacking
    {
        [RuntimeInitializeOnLoadMethod]
        static void TestWasdInput()
        {


            var old = new SimpleWASDInput
            {
                horizontal = -0.7070313f,
                vertical = -0.7070313f,
                jump = false,
                dash = true
            };

            var @new = new SimpleWASDInput
            {
                horizontal = -0.7070313f,
                vertical = -0.7070313f,
                jump = false,
                dash = false
            };

            using var packer = BitPackerPool.Get();

            DeltaPacker<SimpleWASDInput>.Write(packer, old, @new);

            packer.ResetPositionAndMode(true);

            SimpleWASDInput newResult = default;
            DeltaPacker<SimpleWASDInput>.Read(packer, old, ref newResult);

            if (!newResult.Equals(@new))
            {
                Debug.LogError($"New:\n{@new}\nResult:\n{newResult}");
            }
        }
    }
}
