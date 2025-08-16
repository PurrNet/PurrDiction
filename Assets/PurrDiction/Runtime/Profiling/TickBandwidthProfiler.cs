using System;
using System.Collections.Generic;

namespace PurrNet.Prediction.Profiler
{
    public static class TickBandwidthProfiler
    {
        static readonly List<PackingInfo> _wroteStates = new List<PackingInfo>(256);
        static readonly List<PackingInfo> _readStates = new List<PackingInfo>(256);
        static readonly List<PackingInfo> _wroteInputs = new List<PackingInfo>(256);
        static readonly List<PackingInfo> _readInputs = new List<PackingInfo>(256);

        public static IReadOnlyList<PackingInfo> wroteStates => _wroteStates;
        public static IReadOnlyList<PackingInfo> readStates => _readStates;
        public static IReadOnlyList<PackingInfo> wroteInputs => _wroteInputs;
        public static IReadOnlyList<PackingInfo> readInputs => _readInputs;

        public static event Action onTickEnded;

        public static void OnWroteState(Type parent, int bitCount, UnityEngine.Object reference)
        {
            _wroteStates.Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        public static void OnReadState(Type parent, int bitCount, UnityEngine.Object reference)
        {
            _readStates.Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        public static void OnWroteInput(Type parent, int bitCount, UnityEngine.Object reference)
        {
            _wroteInputs.Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        public static void OnReadInput(Type parent, int bitCount, UnityEngine.Object reference)
        {
            _readInputs.Add(new PackingInfo { parent = parent, bitCount = bitCount, reference = reference });
        }

        public static void MarkEndOfTick()
        {
            onTickEnded?.Invoke();

            _wroteStates.Clear();
            _readStates.Clear();
            _wroteInputs.Clear();
            _readInputs.Clear();
        }
    }
}
