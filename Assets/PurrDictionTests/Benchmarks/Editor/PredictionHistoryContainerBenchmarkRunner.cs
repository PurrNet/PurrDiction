using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using PurrNet;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    public static class PredictionHistoryContainerBenchmarkRunner
    {
        private const int SampleCount = 7;
        private const int WarmupIterations = 16;
        private const string DefaultOutputDirectory = "Temp/PurrDictionHistoryBenchmarks";

        private static int _intSink;

        [MenuItem("Tools/PurrDiction/Analysis/Run History Container Benchmarks", false, -89)]
        public static void RunFromMenu()
        {
            var report = Run();
            EditorUtility.RevealInFinder(report.markdownPath);
        }

        public static void RunFromCommandLine()
        {
            try
            {
                Run();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        public static HistoryContainerReport Run()
        {
            var outputDirectory = GetArgument("-purrdictionBenchmarkOutput") ?? DefaultOutputDirectory;
            Directory.CreateDirectory(outputDirectory);

            NetworkManager.LoadOrGenerateHashes();

            var report = new HistoryContainerReport
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                projectPath = Directory.GetCurrentDirectory(),
                outputDirectory = Path.GetFullPath(outputDirectory),
                jsonPath = Path.GetFullPath(Path.Combine(outputDirectory, "prediction-history-container-benchmarks.json")),
                markdownPath = Path.GetFullPath(Path.Combine(outputDirectory, "prediction-history-container-benchmarks.md"))
            };

            AddCurrentHistoryOperations(report);
            AddPrototypeRingOperations(report);

            WriteReports(report);
            Debug.Log($"PurrDiction history container benchmarks wrote:\n{report.jsonPath}\n{report.markdownPath}");
            return report;
        }

        private static void AddCurrentHistoryOperations(HistoryContainerReport report)
        {
            report.operations.Add(MeasureOperation(
                "CurrentHistory.Append.Preallocated",
                100000,
                () => new CurrentAppendContext(100001),
                (ctx, i) => ctx.history.Write((ulong)i, HistoryProbe.Create(i)),
                ctx => ctx.Dispose(),
                "Sequential append with capacity above iteration count; no trim."));

            report.operations.Add(MeasureOperation(
                "CurrentHistory.Append.BoundedWithTrim",
                100000,
                () => new CurrentAppendContext(1024),
                (ctx, i) => ctx.history.Write((ulong)i, HistoryProbe.Create(i)),
                ctx => ctx.Dispose(),
                "Sequential append with maxEntries=1024, including periodic RemoveRange trimming."));

            report.operations.Add(MeasureOperation(
                "CurrentHistory.OverwriteExisting",
                100000,
                () => CurrentReadContext.CreateDense(4096),
                (ctx, i) => ctx.history.Write((ulong)(i & 4095), HistoryProbe.Create(i)),
                ctx => ctx.Dispose(),
                "Writes repeatedly to ticks that already exist; exercises binary search plus dispose old value."));

            report.operations.Add(MeasureOperation(
                "CurrentHistory.InsertMiddle",
                4096,
                () => CurrentInsertContext.Create(4096),
                (ctx, i) => ctx.WriteOddTick(i),
                ctx => ctx.Dispose(),
                "Starts with even ticks, inserts odd ticks; exercises sorted List<T>.Insert movement."));

            report.operations.Add(MeasureOperation(
                "CurrentHistory.Read.Exact",
                100000,
                () => CurrentReadContext.CreateDense(4096),
                (ctx, i) =>
                {
                    if (ctx.history.Read((ulong)(i & 4095), out var value))
                        _intSink ^= value.a;
                },
                ctx => ctx.Dispose(),
                "Exact tick read over 4096 sorted entries."));

            report.operations.Add(MeasureOperation(
                "CurrentHistory.ReadOrPrevious",
                100000,
                () => CurrentReadContext.CreateEvenTicks(4096),
                (ctx, i) =>
                {
                    if (ctx.history.ReadOrPrevious((ulong)((i & 4095) * 2 + 1), out var value))
                        _intSink ^= value.a;
                },
                ctx => ctx.Dispose(),
                "Misses exact tick and returns previous entry over 4096 sorted entries."));

            report.operations.Add(MeasureOperation(
                "CurrentHistory.TryGetClosest",
                100000,
                () => CurrentReadContext.CreateEvenTicks(4096),
                (ctx, i) =>
                {
                    if (ctx.history.TryGetClosest((ulong)((i & 4095) * 2 + 1), out var value))
                        _intSink ^= value.a;
                },
                ctx => ctx.Dispose(),
                "Closest tick lookup over 4096 sorted entries."));
        }

        private static void AddPrototypeRingOperations(HistoryContainerReport report)
        {
            report.operations.Add(MeasureOperation(
                "PrototypeRing.Append.BoundedOverwrite",
                100000,
                () => new RingAppendContext(1024),
                (ctx, i) => ctx.history.Write((ulong)i, HistoryProbe.Create(i)),
                ctx => ctx.Dispose(),
                "Sequential append with capacity=1024, overwriting oldest slot instead of RemoveRange."));

            report.operations.Add(MeasureOperation(
                "PrototypeRing.Read.Exact",
                100000,
                () => RingReadContext.CreateDense(4096),
                (ctx, i) =>
                {
                    if (ctx.history.Read((ulong)(i & 4095), out var value))
                        _intSink ^= value.a;
                },
                ctx => ctx.Dispose(),
                "Exact tick read over 4096 sorted entries using binary search over logical ring order."));

            report.operations.Add(MeasureOperation(
                "PrototypeRing.ReadOrPrevious",
                100000,
                () => RingReadContext.CreateEvenTicks(4096),
                (ctx, i) =>
                {
                    if (ctx.history.ReadOrPrevious((ulong)((i & 4095) * 2 + 1), out var value))
                        _intSink ^= value.a;
                },
                ctx => ctx.Dispose(),
                "Previous tick lookup over 4096 sorted entries using binary search over logical ring order."));
        }

        private static HistoryOperationResult MeasureOperation<TContext>(
            string name,
            int iterations,
            Func<TContext> setup,
            Action<TContext, int> body,
            Action<TContext> cleanup,
            string notes) where TContext : class
        {
            Debug.Log($"PurrDiction history container benchmark: measuring {name}.");

            try
            {
                TContext warmupContext = null;
                try
                {
                    warmupContext = setup();
                    var warmups = Math.Min(iterations, WarmupIterations);
                    for (int i = 0; i < warmups; i++)
                        body(warmupContext, i);
                }
                finally
                {
                    if (warmupContext != null)
                        cleanup(warmupContext);
                }

                var timings = new double[SampleCount];
                var allocations = new double[SampleCount];

                for (int sample = 0; sample < SampleCount; sample++)
                {
                    TContext context = null;
                    try
                    {
                        context = setup();

                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();

                        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();

                        for (int i = 0; i < iterations; i++)
                            body(context, i);

                        long elapsed = Stopwatch.GetTimestamp() - started;
                        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

                        timings[sample] = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;
                        allocations[sample] = (allocatedAfter - allocatedBefore) / (double)iterations;
                    }
                    finally
                    {
                        if (context != null)
                            cleanup(context);
                    }
                }

                var result = HistoryOperationResult.FromSamples(name, iterations, timings, allocations, notes);
                Debug.Log($"PurrDiction history container benchmark: measured {name} = {FormatNumber(result.medianNanoseconds)} ns/op, {FormatNumber(result.medianAllocatedBytes)} B/op.");
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"PurrDiction history container benchmark: skipped {name}: {e.GetType().Name}: {e.Message}");
                return HistoryOperationResult.Skipped(name, iterations, $"{e.GetType().Name}: {e.Message}");
            }
        }

        private static void WriteReports(HistoryContainerReport report)
        {
            File.WriteAllText(report.jsonPath, BuildJson(report));
            File.WriteAllText(report.markdownPath, BuildMarkdown(report));
        }

        private static string BuildMarkdown(HistoryContainerReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# PurrDiction History Container Benchmarks");
            sb.AppendLine();
            sb.AppendLine($"Generated: `{report.generatedAtUtc}`");
            sb.AppendLine($"Unity: `{report.unityVersion}`");
            sb.AppendLine($"Project: `{report.projectPath}`");
            sb.AppendLine();
            sb.AppendLine("## Operations");
            sb.AppendLine();
            sb.AppendLine("| Operation | Iterations | Median ns/op | Min | Max | Alloc B/op | Notes |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");

            foreach (var op in report.operations)
            {
                sb.Append("| ");
                sb.Append(EscapeMarkdown(op.name));
                sb.Append(" | ");
                sb.Append(op.iterations);
                sb.Append(" | ");
                sb.Append(op.skipped ? "skipped" : FormatNumber(op.medianNanoseconds));
                sb.Append(" | ");
                sb.Append(op.skipped ? "" : FormatNumber(op.minNanoseconds));
                sb.Append(" | ");
                sb.Append(op.skipped ? "" : FormatNumber(op.maxNanoseconds));
                sb.Append(" | ");
                sb.Append(op.skipped ? "" : FormatNumber(op.medianAllocatedBytes));
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(op.notes));
                sb.AppendLine(" |");
            }

            sb.AppendLine();
            sb.AppendLine("## How To Run");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("Unity.exe -batchmode -projectPath C:\\wkspaces\\unity\\riten\\PurrDiction -executeMethod PurrNet.Prediction.Benchmarks.Editor.PredictionHistoryContainerBenchmarkRunner.RunFromCommandLine -quit -logFile Temp\\PurrDictionHistoryBenchmarks\\history-container.log");
            sb.AppendLine("```");
            return sb.ToString();
        }

        private static string BuildJson(HistoryContainerReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            AppendJsonField(sb, "generatedAtUtc", report.generatedAtUtc, 1, comma: true);
            AppendJsonField(sb, "unityVersion", report.unityVersion, 1, comma: true);
            AppendJsonField(sb, "projectPath", report.projectPath, 1, comma: true);
            AppendJsonField(sb, "outputDirectory", report.outputDirectory, 1, comma: true);
            AppendJsonField(sb, "jsonPath", report.jsonPath, 1, comma: true);
            AppendJsonField(sb, "markdownPath", report.markdownPath, 1, comma: true);

            Indent(sb, 1);
            sb.AppendLine("\"operations\": [");
            for (int i = 0; i < report.operations.Count; i++)
            {
                AppendOperationJson(sb, report.operations[i], 2);
                sb.AppendLine(i == report.operations.Count - 1 ? "" : ",");
            }
            Indent(sb, 1);
            sb.AppendLine("]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AppendOperationJson(StringBuilder sb, HistoryOperationResult op, int indent)
        {
            Indent(sb, indent);
            sb.AppendLine("{");
            AppendJsonField(sb, "name", op.name, indent + 1, comma: true);
            AppendJsonField(sb, "iterations", op.iterations, indent + 1, comma: true);
            AppendJsonField(sb, "samples", op.samples, indent + 1, comma: true);
            AppendJsonField(sb, "skipped", op.skipped, indent + 1, comma: true);
            AppendJsonField(sb, "medianNanoseconds", op.medianNanoseconds, indent + 1, comma: true);
            AppendJsonField(sb, "minNanoseconds", op.minNanoseconds, indent + 1, comma: true);
            AppendJsonField(sb, "maxNanoseconds", op.maxNanoseconds, indent + 1, comma: true);
            AppendJsonField(sb, "medianAllocatedBytes", op.medianAllocatedBytes, indent + 1, comma: true);
            AppendJsonField(sb, "minAllocatedBytes", op.minAllocatedBytes, indent + 1, comma: true);
            AppendJsonField(sb, "maxAllocatedBytes", op.maxAllocatedBytes, indent + 1, comma: true);
            AppendJsonField(sb, "notes", op.notes, indent + 1, comma: false);
            Indent(sb, indent);
            sb.Append("}");
        }

        private static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static string FormatNumber(double value)
        {
            if (value >= 1000)
                return value.ToString("N0");
            if (value >= 10)
                return value.ToString("N1");
            return value.ToString("N2");
        }

        private static string EscapeMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static void AppendJsonField(StringBuilder sb, string name, string value, int indent, bool comma)
        {
            Indent(sb, indent);
            AppendJsonString(sb, name);
            sb.Append(": ");
            AppendJsonString(sb, value ?? "");
            sb.AppendLine(comma ? "," : "");
        }

        private static void AppendJsonField(StringBuilder sb, string name, bool value, int indent, bool comma)
        {
            Indent(sb, indent);
            AppendJsonString(sb, name);
            sb.Append(": ");
            sb.Append(value ? "true" : "false");
            sb.AppendLine(comma ? "," : "");
        }

        private static void AppendJsonField(StringBuilder sb, string name, int value, int indent, bool comma)
        {
            Indent(sb, indent);
            AppendJsonString(sb, name);
            sb.Append(": ");
            sb.Append(value);
            sb.AppendLine(comma ? "," : "");
        }

        private static void AppendJsonField(StringBuilder sb, string name, double value, int indent, bool comma)
        {
            Indent(sb, indent);
            AppendJsonString(sb, name);
            sb.Append(": ");
            sb.Append(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine(comma ? "," : "");
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '\\':
                            sb.Append("\\\\");
                            break;
                        case '"':
                            sb.Append("\\\"");
                            break;
                        case '\n':
                            sb.Append("\\n");
                            break;
                        case '\r':
                            sb.Append("\\r");
                            break;
                        case '\t':
                            sb.Append("\\t");
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++)
                sb.Append("  ");
        }

        [Serializable]
        public sealed class HistoryContainerReport
        {
            public string generatedAtUtc;
            public string unityVersion;
            public string projectPath;
            public string outputDirectory;
            public string jsonPath;
            public string markdownPath;
            public readonly List<HistoryOperationResult> operations = new();
        }

        [Serializable]
        public sealed class HistoryOperationResult
        {
            public string name;
            public int iterations;
            public int samples;
            public bool skipped;
            public double medianNanoseconds;
            public double minNanoseconds;
            public double maxNanoseconds;
            public double medianAllocatedBytes;
            public double minAllocatedBytes;
            public double maxAllocatedBytes;
            public string notes;

            public static HistoryOperationResult Skipped(string name, int iterations, string notes)
            {
                return new HistoryOperationResult
                {
                    name = name,
                    iterations = iterations,
                    samples = 0,
                    skipped = true,
                    notes = notes
                };
            }

            public static HistoryOperationResult FromSamples(
                string name,
                int iterations,
                double[] timings,
                double[] allocations,
                string notes)
            {
                return new HistoryOperationResult
                {
                    name = name,
                    iterations = iterations,
                    samples = timings.Length,
                    skipped = false,
                    medianNanoseconds = Median(timings),
                    minNanoseconds = Min(timings),
                    maxNanoseconds = Max(timings),
                    medianAllocatedBytes = Median(allocations),
                    minAllocatedBytes = Min(allocations),
                    maxAllocatedBytes = Max(allocations),
                    notes = notes ?? ""
                };
            }

            private static double Median(double[] values)
            {
                var copy = (double[])values.Clone();
                Array.Sort(copy);
                int middle = copy.Length / 2;
                if ((copy.Length & 1) == 1)
                    return copy[middle];
                return (copy[middle - 1] + copy[middle]) * 0.5;
            }

            private static double Min(double[] values)
            {
                double min = double.MaxValue;
                for (int i = 0; i < values.Length; i++)
                    if (values[i] < min)
                        min = values[i];
                return min;
            }

            private static double Max(double[] values)
            {
                double max = double.MinValue;
                for (int i = 0; i < values.Length; i++)
                    if (values[i] > max)
                        max = values[i];
                return max;
            }
        }

        private struct HistoryProbe : IDisposable
        {
            public int a;
            public int b;
            public int c;
            public int d;

            public static HistoryProbe Create(int value)
            {
                return new HistoryProbe
                {
                    a = value,
                    b = value * 3,
                    c = value * 7,
                    d = value * 11
                };
            }

            public void Dispose()
            {
            }
        }

        private sealed class CurrentAppendContext : IDisposable
        {
            public readonly History<HistoryProbe> history;

            public CurrentAppendContext(int capacity)
            {
                history = new History<HistoryProbe>(capacity);
            }

            public void Dispose()
            {
                history.Clear();
            }
        }

        private sealed class CurrentInsertContext : IDisposable
        {
            public readonly History<HistoryProbe> history;

            private CurrentInsertContext(History<HistoryProbe> history)
            {
                this.history = history;
            }

            public static CurrentInsertContext Create(int count)
            {
                var history = new History<HistoryProbe>(count * 2 + 1);
                for (int i = 0; i < count; i++)
                    history.Write((ulong)(i * 2), HistoryProbe.Create(i));
                return new CurrentInsertContext(history);
            }

            public void WriteOddTick(int index)
            {
                history.Write((ulong)(index * 2 + 1), HistoryProbe.Create(index));
            }

            public void Dispose()
            {
                history.Clear();
            }
        }

        private sealed class CurrentReadContext : IDisposable
        {
            public readonly History<HistoryProbe> history;

            private CurrentReadContext(History<HistoryProbe> history)
            {
                this.history = history;
            }

            public static CurrentReadContext CreateDense(int count)
            {
                var history = new History<HistoryProbe>(count + 1);
                for (int i = 0; i < count; i++)
                    history.Write((ulong)i, HistoryProbe.Create(i));
                return new CurrentReadContext(history);
            }

            public static CurrentReadContext CreateEvenTicks(int count)
            {
                var history = new History<HistoryProbe>(count + 1);
                for (int i = 0; i < count; i++)
                    history.Write((ulong)(i * 2), HistoryProbe.Create(i));
                return new CurrentReadContext(history);
            }

            public void Dispose()
            {
                history.Clear();
            }
        }

        private sealed class RingHistory<T> : IDisposable where T : struct, IDisposable
        {
            private struct Entry
            {
                public ulong tick;
                public T data;
                public bool hasValue;
            }

            private readonly Entry[] _entries;
            private int _start;
            private int _count;

            public RingHistory(int capacity)
            {
                _entries = new Entry[capacity];
            }

            public void Write(ulong tick, in T data)
            {
                if (_count == _entries.Length)
                {
                    _entries[_start].data.Dispose();
                    _entries[_start] = new Entry
                    {
                        tick = tick,
                        data = data,
                        hasValue = true
                    };
                    _start = (_start + 1) % _entries.Length;
                    return;
                }

                int index = PhysicalIndex(_count);
                _entries[index] = new Entry
                {
                    tick = tick,
                    data = data,
                    hasValue = true
                };
                _count++;
            }

            public bool Read(ulong tick, out T value)
            {
                value = default;
                int index = Find(tick, out var found);
                if (!found)
                    return false;

                value = _entries[PhysicalIndex(index)].data;
                return true;
            }

            public bool ReadOrPrevious(ulong tick, out T value)
            {
                value = default;
                int index = Find(tick, out var found);

                if (found)
                {
                    value = _entries[PhysicalIndex(index)].data;
                    return true;
                }

                int previous = index - 1;
                if (previous < 0)
                    return false;

                value = _entries[PhysicalIndex(previous)].data;
                return true;
            }

            private int Find(ulong tick, out bool found)
            {
                int min = 0;
                int max = _count - 1;

                while (min <= max)
                {
                    int mid = (min + max) / 2;
                    var entryTick = _entries[PhysicalIndex(mid)].tick;

                    if (tick == entryTick)
                    {
                        found = true;
                        return mid;
                    }

                    if (tick < entryTick)
                        max = mid - 1;
                    else min = mid + 1;
                }

                found = false;
                return min;
            }

            private int PhysicalIndex(int logicalIndex)
            {
                return (_start + logicalIndex) % _entries.Length;
            }

            public void Dispose()
            {
                for (int i = 0; i < _entries.Length; i++)
                {
                    if (_entries[i].hasValue)
                    {
                        _entries[i].data.Dispose();
                        _entries[i] = default;
                    }
                }

                _start = 0;
                _count = 0;
            }
        }

        private sealed class RingAppendContext : IDisposable
        {
            public readonly RingHistory<HistoryProbe> history;

            public RingAppendContext(int capacity)
            {
                history = new RingHistory<HistoryProbe>(capacity);
            }

            public void Dispose()
            {
                history.Dispose();
            }
        }

        private sealed class RingReadContext : IDisposable
        {
            public readonly RingHistory<HistoryProbe> history;

            private RingReadContext(RingHistory<HistoryProbe> history)
            {
                this.history = history;
            }

            public static RingReadContext CreateDense(int count)
            {
                var history = new RingHistory<HistoryProbe>(count + 1);
                for (int i = 0; i < count; i++)
                    history.Write((ulong)i, HistoryProbe.Create(i));
                return new RingReadContext(history);
            }

            public static RingReadContext CreateEvenTicks(int count)
            {
                var history = new RingHistory<HistoryProbe>(count + 1);
                for (int i = 0; i < count; i++)
                    history.Write((ulong)(i * 2), HistoryProbe.Create(i));
                return new RingReadContext(history);
            }

            public void Dispose()
            {
                history.Dispose();
            }
        }
    }
}
