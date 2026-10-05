using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using PurrNet.Packing;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    /// <summary>Opt-in microbenchmark of the actual deterministic identity entry points.</summary>
    public static class DeterministicMetadataBenchmarkRunner
    {
        private const int Window = 600;
        private const int Lead = 8;
        private static object _allocationSink;

        private enum Operation
        {
            SaveForward, SaveReplay,
            ReadUnchangedLatest, ReadUnchangedHistorical,
            ReadStateUnchangedLatest, ReadStateUnchangedHistorical,
            ReadStateChangedLatest, ReadStateChangedHistorical
        }

        [MenuItem("Tools/PurrDiction/Analysis/Run Deterministic Metadata Benchmarks", false, -87)]
        public static void RunFromMenu() => Run();

        public static void RunFromCommandLine()
        {
            try { Run(); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        public static Report Run()
        {
            int iterations = IntegerArgument("-deterministicBenchmarkIterations", 8192, 1024, 100000);
            int samples = IntegerArgument("-deterministicBenchmarkSamples", 7, 3, 31);
            string output = Path.GetFullPath(Argument("-deterministicBenchmarkOutput") ?? "Temp/DeterministicMetadataBenchmarks");
            Directory.CreateDirectory(output);
            NetworkManager.CallAllRegisters();
            var originalCopy = PurrCopy<DeterministicMetadataBenchmarkCollection>.Copy;
            var originalComparer = PurrEquality<DeterministicMetadataBenchmarkCollection>.Default;
            var report = new Report
            {
                utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                iterations = iterations, samples = samples, retainedTickWindow = Window,
                historicalLead = Lead, stopwatchFrequency = Stopwatch.Frequency,
                allocationCounterAvailable = CalibrateAllocationCounter(out long allocationDelta),
                allocationCalibrationBytes = allocationDelta,
                notes = new[]
                {
                    "Actual DeterministicIdentity methods are called; reflection is restricted to fixture setup.",
                    "Time is elapsed Stopwatch duration, not OS CPU utilization. Every call has an individual timestamp bracket; its measured empty bracket is reported and not subtracted.",
                    "Seeding, forward prerequisites, payload construction, rollback/ClearFuture setup, assertions, disposal and report writing are outside each measured operation.",
                    "The bounded history retains an exact snapshot for every tick in its normal 600-tick window. Metadata calls never substitute equality compression for deterministic saves.",
                    "Latest cases model manager ClearFuture before metadata application. Historical cases retain eight future ticks and exercise the separate exact historical replacement path.",
                    "The disposable collection owns an int array, uses IDuplicate for a deep copy, and a typed contents comparer. The benchmark restores both copy/comparer delegates on exit.",
                    "Unavailable allocation counters are reported as -1, never interpreted as allocation-free. Duplicate/dispose/element counts remain observed independently.",
                    "Editor microbenchmarks do not establish whole-game FPS or networking/physics costs. SaveReplay times save calls after a real rollback, not the rollback itself."
                }
            };
            try
            {
                PurrCopy<DeterministicMetadataBenchmarkCollection>.Copy =
                    (in DeterministicMetadataBenchmarkCollection state) => state.Duplicate();
                // Avoid the unrelated generic reference-null boxing behavior in older SDKs.
                // Only this benchmark's private fixture type is affected, and it is restored below.
                PurrEquality<DeterministicMetadataBenchmarkCollection>.Default = new CollectionComparer();
                report.emptyBracketMedianNanoseconds = MeasureEmptyBracket(iterations, samples);
                Benchmark<DeterministicMetadataBenchmarkSmall, DeterministicMetadataBenchmarkSmallProbe>(report,
                    "small-blittable", 0, () => new DeterministicMetadataBenchmarkSmall { a = 7, b = 19, c = 31, d = 47 },
                    state => state.a == 7 && state.b == 19 && state.c == 31 && state.d == 47);
                foreach (int count in new[] { 64, 512 })
                    Benchmark<DeterministicMetadataBenchmarkCollection, DeterministicMetadataBenchmarkCollectionProbe>(report,
                        "disposable-int-array-" + count, count, () => CreateCollection(count), state => ValidCollection(state, count));
            }
            finally
            {
                PurrCopy<DeterministicMetadataBenchmarkCollection>.Copy = originalCopy;
                PurrEquality<DeterministicMetadataBenchmarkCollection>.Default = originalComparer;
                report.fixtureRegistrationsRestored = ReferenceEquals(PurrCopy<DeterministicMetadataBenchmarkCollection>.Copy, originalCopy)
                    && ReferenceEquals(PurrEquality<DeterministicMetadataBenchmarkCollection>.Default, originalComparer);
                File.WriteAllText(Path.Combine(output, "deterministic-metadata-benchmarks.json"), JsonUtility.ToJson(report, true));
            }
            Debug.Log("Deterministic metadata benchmark complete: " + output);
            return report;
        }

        private static void Benchmark<T, TProbe>(Report report, string name, int elements,
            Func<T> create, Func<T, bool> valid)
            where T : struct, IPredictedData<T>
            where TProbe : DeterministicIdentity<T>
        {
            foreach (Operation operation in Enum.GetValues(typeof(Operation)))
            {
                // Two independent warmup fixtures prime registration/JIT and both ring wrapping and age pruning.
                for (int warmup = 0; warmup < 2; warmup++)
                    Measure<T, TProbe>(operation, 1024, report.allocationCounterAvailable, create, valid);
                var result = new Result
                {
                    state = name, operation = operation.ToString(), stateElementCount = elements,
                    containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
                    copyDelegate = PurrCopy<T>.Copy.Method.ToString()
                };
                for (int sample = 0; sample < report.samples; sample++)
                    result.runs.Add(Measure<T, TProbe>(operation, report.iterations, report.allocationCounterAvailable, create, valid));
                result.medianNanosecondsPerCall = Median(result.runs.ConvertAll(x => x.nanosecondsPerCall));
                result.minimumNanosecondsPerCall = Min(result.runs.ConvertAll(x => x.nanosecondsPerCall));
                result.maximumNanosecondsPerCall = Max(result.runs.ConvertAll(x => x.nanosecondsPerCall));
                result.medianAllocatedBytesPerCall = report.allocationCounterAvailable
                    ? Median(result.runs.ConvertAll(x => x.allocatedBytes / (double)x.measuredCalls)) : -1;
                report.results.Add(result);
                Debug.Log($"Deterministic metadata: {name} {operation}: {result.medianNanosecondsPerCall:F1} ns/call; {result.medianAllocatedBytesPerCall:F1} B/call");
            }
        }

        private static Sample Measure<T, TProbe>(Operation operation, int iterations, bool allocatedCounter,
            Func<T> create, Func<T, bool> valid)
            where T : struct, IPredictedData<T>
            where TProbe : DeterministicIdentity<T>
        {
            using var fixture = new Fixture<T, TProbe>(create);
            bool save = operation == Operation.SaveForward || operation == Operation.SaveReplay;
            bool historical = operation.ToString().EndsWith("Historical", StringComparison.Ordinal);
            bool changed = operation == Operation.ReadStateChangedLatest || operation == Operation.ReadStateChangedHistorical;
            bool readUnchanged = operation == Operation.ReadUnchangedLatest || operation == Operation.ReadUnchangedHistorical;
            var sample = new Sample { measuredCalls = iterations, allocatedBytes = allocatedCounter ? 0 : -1 };
            ulong latestSeed = 0, lastTick = 0;
            long elapsed = 0;
            // Collection/heap cleanup is excluded from samples, not forced during a measured call.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            for (int i = 0; i < iterations; i++)
            {
                ulong tick = (ulong)i + 1;
                lastTick = tick;
                var previousMetadata = changed && (i & 1) == 1 ? fixture.metadataB : fixture.metadataA;
                var expectedMetadata = changed && (i & 1) == 0 ? fixture.metadataB : fixture.metadataA;
                fixture.identity.fullPredictedState.prediction = previousMetadata;
                if (operation == Operation.SaveReplay && i % 64 == 0)
                {
                    // Real speculative future, real ClearFuture and Rollback, then 64 sequential saves.
                    ulong lastFuture = Math.Min((ulong)iterations, tick + 63);
                    for (ulong future = tick; future <= lastFuture; future++) fixture.identity.SaveStateInHistory(future);
                    fixture.identity.ClearFuture(tick - 1);
                    fixture.identity.Rollback(tick - 1);
                    sample.rollbackSetups++;
                }
                if (!save)
                {
                    ulong target = tick + (historical ? (ulong)Lead : 0);
                    while (latestSeed < target) fixture.identity.SaveStateInHistory(++latestSeed);
                }
                var payload = changed ? ((i & 1) == 0 ? fixture.aToB : fixture.bToA) : fixture.unchanged;
                payload.ResetPositionAndMode(true);
                var before = DeterministicMetadataBenchmarkCounters.Read();
                long allocationBefore = allocatedCounter ? GC.GetAllocatedBytesForCurrentThread() : 0;
                long started = Stopwatch.GetTimestamp();
                if (save) fixture.identity.SaveStateInHistory(tick);
                else if (readUnchanged) fixture.identity.ReadUnchangedState(tick, tick - 1, tick);
                else fixture.identity.ReadState(tick, payload, tick - 1, tick);
                elapsed += Stopwatch.GetTimestamp() - started;
                if (allocatedCounter) sample.allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
                sample.counters.Add(DeterministicMetadataBenchmarkCounters.Read() - before);
                if (!fixture.history.Read(tick, out var state) || !valid(state.state))
                    throw new InvalidOperationException("Measured operation lost or corrupted its exact tick.");
                var expected = save ? fixture.identity.fullPredictedState.prediction : expectedMetadata;
                if (!Packer.AreEqualRef(ref state.prediction, ref expected))
                    throw new InvalidOperationException("Metadata does not match the applied baseline/delta.");
                if (save) sample.saveCalls++; else if (readUnchanged) sample.readUnchangedCalls++; else sample.readStateCalls++;
            }
            sample.nanosecondsPerCall = elapsed * 1e9 / Stopwatch.Frequency / iterations;
            ulong last = save ? lastTick : latestSeed;
            ulong first = last > Window ? last - Window : 0;
            for (ulong tick = first; tick <= last; tick++)
            {
                if (!fixture.history.Read(tick, out var state) || !valid(state.state))
                    throw new InvalidOperationException("Expected every retained tick, including unchanged snapshots.");
                sample.exactTicksValidated++;
            }
            sample.historyCount = fixture.history.Count;
            sample.firstRetainedTick = fixture.history.GetEntryTick(0);
            sample.lastRetainedTick = fixture.history.MostRecentTick;
            sample.metadataCount = fixture.metadata.Count;
            return sample;
        }

        private sealed class Fixture<T, TProbe> : IDisposable
            where T : struct, IPredictedData<T>
            where TProbe : DeterministicIdentity<T>
        {
            private readonly GameObject _object;
            public readonly TProbe identity;
            public readonly History<FULL_STATE<T>> history = new(Window);
            public readonly History<PredictedIdentityState> metadata = new(Window);
            public readonly PredictedIdentityState metadataA = new() { wasOnSimulationStartCalled = true };
            public readonly PredictedIdentityState metadataB = new() { wasOnSimulationStartCalled = false };
            public readonly BitPacker unchanged = BitPackerPool.Get();
            public readonly BitPacker aToB = BitPackerPool.Get();
            public readonly BitPacker bToA = BitPackerPool.Get();

            public Fixture(Func<T> create)
            {
                _object = new GameObject("Deterministic metadata benchmark") { hideFlags = HideFlags.HideAndDontSave };
                identity = _object.AddComponent<TProbe>();
                SetField(typeof(DeterministicIdentity<T>), identity, "_stateHistory", history);
                SetField(typeof(PredictedIdentity), identity, "_metadataVerified", metadata);
                identity.fullPredictedState = new FULL_STATE<T> { state = create(), prediction = metadataA };
                history.Write(0, identity.fullPredictedState.DeepCopy());
                metadata.Write(0, metadataA);
                Packer<bool>.Write(unchanged, false);
                Packer<bool>.Write(aToB, true);
                DeltaPacker<PredictedIdentityState>.Write(aToB, metadataA, metadataB);
                Packer<bool>.Write(bToA, true);
                DeltaPacker<PredictedIdentityState>.Write(bToA, metadataB, metadataA);
            }

            public void Dispose()
            {
                unchanged.Dispose(); aToB.Dispose(); bToA.Dispose();
                // Seeded Editor probes do not reliably receive Unity destruction callbacks.
                identity.ReleasePredictionStateForPool();
                Object.DestroyImmediate(_object);
                metadata.Clear();
            }
        }

        private sealed class CollectionComparer : IEqualityComparer<DeterministicMetadataBenchmarkCollection>
        {
            public bool Equals(DeterministicMetadataBenchmarkCollection x, DeterministicMetadataBenchmarkCollection y) => x.Equals(y);
            public int GetHashCode(DeterministicMetadataBenchmarkCollection value) => value.values?.Length ?? 0;
        }

        private static DeterministicMetadataBenchmarkCollection CreateCollection(int count)
        {
            var result = new DeterministicMetadataBenchmarkCollection { values = new int[count] };
            for (int i = 0; i < count; i++) result.values[i] = i * 17 + 3;
            return result;
        }

        private static bool ValidCollection(DeterministicMetadataBenchmarkCollection state, int count)
        {
            if (state.values == null || state.values.Length != count) return false;
            for (int i = 0; i < count; i++) if (state.values[i] != i * 17 + 3) return false;
            return true;
        }

        private static bool CalibrateAllocationCounter(out long allocated)
        {
            _allocationSink = new byte[128];
            long before = GC.GetAllocatedBytesForCurrentThread();
            _allocationSink = new byte[8192];
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(_allocationSink);
            _allocationSink = null;
            return allocated >= 8192;
        }

        private static double MeasureEmptyBracket(int iterations, int samples)
        {
            var values = new List<double>();
            for (int sample = 0; sample < samples; sample++)
            {
                long elapsed = 0;
                for (int i = 0; i < iterations; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    elapsed += Stopwatch.GetTimestamp() - start;
                }
                values.Add(elapsed * 1e9 / Stopwatch.Frequency / iterations);
            }
            return Median(values);
        }

        private static void SetField(Type type, object target, string name, object value)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            field.SetValue(target, value);
        }
        private static double Median(List<double> values) { values.Sort(); return values[values.Count / 2]; }
        private static double Min(List<double> values) { values.Sort(); return values[0]; }
        private static double Max(List<double> values) { values.Sort(); return values[values.Count - 1]; }
        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
        private static int IntegerArgument(string name, int fallback, int minimum, int maximum)
        {
            int value = Argument(name) is string raw ? int.Parse(raw) : fallback;
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
            return value;
        }

        [Serializable] public sealed class Report
        {
            public string utc, unityVersion;
            public int iterations, samples, retainedTickWindow, historicalLead;
            public long stopwatchFrequency, allocationCalibrationBytes;
            public bool allocationCounterAvailable, fixtureRegistrationsRestored;
            public double emptyBracketMedianNanoseconds;
            public string[] notes;
            public List<Result> results = new();
        }
        [Serializable] public sealed class Result
        {
            public string state, operation, copyDelegate;
            public int stateElementCount;
            public bool containsReferences;
            public double medianNanosecondsPerCall, minimumNanosecondsPerCall, maximumNanosecondsPerCall, medianAllocatedBytesPerCall;
            public List<Sample> runs = new();
        }
        [Serializable] public sealed class Sample
        {
            public int measuredCalls, saveCalls, readStateCalls, readUnchangedCalls, rollbackSetups,
                exactTicksValidated, historyCount, metadataCount;
            public ulong firstRetainedTick, lastRetainedTick;
            public long allocatedBytes;
            public double nanosecondsPerCall;
            public DeterministicMetadataBenchmarkCounts counters;
        }
    }

    [Serializable] public struct DeterministicMetadataBenchmarkCounts
    {
        public long duplicates, disposals, copiedElements, disposedElements, equalityCalls, comparedElements;
        public void Add(DeterministicMetadataBenchmarkCounts other)
        {
            duplicates += other.duplicates; disposals += other.disposals;
            copiedElements += other.copiedElements; disposedElements += other.disposedElements;
            equalityCalls += other.equalityCalls; comparedElements += other.comparedElements;
        }
        public static DeterministicMetadataBenchmarkCounts operator -(DeterministicMetadataBenchmarkCounts a, DeterministicMetadataBenchmarkCounts b)
            => new() { duplicates = a.duplicates-b.duplicates, disposals = a.disposals-b.disposals,
                copiedElements = a.copiedElements-b.copiedElements, disposedElements = a.disposedElements-b.disposedElements,
                equalityCalls = a.equalityCalls-b.equalityCalls, comparedElements = a.comparedElements-b.comparedElements };
    }
    public static class DeterministicMetadataBenchmarkCounters
    {
        public static DeterministicMetadataBenchmarkCounts value;
        public static DeterministicMetadataBenchmarkCounts Read() => value;
    }
    public struct DeterministicMetadataBenchmarkSmall : IPredictedData<DeterministicMetadataBenchmarkSmall>
    {
        public int a, b, c, d;
        public void Dispose() { }
    }
    public struct DeterministicMetadataBenchmarkCollection : IPredictedData<DeterministicMetadataBenchmarkCollection>,
        IDuplicate<DeterministicMetadataBenchmarkCollection>, IEquatable<DeterministicMetadataBenchmarkCollection>
    {
        public int[] values;
        public DeterministicMetadataBenchmarkCollection Duplicate()
        {
            DeterministicMetadataBenchmarkCounters.value.duplicates++;
            DeterministicMetadataBenchmarkCounters.value.copiedElements += values?.Length ?? 0;
            return new DeterministicMetadataBenchmarkCollection { values = values == null ? null : (int[])values.Clone() };
        }
        public void Dispose()
        {
            if (values == null) return;
            DeterministicMetadataBenchmarkCounters.value.disposals++;
            DeterministicMetadataBenchmarkCounters.value.disposedElements += values.Length;
            values = null;
        }
        public bool Equals(DeterministicMetadataBenchmarkCollection other)
        {
            DeterministicMetadataBenchmarkCounters.value.equalityCalls++;
            if (values == null || other.values == null) return ReferenceEquals(values, other.values);
            if (values.Length != other.values.Length) return false;
            DeterministicMetadataBenchmarkCounters.value.comparedElements += values.Length;
            for (int i = 0; i < values.Length; i++) if (values[i] != other.values[i]) return false;
            return true;
        }
    }
    public sealed class DeterministicMetadataBenchmarkSmallProbe : DeterministicIdentity<DeterministicMetadataBenchmarkSmall> { }
    public sealed class DeterministicMetadataBenchmarkCollectionProbe : DeterministicIdentity<DeterministicMetadataBenchmarkCollection> { }
}
