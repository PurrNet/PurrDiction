using PurrNet.Packing;
using PurrNet.Pooling;
using UnityEngine;

namespace PurrNet.Prediction.Tests
{
    [RegisterNetworkType(typeof(DisposableList<int>))]
    public static class TestDeltaPacking
    {
        [RuntimeInitializeOnLoadMethod]
        static void TestWasdInput()
        {
            var old = new DisposableList<int>(1);
            var @new = new DisposableList<int>(5);
            old.Dispose();

            @new.Add(1);
            @new.Add(5);

            using var packer = BitPackerPool.Get();

            DeltaPacker<DisposableList<int>>.Write(packer, old, @new);

            packer.ResetPositionAndMode(true);

            DisposableList<int> newResult = default;
            DeltaPacker<DisposableList<int>>.Read(packer, old, ref newResult);

            if (!Packer.AreEqual(@new, newResult))
            {
                Debug.LogError($"Old:\n{@new.isDisposed}\nResult:\n{newResult.isDisposed}");

                if (!@new.isDisposed)
                {
                    Debug.LogError($"Old Count: {@new.Count}");
                    for (int i = 0; i < @new.Count; i++)
                    {
                        Debug.LogError($"Old[{i}]: {@new[i]}");
                    }
                }

                if (!newResult.isDisposed)
                {
                    Debug.LogError($"Result Count: {newResult.Count}");
                    for (int i = 0; i < newResult.Count; i++)
                    {
                        Debug.LogError($"Result[{i}]: {newResult[i]}");
                    }
                }
            }
        }
    }
}
