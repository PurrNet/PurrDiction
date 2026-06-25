using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction.Tests;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    public static class PredictionDeltaHistoryBenchmarkRunner
    {
        private const int SampleCount = 3;
        private const int WarmupIterations = 2;
        private const string DefaultOutputDirectory = "Temp/PurrDictionHistoryBenchmarks";

        private static int _intSink;
        private static string _currentTypeName;

        [MenuItem("Tools/PurrDiction/Analysis/Run Delta History Benchmarks", false, -88)]
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

        public static DeltaHistoryReport Run()
        {
            var outputDirectory = GetArgument("-purrdictionBenchmarkOutput") ?? DefaultOutputDirectory;
            Directory.CreateDirectory(outputDirectory);

            NetworkManager.LoadOrGenerateHashes();

            var report = new DeltaHistoryReport
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                projectPath = Directory.GetCurrentDirectory(),
                outputDirectory = Path.GetFullPath(outputDirectory),
                jsonPath = Path.GetFullPath(Path.Combine(outputDirectory, "prediction-delta-history-benchmarks.json")),
                markdownPath = Path.GetFullPath(Path.Combine(outputDirectory, "prediction-delta-history-benchmarks.md"))
            };

            BenchmarkType(report, "PredictedTransformState", CreatePredictedTransformState, CreateChangedPredictedTransformState, 10000, 16);
            WriteReports(report);

            BenchmarkType(report, "ProjectileState3D", () => CreateProjectileState(8), () => CreateProjectileState(9), 1000, 16);
            WriteReports(report);

            BenchmarkType(report, "PredictedPhysicsData", () => CreatePredictedPhysicsData(16), () => CreatePredictedPhysicsData(17), 250, 8);
            WriteReports(report);

            BenchmarkType(report, "FallingSandState_10", () => CreateFallingSandState(10, 10), () => CreateFallingSandState(10, 11), 250, 8);
            WriteReports(report);

            BenchmarkType(report, "FallingSandState_64", () => CreateFallingSandState(64, 64), () => CreateFallingSandState(64, 65), 16, 4);
            WriteReports(report);

            BenchmarkType(report, "FallingSandState_128", () => CreateFallingSandState(128, 128), () => CreateFallingSandState(128, 129), 2, 2);
            WriteReports(report);

            BenchmarkType(report, "FallingSandState_256", () => CreateFallingSandState(256, 256), () => CreateFallingSandState(256, 257), 1, 1);
            WriteReports(report);

            Debug.Log($"PurrDiction delta history benchmarks wrote:\n{report.jsonPath}\n{report.markdownPath}");
            return report;
        }

        private static void BenchmarkType<T>(
            DeltaHistoryReport report,
            string name,
            Func<T> create,
            Func<T> createChanged,
            int iterations,
            int replayDeltas) where T : struct, IPredictedData<T>
        {
            _currentTypeName = name;
            Debug.Log($"PurrDiction delta history benchmark: starting {name} ({iterations} iterations, replay {replayDeltas}).");

            var type = new DeltaTypeResult
            {
                name = name,
                stateType = typeof(T).FullName,
                containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
                implementsIDuplicate = typeof(IDuplicate<T>).IsAssignableFrom(typeof(T)),
                hasDeltaPacker = DeltaPacker<T>.HasPacker(),
                replayDeltas = replayDeltas
            };

            type.operations.Add(MeasureSnapshotStoreCopy(iterations, createChanged));
            type.operations.Add(MeasureSnapshotRestoreCopy(iterations, createChanged));
            type.operations.Add(MeasureDeltaStore(iterations, "DeltaHistory.StoreEqual", create, create, "Write equal delta into a retained pooled BitPacker."));
            type.operations.Add(MeasureDeltaStore(iterations, "DeltaHistory.StoreChanged", create, createChanged, "Write changed delta into a retained pooled BitPacker."));
            type.operations.Add(MeasureDeltaApply(iterations, "DeltaHistory.ApplyOneEqual", create, create, "Apply one stored equal delta to a copied baseline."));
            type.operations.Add(MeasureDeltaApply(iterations, "DeltaHistory.ApplyOneChanged", create, createChanged, "Apply one stored changed delta to a copied baseline."));
            type.operations.Add(MeasureDeltaReplay(iterations, replayDeltas, create, createChanged));

            report.types.Add(type);
            Debug.Log($"PurrDiction delta history benchmark: finished {name}.");
        }

        private static DeltaOperationResult MeasureSnapshotStoreCopy<T>(
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "FullSnapshot.StoreCopy",
                iterations,
                () => SnapshotStoreContext<T>.Create(iterations, create),
                (ctx, i) => ctx.Store(i),
                ctx => ctx.Dispose(),
                -1,
                "Store a full snapshot by PurrCopy/IDuplicate.");
        }

        private static DeltaOperationResult MeasureSnapshotRestoreCopy<T>(
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "FullSnapshot.RestoreCopy",
                iterations,
                () => SnapshotRestoreContext<T>.Create(create),
                (ctx, _) =>
                {
                    var restored = PurrCopy<T>.Copy(ctx.snapshot);
                    restored.Dispose();
                },
                ctx => ctx.Dispose(),
                -1,
                "Restore/read a full snapshot by copying the saved state.");
        }

        private static DeltaOperationResult MeasureDeltaStore<T>(
            int iterations,
            string operationName,
            Func<T> createOld,
            Func<T> createNew,
            string notes) where T : struct, IPredictedData<T>
        {
            if (!DeltaPacker<T>.HasPacker())
                return DeltaOperationResult.Skipped(operationName, iterations, "DeltaPacker<T>.HasPacker() is false.");

            return MeasureOperation(
                operationName,
                iterations,
                () => DeltaStoreContext<T>.Create(iterations, createOld, createNew),
                (ctx, i) => ctx.Store(i),
                ctx => ctx.Dispose(),
                MeasureDeltaBits(createOld, createNew),
                notes);
        }

        private static DeltaOperationResult MeasureDeltaApply<T>(
            int iterations,
            string operationName,
            Func<T> createOld,
            Func<T> createNew,
            string notes) where T : struct, IPredictedData<T>
        {
            if (!DeltaPacker<T>.HasPacker())
                return DeltaOperationResult.Skipped(operationName, iterations, "DeltaPacker<T>.HasPacker() is false.");

            return MeasureOperation(
                operationName,
                iterations,
                () => DeltaApplyContext<T>.Create(createOld, createNew),
                (ctx, _) => ctx.Apply(),
                ctx => ctx.Dispose(),
                MeasureDeltaBits(createOld, createNew),
                notes);
        }

        private static DeltaOperationResult MeasureDeltaReplay<T>(
            int iterations,
            int replayDeltas,
            Func<T> createA,
            Func<T> createB) where T : struct, IPredictedData<T>
        {
            if (!DeltaPacker<T>.HasPacker())
                return DeltaOperationResult.Skipped("DeltaHistory.ReplayChain", iterations, "DeltaPacker<T>.HasPacker() is false.");

            return MeasureOperation(
                "DeltaHistory.ReplayChain",
                iterations,
                () => DeltaReplayContext<T>.Create(replayDeltas, createA, createB),
                (ctx, _) => ctx.Replay(),
                ctx => ctx.Dispose(),
                -1,
                $"Restore by copying one baseline and applying {replayDeltas} stored changed deltas.");
        }

        private static DeltaOperationResult MeasureOperation<TContext>(
            string name,
            int iterations,
            Func<TContext> setup,
            Action<TContext, int> body,
            Action<TContext> cleanup,
            long serializedBits,
            string notes) where TContext : class
        {
            Debug.Log($"PurrDiction delta history benchmark: measuring {_currentTypeName}.{name}.");

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

                var result = DeltaOperationResult.FromSamples(name, iterations, timings, allocations, serializedBits, notes);
                Debug.Log($"PurrDiction delta history benchmark: measured {_currentTypeName}.{name} = {FormatNumber(result.medianNanoseconds)} ns/op, {FormatNumber(result.medianAllocatedBytes)} B/op.");
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"PurrDiction delta history benchmark: skipped {_currentTypeName}.{name}: {e.GetType().Name}: {e.Message}");
                return DeltaOperationResult.Skipped(name, iterations, $"{e.GetType().Name}: {e.Message}");
            }
        }

        private static long MeasureDeltaBits<T>(Func<T> createOld, Func<T> createNew) where T : struct, IPredictedData<T>
        {
            T oldValue = default;
            T newValue = default;
            try
            {
                oldValue = createOld();
                newValue = createNew();
                using var packer = BitPackerPool.Get();
                DeltaPacker<T>.Write(packer, oldValue, newValue);
                return packer.positionInBits;
            }
            catch
            {
                return -1;
            }
            finally
            {
                oldValue.Dispose();
                newValue.Dispose();
            }
        }

        private static PredictedTransformState CreatePredictedTransformState()
        {
            return new PredictedTransformState
            {
                unityPosition = new Vector3(1.25f, 2.5f, 3.75f),
                unityRotation = Quaternion.Euler(10f, 20f, 30f)
            };
        }

        private static PredictedTransformState CreateChangedPredictedTransformState()
        {
            return new PredictedTransformState
            {
                unityPosition = new Vector3(1.5f, 2.5f, 3.75f),
                unityRotation = Quaternion.Euler(10f, 25f, 30f)
            };
        }

        private static PredictedPhysicsData CreatePredictedPhysicsData(int eventCount)
        {
            var events = DisposableList<PhysicsEvent>.Create(eventCount);
            for (int i = 0; i < eventCount; i++)
                events.Add(CreatePhysicsEvent(i));

            return new PredictedPhysicsData
            {
                events = events
            };
        }

        private static PhysicsEvent CreatePhysicsEvent(int index)
        {
            var collision = new PhysicsCollision();
#if UNITY_PHYSICS_3D
            collision.contacts = DisposableList<PhysicsContactPoint>.Create(4);
            for (int i = 0; i < 4; i++)
            {
                collision.contacts.Add(new PhysicsContactPoint
                {
                    point = new Vector3(index, i, index + i),
                    normal = Vector3.up,
                    separation = -0.01f * i
                });
            }

            collision.impulse = new Vector3(index * 0.1f, index * 0.2f, index * 0.3f);
            collision.relativeVelocity = new Vector3(index * 0.4f, index * 0.5f, index * 0.6f);
#endif

            return new PhysicsEvent
            {
                isTrigger = (index & 1) == 0,
                type = (PhysicsEventType)(index % 3),
                me = new PredictedComponentID(new PredictedObjectID((uint)index + 10), 1),
                other = new PredictedComponentID(new PredictedObjectID((uint)index + 20), 2),
                collision = collision
            };
        }

        private static ProjectileState3D CreateProjectileState(int overlapCount)
        {
            var overlaps = DisposableList<PredictedComponentID>.Create(overlapCount);
            for (int i = 0; i < overlapCount; i++)
                overlaps.Add(new PredictedComponentID(new PredictedObjectID((uint)i + 50), 3));

            return new ProjectileState3D
            {
                velocity = new Vector3(5f + overlapCount, 2f, 1f),
                gravity = -9.81f,
                radius = 0.25f,
                isTrigger = true,
                overlappingTriggers = overlaps,
                lastSolidContact = new PredictedComponentID(new PredictedObjectID(77), 4),
                hasLastSolidContact = overlapCount > 8
            };
        }

        private static FallingSandState CreateFallingSandState(int gridSize, int variant)
        {
            int count = gridSize * gridSize;
            var grid = DisposableArray<SandTile>.Create(count);
            var dirtyIndexes = DisposableList<Size>.Create(Math.Min(count, gridSize * 2));
            int populated = Math.Min(count, Math.Max(1, gridSize * 2));

            for (int i = 0; i < populated; i++)
            {
                int index = (i * 37 + variant * 13) % count;
                grid[index] = new SandTile
                {
                    color = new ByteColor
                    {
                        R = (byte)(32 + (index % 200)),
                        G = (byte)(64 + (variant % 128)),
                        B = (byte)(96 + (i % 128))
                    }
                };
                dirtyIndexes.Add(index);
            }

            return new FallingSandState
            {
                grid = grid,
                dirtyIndexes = dirtyIndexes,
                random = PredictedRandom.Create((uint)(65645 + variant))
            };
        }

        private static void WriteReports(DeltaHistoryReport report)
        {
            File.WriteAllText(report.jsonPath, BuildJson(report));
            File.WriteAllText(report.markdownPath, BuildMarkdown(report));
        }

        private static string BuildMarkdown(DeltaHistoryReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# PurrDiction Delta History Benchmarks");
            sb.AppendLine();
            sb.AppendLine($"Generated: `{report.generatedAtUtc}`");
            sb.AppendLine($"Unity: `{report.unityVersion}`");
            sb.AppendLine($"Project: `{report.projectPath}`");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| Type | Snapshot Store | Delta Store Equal | Delta Store Changed | Snapshot Restore | Delta Apply Changed | Delta Replay Chain | Changed Delta Bits |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var type in report.types)
            {
                var snapshotStore = Find(type, "FullSnapshot.StoreCopy");
                var deltaEqual = Find(type, "DeltaHistory.StoreEqual");
                var deltaChanged = Find(type, "DeltaHistory.StoreChanged");
                var snapshotRestore = Find(type, "FullSnapshot.RestoreCopy");
                var applyChanged = Find(type, "DeltaHistory.ApplyOneChanged");
                var replay = Find(type, "DeltaHistory.ReplayChain");

                sb.Append("| ");
                sb.Append(EscapeMarkdown(type.name));
                sb.Append(" | ");
                sb.Append(FormatOperation(snapshotStore));
                sb.Append(" | ");
                sb.Append(FormatOperation(deltaEqual));
                sb.Append(" | ");
                sb.Append(FormatOperation(deltaChanged));
                sb.Append(" | ");
                sb.Append(FormatOperation(snapshotRestore));
                sb.Append(" | ");
                sb.Append(FormatOperation(applyChanged));
                sb.Append(" | ");
                sb.Append(FormatOperation(replay));
                sb.Append(" | ");
                sb.Append(deltaChanged != null && deltaChanged.serializedBits >= 0 ? deltaChanged.serializedBits.ToString() : "");
                sb.AppendLine(" |");
            }

            sb.AppendLine();
            sb.AppendLine("## Type Details");
            foreach (var type in report.types)
            {
                sb.AppendLine();
                sb.AppendLine($"### {type.name}");
                sb.AppendLine();
                sb.AppendLine($"- State type: `{type.stateType}`");
                sb.AppendLine($"- Contains references: `{type.containsReferences}`");
                sb.AppendLine($"- Implements `IDuplicate<T>`: `{type.implementsIDuplicate}`");
                sb.AppendLine($"- `DeltaPacker<T>.HasPacker()`: `{type.hasDeltaPacker}`");
                sb.AppendLine($"- Replay chain deltas: `{type.replayDeltas}`");
                sb.AppendLine();
                sb.AppendLine("| Operation | Iterations | Median ns/op | Min | Max | Alloc B/op | Bits | Notes |");
                sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
                foreach (var op in type.operations)
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
                    sb.Append(op.serializedBits >= 0 ? op.serializedBits.ToString() : "");
                    sb.Append(" | ");
                    sb.Append(EscapeMarkdown(op.notes));
                    sb.AppendLine(" |");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## How To Run");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("Unity.exe -batchmode -projectPath C:\\wkspaces\\unity\\riten\\PurrDiction -executeMethod PurrNet.Prediction.Benchmarks.Editor.PredictionDeltaHistoryBenchmarkRunner.RunFromCommandLine -quit -logFile Temp\\PurrDictionHistoryBenchmarks\\delta-history.log");
            sb.AppendLine("```");
            return sb.ToString();
        }

        private static DeltaOperationResult Find(DeltaTypeResult type, string name)
        {
            for (int i = 0; i < type.operations.Count; i++)
            {
                if (type.operations[i].name == name)
                    return type.operations[i];
            }
            return null;
        }

        private static string FormatOperation(DeltaOperationResult operation)
        {
            if (operation == null)
                return "";
            if (operation.skipped)
                return "skipped";
            return FormatNumber(operation.medianNanoseconds);
        }

        private static string BuildJson(DeltaHistoryReport report)
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
            sb.AppendLine("\"types\": [");
            for (int i = 0; i < report.types.Count; i++)
            {
                AppendTypeJson(sb, report.types[i], 2);
                sb.AppendLine(i == report.types.Count - 1 ? "" : ",");
            }
            Indent(sb, 1);
            sb.AppendLine("]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AppendTypeJson(StringBuilder sb, DeltaTypeResult type, int indent)
        {
            Indent(sb, indent);
            sb.AppendLine("{");
            AppendJsonField(sb, "name", type.name, indent + 1, comma: true);
            AppendJsonField(sb, "stateType", type.stateType, indent + 1, comma: true);
            AppendJsonField(sb, "containsReferences", type.containsReferences, indent + 1, comma: true);
            AppendJsonField(sb, "implementsIDuplicate", type.implementsIDuplicate, indent + 1, comma: true);
            AppendJsonField(sb, "hasDeltaPacker", type.hasDeltaPacker, indent + 1, comma: true);
            AppendJsonField(sb, "replayDeltas", type.replayDeltas, indent + 1, comma: true);

            Indent(sb, indent + 1);
            sb.AppendLine("\"operations\": [");
            for (int i = 0; i < type.operations.Count; i++)
            {
                AppendOperationJson(sb, type.operations[i], indent + 2);
                sb.AppendLine(i == type.operations.Count - 1 ? "" : ",");
            }
            Indent(sb, indent + 1);
            sb.AppendLine("]");
            Indent(sb, indent);
            sb.Append("}");
        }

        private static void AppendOperationJson(StringBuilder sb, DeltaOperationResult op, int indent)
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
            AppendJsonField(sb, "serializedBits", op.serializedBits, indent + 1, comma: true);
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

        private static void AppendJsonField(StringBuilder sb, string name, long value, int indent, bool comma)
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

        private static string EscapeMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static string FormatNumber(double value)
        {
            if (value >= 1000)
                return value.ToString("N0");
            if (value >= 10)
                return value.ToString("N1");
            return value.ToString("N2");
        }

        [Serializable]
        public sealed class DeltaHistoryReport
        {
            public string generatedAtUtc;
            public string unityVersion;
            public string projectPath;
            public string outputDirectory;
            public string jsonPath;
            public string markdownPath;
            public readonly List<DeltaTypeResult> types = new();
        }

        [Serializable]
        public sealed class DeltaTypeResult
        {
            public string name;
            public string stateType;
            public bool containsReferences;
            public bool implementsIDuplicate;
            public bool hasDeltaPacker;
            public int replayDeltas;
            public readonly List<DeltaOperationResult> operations = new();
        }

        [Serializable]
        public sealed class DeltaOperationResult
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
            public long serializedBits;
            public string notes;

            public static DeltaOperationResult Skipped(string name, int iterations, string notes)
            {
                return new DeltaOperationResult
                {
                    name = name,
                    iterations = iterations,
                    samples = 0,
                    skipped = true,
                    serializedBits = -1,
                    notes = notes
                };
            }

            public static DeltaOperationResult FromSamples(
                string name,
                int iterations,
                double[] timings,
                double[] allocations,
                long serializedBits,
                string notes)
            {
                return new DeltaOperationResult
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
                    serializedBits = serializedBits,
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

        private sealed class SnapshotStoreContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            private readonly T[] _snapshots;
            private T _source;

            private SnapshotStoreContext(T[] snapshots, T source)
            {
                _snapshots = snapshots;
                _source = source;
            }

            public static SnapshotStoreContext<T> Create(int iterations, Func<T> create)
            {
                return new SnapshotStoreContext<T>(new T[iterations], create());
            }

            public void Store(int index)
            {
                _snapshots[index] = PurrCopy<T>.Copy(_source);
                _intSink ^= index;
            }

            public void Dispose()
            {
                for (int i = 0; i < _snapshots.Length; i++)
                {
                    _snapshots[i].Dispose();
                    _snapshots[i] = default;
                }

                _source.Dispose();
                _source = default;
            }
        }

        private sealed class SnapshotRestoreContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            public T snapshot;

            private SnapshotRestoreContext(T snapshot)
            {
                this.snapshot = snapshot;
            }

            public static SnapshotRestoreContext<T> Create(Func<T> create)
            {
                return new SnapshotRestoreContext<T>(create());
            }

            public void Dispose()
            {
                snapshot.Dispose();
                snapshot = default;
            }
        }

        private sealed class DeltaStoreContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            private readonly BitPacker[] _stored;
            private T _oldValue;
            private T _newValue;

            private DeltaStoreContext(BitPacker[] stored, T oldValue, T newValue)
            {
                _stored = stored;
                _oldValue = oldValue;
                _newValue = newValue;
            }

            public static DeltaStoreContext<T> Create(int iterations, Func<T> createOld, Func<T> createNew)
            {
                return new DeltaStoreContext<T>(new BitPacker[iterations], createOld(), createNew());
            }

            public void Store(int index)
            {
                var packer = BitPackerPool.Get();
                DeltaPacker<T>.Write(packer, _oldValue, _newValue);
                _intSink ^= packer.positionInBits;
                _stored[index] = packer;
            }

            public void Dispose()
            {
                for (int i = 0; i < _stored.Length; i++)
                {
                    _stored[i]?.Dispose();
                    _stored[i] = null;
                }

                _oldValue.Dispose();
                _newValue.Dispose();
                _oldValue = default;
                _newValue = default;
            }
        }

        private sealed class DeltaApplyContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            private T _oldValue;
            private BitPacker _delta;

            private DeltaApplyContext(T oldValue, BitPacker delta)
            {
                _oldValue = oldValue;
                _delta = delta;
            }

            public static DeltaApplyContext<T> Create(Func<T> createOld, Func<T> createNew)
            {
                var oldValue = createOld();
                var newValue = createNew();
                var delta = BitPackerPool.Get();
                DeltaPacker<T>.Write(delta, oldValue, newValue);
                newValue.Dispose();
                return new DeltaApplyContext<T>(oldValue, delta);
            }

            public void Apply()
            {
                _delta.ResetPositionAndMode(true);
                T restored = default;
                DeltaPacker<T>.Read(_delta, _oldValue, ref restored);
                _intSink ^= _delta.positionInBits;
                restored.Dispose();
            }

            public void Dispose()
            {
                _oldValue.Dispose();
                _delta?.Dispose();
                _oldValue = default;
                _delta = null;
            }
        }

        private sealed class DeltaReplayContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            private T _baseline;
            private readonly BitPacker[] _deltas;

            private DeltaReplayContext(T baseline, BitPacker[] deltas)
            {
                _baseline = baseline;
                _deltas = deltas;
            }

            public static DeltaReplayContext<T> Create(int replayDeltas, Func<T> createA, Func<T> createB)
            {
                var baseline = createA();
                var current = PurrCopy<T>.Copy(baseline);
                var deltas = new BitPacker[replayDeltas];

                for (int i = 0; i < replayDeltas; i++)
                {
                    var next = ((i & 1) == 0 ? createB : createA)();
                    var delta = BitPackerPool.Get();
                    DeltaPacker<T>.Write(delta, current, next);
                    deltas[i] = delta;
                    current.Dispose();
                    current = next;
                }

                current.Dispose();
                return new DeltaReplayContext<T>(baseline, deltas);
            }

            public void Replay()
            {
                var current = PurrCopy<T>.Copy(_baseline);
                for (int i = 0; i < _deltas.Length; i++)
                {
                    _deltas[i].ResetPositionAndMode(true);
                    T next = default;
                    DeltaPacker<T>.Read(_deltas[i], current, ref next);
                    current.Dispose();
                    current = next;
                }

                _intSink ^= _deltas.Length;
                current.Dispose();
            }

            public void Dispose()
            {
                _baseline.Dispose();
                for (int i = 0; i < _deltas.Length; i++)
                {
                    _deltas[i]?.Dispose();
                    _deltas[i] = null;
                }
                _baseline = default;
            }
        }
    }
}
