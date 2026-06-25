using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    public static class PredictionHistoryBenchmarkRunner
    {
        private const int SampleCount = 5;
        private const int WarmupIterations = 8;
        private const string DefaultOutputDirectory = "Temp/PurrDictionHistoryBenchmarks";

        private static bool _boolSink;
        private static int _intSink;
        private static string _currentTypeName;

        [MenuItem("Tools/PurrDiction/Analysis/Run History Benchmarks", false, -90)]
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

        public static BenchmarkReport Run()
        {
            var outputDirectory = GetArgument("-purrdictionBenchmarkOutput") ?? DefaultOutputDirectory;
            Directory.CreateDirectory(outputDirectory);

            NetworkManager.LoadOrGenerateHashes();

            var report = new BenchmarkReport
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                projectPath = Directory.GetCurrentDirectory(),
                outputDirectory = Path.GetFullPath(outputDirectory)
            };

            var jsonPath = Path.Combine(outputDirectory, "prediction-history-benchmarks.json");
            var markdownPath = Path.Combine(outputDirectory, "prediction-history-benchmarks.md");

            report.jsonPath = Path.GetFullPath(jsonPath);
            report.markdownPath = Path.GetFullPath(markdownPath);

            BenchmarkType(
                report,
                "PredictedTransformState",
                CreatePredictedTransformState,
                CreateChangedPredictedTransformState,
                10000);
            WriteReports(report);

#if UNITY_PHYSICS_3D
            BenchmarkType(
                report,
                "UnityRigidbodyState",
                CreateUnityRigidbodyState,
                CreateChangedUnityRigidbodyState,
                10000);
            WriteReports(report);
#else
            report.warnings.Add("UNITY_PHYSICS_3D is not defined; skipped UnityRigidbodyState.");
#endif

            BenchmarkTypeWithDuplicate(
                report,
                "PredictedHierarchyState",
                () => CreatePredictedHierarchyState(32, 8),
                () => CreatePredictedHierarchyState(33, 9),
                1000);
            WriteReports(report);

            BenchmarkTypeWithDuplicate(
                report,
                "PredictedPhysicsData",
                () => CreatePredictedPhysicsData(16),
                () => CreatePredictedPhysicsData(17),
                500);
            WriteReports(report);

            BenchmarkTypeWithDuplicate(
                report,
                "ProjectileState3D",
                () => CreateProjectileState(8),
                () => CreateProjectileState(9),
                1000);
            WriteReports(report);

            BenchmarkTypeWithDuplicate(
                report,
                "FallingSandState_10",
                () => CreateFallingSandState(10, 10),
                () => CreateFallingSandState(10, 11),
                1000);
            WriteReports(report);

            BenchmarkTypeWithDuplicate(
                report,
                "FallingSandState_64",
                () => CreateFallingSandState(64, 64),
                () => CreateFallingSandState(64, 65),
                128);
            WriteReports(report);

            BenchmarkTypeWithDuplicate(
                report,
                "FallingSandState_128",
                () => CreateFallingSandState(128, 128),
                () => CreateFallingSandState(128, 129),
                16);
            WriteReports(report);

            BenchmarkTypeWithDuplicate(
                report,
                "FallingSandState_256",
                () => CreateFallingSandState(256, 256),
                () => CreateFallingSandState(256, 257),
                4);
            WriteReports(report);

            Debug.Log($"PurrDiction history benchmarks wrote:\n{report.jsonPath}\n{report.markdownPath}");
            return report;
        }

        private static void WriteReports(BenchmarkReport report)
        {
            File.WriteAllText(report.jsonPath, BuildJson(report));
            File.WriteAllText(report.markdownPath, BuildMarkdown(report));
        }

        private static void BenchmarkType<T>(
            BenchmarkReport report,
            string name,
            Func<T> create,
            Func<T> createDifferent,
            int iterations) where T : struct, IPredictedData<T>
        {
            _currentTypeName = name;
            Debug.Log($"PurrDiction history benchmark: starting {name} ({iterations} iterations).");
            var result = CreateTypeResult<T>(name);
            AddCommonOperations(result, create, createDifferent, iterations);
            report.types.Add(result);
            Debug.Log($"PurrDiction history benchmark: finished {name}.");
        }

        private static void BenchmarkTypeWithDuplicate<T>(
            BenchmarkReport report,
            string name,
            Func<T> create,
            Func<T> createDifferent,
            int iterations) where T : struct, IPredictedData<T>, IDuplicate<T>
        {
            _currentTypeName = name;
            Debug.Log($"PurrDiction history benchmark: starting {name} ({iterations} iterations).");
            var result = CreateTypeResult<T>(name);
            AddCommonOperations(result, create, createDifferent, iterations);
            result.operations.Insert(1, MeasureDirectDuplicate(name, iterations, create));
            report.types.Add(result);
            Debug.Log($"PurrDiction history benchmark: finished {name}.");
        }

        private static TypeBenchmarkResult CreateTypeResult<T>(string name)
        {
            var copyMethod = PurrCopy<T>.Copy?.Method;
            return new TypeBenchmarkResult
            {
                name = name,
                stateType = typeof(T).FullName,
                containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>(),
                implementsIDuplicate = typeof(IDuplicate<T>).IsAssignableFrom(typeof(T)),
                implementsIPurrEquatable = typeof(IPurrEquatable<T>).IsAssignableFrom(typeof(T)),
                hasPacker = Packer<T>.HasPacker(),
                hasDeltaPacker = DeltaPacker<T>.HasPacker(),
                copyDelegate = FormatMethod(copyMethod)
            };
        }

        private static void AddCommonOperations<T>(
            TypeBenchmarkResult result,
            Func<T> create,
            Func<T> createDifferent,
            int iterations) where T : struct, IPredictedData<T>
        {
            result.operations.Add(MeasurePurrCopy(result.name, iterations, create));
            result.operations.Add(MeasureExplicitPackerCopy(result.name, iterations, create));
            result.operations.Add(MeasureFullStateDeepCopy(result.name, iterations, create));
            result.operations.Add(MeasureAreEqualRef(result.name, iterations, create));
            result.operations.Add(MeasureHistoryWritePreCopied(result.name, iterations, create));
            result.operations.Add(MeasureStatefulSaveUnchanged(result.name, iterations, create));
            result.operations.Add(MeasureStatefulSavePredictionChanged(result.name, iterations, create));
            result.operations.Add(MeasureStatefulSaveStateChanged(result.name, iterations, create, createDifferent));
            result.operations.Add(MeasureDeterministicSave(result.name, iterations, create));
            result.operations.Add(MeasurePackerWriteRead(result.name, iterations, create));
            result.operations.Add(MeasureDeltaWriteRead(result.name, "DeltaPacker.WriteRead.Equal", iterations, create, create));
            result.operations.Add(MeasureDeltaWriteRead(result.name, "DeltaPacker.WriteRead.Changed", iterations, create, createDifferent));
        }

        private static OperationResult MeasurePurrCopy<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "PurrCopy.Copy",
                iterations,
                () => new ValueContext<T>(create()),
                (ctx, _) =>
                {
                    var copy = PurrCopy<T>.Copy(ctx.value);
                    copy.Dispose();
                },
                ctx => ctx.Dispose(),
                note: $"Delegate: {FormatMethod(PurrCopy<T>.Copy?.Method)}");
        }

        private static OperationResult MeasureDirectDuplicate<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>, IDuplicate<T>
        {
            return MeasureOperation(
                "IDuplicate.Duplicate",
                iterations,
                () => new ValueContext<T>(create()),
                (ctx, _) =>
                {
                    var copy = ctx.value.Duplicate();
                    copy.Dispose();
                },
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasureExplicitPackerCopy<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "ExplicitPackerCopy.WriteRead",
                iterations,
                () => new ValueContext<T>(create()),
                (ctx, _) =>
                {
                    var copy = PackerRoundTripCopy(ctx.value);
                    copy.Dispose();
                },
                ctx => ctx.Dispose(),
                MeasurePackerBits(create));
        }

        private static OperationResult MeasureFullStateDeepCopy<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "FULL_STATE.DeepCopy",
                iterations,
                () => new FullStateContext<T>(CreateFullState(create())),
                (ctx, _) =>
                {
                    var copy = ctx.fullState.DeepCopy();
                    copy.Dispose();
                },
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasureAreEqualRef<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "Packer.AreEqualRef",
                iterations,
                () =>
                {
                    var left = create();
                    var right = PurrCopy<T>.Copy(left);
                    return new EqualityContext<T>(left, right);
                },
                (ctx, _) => _boolSink ^= Packer.AreEqualRef(ref ctx.left, ref ctx.right),
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasureHistoryWritePreCopied<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "History.Write.PreCopied",
                iterations,
                () => HistoryWriteContext<T>.Create(iterations, create),
                (ctx, i) => ctx.Write(i),
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasureStatefulSaveUnchanged<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "StatefulSave.Unchanged",
                iterations,
                () => SaveContext<T>.Create(create, iterations + 1),
                (ctx, i) =>
                {
                    if (ctx.history.Count > 0)
                    {
                        var last = ctx.history[ctx.history.Count - 1];
                        if (Packer.AreEqualRef(ref last.prediction, ref ctx.fullState.prediction) &&
                            Packer.AreEqualRef(ref last.state, ref ctx.fullState.state))
                            return;
                    }

                    ctx.history.Write((ulong)i + 1, ctx.fullState.DeepCopy());
                },
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasureStatefulSavePredictionChanged<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "StatefulSave.PredictionChanged",
                iterations,
                () => SaveContext<T>.Create(create, iterations + 1),
                (ctx, i) =>
                {
                    ctx.fullState.prediction.wasOnSimulationStartCalled =
                        !ctx.fullState.prediction.wasOnSimulationStartCalled;

                    if (ctx.history.Count > 0)
                    {
                        var last = ctx.history[ctx.history.Count - 1];
                        if (Packer.AreEqualRef(ref last.prediction, ref ctx.fullState.prediction) &&
                            Packer.AreEqualRef(ref last.state, ref ctx.fullState.state))
                            return;
                    }

                    ctx.history.Write((ulong)i + 1, ctx.fullState.DeepCopy());
                },
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasureStatefulSaveStateChanged<T>(
            string typeName,
            int iterations,
            Func<T> create,
            Func<T> createDifferent) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "StatefulSave.StateChanged",
                iterations,
                () => AlternatingSaveContext<T>.Create(create, createDifferent, iterations + 1),
                (ctx, i) =>
                {
                    ref var fullState = ref ctx.GetAlternatingState(i);

                    if (ctx.history.Count > 0)
                    {
                        var last = ctx.history[ctx.history.Count - 1];
                        if (Packer.AreEqualRef(ref last.prediction, ref fullState.prediction) &&
                            Packer.AreEqualRef(ref last.state, ref fullState.state))
                            return;
                    }

                    ctx.history.Write((ulong)i + 1, fullState.DeepCopy());
                },
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasureDeterministicSave<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "DeterministicSave.AlwaysCopyWrite",
                iterations,
                () => SaveContext<T>.Create(create, iterations + 1),
                (ctx, i) => ctx.history.Write((ulong)i + 1, ctx.fullState.DeepCopy()),
                ctx => ctx.Dispose());
        }

        private static OperationResult MeasurePackerWriteRead<T>(
            string typeName,
            int iterations,
            Func<T> create) where T : struct, IPredictedData<T>
        {
            return MeasureOperation(
                "Packer.WriteRead",
                iterations,
                () => new ValueContext<T>(create()),
                (ctx, _) =>
                {
                    using var packer = BitPackerPool.Get();
                    Packer<T>.Write(packer, ctx.value);
                    _intSink ^= packer.positionInBits;
                    packer.ResetPositionAndMode(true);

                    T read = default;
                    Packer<T>.Read(packer, ref read);
                    read.Dispose();
                },
                ctx => ctx.Dispose(),
                MeasurePackerBits(create));
        }

        private static OperationResult MeasureDeltaWriteRead<T>(
            string typeName,
            string operationName,
            int iterations,
            Func<T> createOld,
            Func<T> createNew) where T : struct, IPredictedData<T>
        {
            if (!DeltaPacker<T>.HasPacker())
            {
                return OperationResult.Skipped(
                    operationName,
                    iterations,
                    "DeltaPacker<T>.HasPacker() is false; skipped to avoid measuring fallback aliasing behavior.");
            }

            return MeasureOperation(
                operationName,
                iterations,
                () => new DeltaContext<T>(createOld(), createNew()),
                (ctx, _) =>
                {
                    using var packer = BitPackerPool.Get();
                    DeltaPacker<T>.Write(packer, ctx.oldValue, ctx.newValue);
                    _intSink ^= packer.positionInBits;
                    packer.ResetPositionAndMode(true);

                    T read = default;
                    DeltaPacker<T>.Read(packer, ctx.oldValue, ref read);
                    read.Dispose();
                },
                ctx => ctx.Dispose(),
                MeasureDeltaBits(createOld, createNew));
        }

        private static OperationResult MeasureOperation<TContext>(
            string name,
            int iterations,
            Func<TContext> setup,
            Action<TContext, int> body,
            Action<TContext> cleanup,
            long serializedBits = -1,
            string note = null) where TContext : class
        {
            Debug.Log($"PurrDiction history benchmark: measuring {_currentTypeName}.{name}.");

            if (iterations <= 0)
                return OperationResult.Skipped(name, iterations, "Iteration count must be positive.");

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

                var result = OperationResult.FromSamples(name, iterations, timings, allocations, serializedBits, note);
                Debug.Log($"PurrDiction history benchmark: measured {_currentTypeName}.{name} = {FormatNumber(result.medianNanoseconds)} ns/op, {FormatNumber(result.medianAllocatedBytes)} B/op.");
                return result;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"PurrDiction history benchmark: skipped {_currentTypeName}.{name}: {e.GetType().Name}: {e.Message}");
                return OperationResult.Skipped(name, iterations, $"{e.GetType().Name}: {e.Message}");
            }
        }

        private static FULL_STATE<T> CreateFullState<T>(T state) where T : struct, IPredictedData<T>
        {
            return new FULL_STATE<T>
            {
                state = state,
                prediction = new PredictedIdentityState
                {
                    wasOnSimulationStartCalled = false
                }
            };
        }

        private static T PackerRoundTripCopy<T>(T value)
        {
            using var packer = BitPackerPool.Get();
            Packer<T>.Write(packer, value);
            packer.ResetPositionAndMode(true);

            T copy = default;
            Packer<T>.Read(packer, ref copy);
            return copy;
        }

        private static long MeasurePackerBits<T>(Func<T> create) where T : struct, IPredictedData<T>
        {
            T value = default;
            try
            {
                value = create();
                using var packer = BitPackerPool.Get();
                Packer<T>.Write(packer, value);
                return packer.positionInBits;
            }
            catch
            {
                return -1;
            }
            finally
            {
                value.Dispose();
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

#if UNITY_PHYSICS_3D
        private static UnityRigidbodyState CreateUnityRigidbodyState()
        {
            return new UnityRigidbodyState
            {
                linearVelocity = new Vector3(1f, 2f, 3f),
                angularVelocity = new Vector3(4f, 5f, 6f),
                isKinematic = false,
                isSleeping = false,
                useGravity = true
            };
        }

        private static UnityRigidbodyState CreateChangedUnityRigidbodyState()
        {
            return new UnityRigidbodyState
            {
                linearVelocity = new Vector3(2f, 2f, 3f),
                angularVelocity = new Vector3(4f, 5.5f, 6f),
                isKinematic = false,
                isSleeping = true,
                useGravity = true
            };
        }
#endif

        private static PredictedHierarchyState CreatePredictedHierarchyState(int spawnedCount, int deleteCount)
        {
            var spawned = DisposableList<InstanceDetails>.Create(spawnedCount);
            for (int i = 0; i < spawnedCount; i++)
            {
                spawned.Add(new InstanceDetails(
                    i % 7,
                    new PredictedObjectID((uint)i + 2),
                    new Vector3(i, i * 0.25f, i * 0.5f),
                    Quaternion.Euler(i, i * 2f, i * 3f),
                    null));
            }

            var toDelete = DisposableList<PredictedObjectID>.Create(deleteCount);
            for (int i = 0; i < deleteCount; i++)
                toDelete.Add(new PredictedObjectID((uint)i + 1000));

            return new PredictedHierarchyState(spawned, toDelete, (uint)(spawnedCount + 2));
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

        private static string FormatMethod(MethodInfo method)
        {
            if (method == null)
                return "<null>";

            var declaringType = method.DeclaringType == null ? "<unknown>" : method.DeclaringType.FullName;
            return $"{declaringType}.{method.Name}";
        }

        private static string BuildMarkdown(BenchmarkReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# PurrDiction History Benchmarks");
            sb.AppendLine();
            sb.AppendLine($"Generated: `{report.generatedAtUtc}`");
            sb.AppendLine($"Unity: `{report.unityVersion}`");
            sb.AppendLine($"Project: `{report.projectPath}`");
            sb.AppendLine();

            if (report.warnings.Count > 0)
            {
                sb.AppendLine("## Warnings");
                foreach (var warning in report.warnings)
                    sb.AppendLine($"- {warning}");
                sb.AppendLine();
            }

            sb.AppendLine("## Ranked Operations");
            sb.AppendLine();
            sb.AppendLine("| Rank | Type | Operation | Median ns/op | Alloc B/op | Bits | Notes |");
            sb.AppendLine("|---:|---|---|---:|---:|---:|---|");

            var ranked = new List<(TypeBenchmarkResult type, OperationResult op)>();
            foreach (var type in report.types)
            {
                foreach (var op in type.operations)
                {
                    if (!op.skipped)
                        ranked.Add((type, op));
                }
            }

            ranked.Sort((a, b) => b.op.medianNanoseconds.CompareTo(a.op.medianNanoseconds));
            for (int i = 0; i < ranked.Count && i < 30; i++)
            {
                var item = ranked[i];
                sb.Append("| ");
                sb.Append(i + 1);
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(item.type.name));
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(item.op.name));
                sb.Append(" | ");
                sb.Append(FormatNumber(item.op.medianNanoseconds));
                sb.Append(" | ");
                sb.Append(FormatNumber(item.op.medianAllocatedBytes));
                sb.Append(" | ");
                sb.Append(item.op.serializedBits >= 0 ? item.op.serializedBits.ToString() : "");
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(item.op.notes));
                sb.AppendLine(" |");
            }

            AppendSaveHistoryQuestions(sb, report);

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
                sb.AppendLine($"- Implements `IPurrEquatable<T>`: `{type.implementsIPurrEquatable}`");
                sb.AppendLine($"- `Packer<T>.HasPacker()`: `{type.hasPacker}`");
                sb.AppendLine($"- `DeltaPacker<T>.HasPacker()`: `{type.hasDeltaPacker}`");
                sb.AppendLine($"- `PurrCopy<T>.Copy`: `{type.copyDelegate}`");
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
            sb.AppendLine("Unity.exe -batchmode -projectPath C:\\wkspaces\\unity\\riten\\PurrDiction -executeMethod PurrNet.Prediction.Benchmarks.Editor.PredictionHistoryBenchmarkRunner.RunFromCommandLine -quit -logFile Temp\\PurrDictionHistoryBenchmarks\\benchmark.log");
            sb.AppendLine("```");
            return sb.ToString();
        }

        private static void AppendSaveHistoryQuestions(StringBuilder sb, BenchmarkReport report)
        {
            sb.AppendLine();
            sb.AppendLine("## SaveHistory Questions");
            sb.AppendLine();
            sb.AppendLine("| Type | Dominant local save component | PurrCopy path | Equality vs copy | Delta vs local copy |");
            sb.AppendLine("|---|---|---|---|---|");

            foreach (var type in report.types)
            {
                var equality = FindOperation(type, "Packer.AreEqualRef");
                var copy = FindOperation(type, "PurrCopy.Copy");
                var deepCopy = FindOperation(type, "FULL_STATE.DeepCopy");
                var historyWrite = FindOperation(type, "History.Write.PreCopied");
                var deltaChanged = FindOperation(type, "DeltaPacker.WriteRead.Changed");

                sb.Append("| ");
                sb.Append(EscapeMarkdown(type.name));
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(DescribeDominantSaveComponent(equality, deepCopy, historyWrite)));
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(DescribeCopyPath(type)));
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(DescribeComparison(equality, copy)));
                sb.Append(" | ");
                sb.Append(EscapeMarkdown(DescribeComparison(deltaChanged, deepCopy)));
                sb.AppendLine(" |");
            }
        }

        private static OperationResult FindOperation(TypeBenchmarkResult type, string name)
        {
            for (int i = 0; i < type.operations.Count; i++)
            {
                if (type.operations[i].name == name)
                    return type.operations[i];
            }

            return null;
        }

        private static string DescribeDominantSaveComponent(
            OperationResult equality,
            OperationResult deepCopy,
            OperationResult historyWrite)
        {
            OperationResult best = null;

            if (IsMeasured(equality))
                best = equality;
            if (IsMeasured(deepCopy) && (best == null || deepCopy.medianNanoseconds > best.medianNanoseconds))
                best = deepCopy;
            if (IsMeasured(historyWrite) && (best == null || historyWrite.medianNanoseconds > best.medianNanoseconds))
                best = historyWrite;

            if (best == null)
                return "No component measurement";

            return $"{best.name} ({FormatNumber(best.medianNanoseconds)} ns/op)";
        }

        private static string DescribeCopyPath(TypeBenchmarkResult type)
        {
            if (type.copyDelegate.Contains("StructShortcut"))
                return "value copy shortcut";
            if (type.copyDelegate.Contains("Fallback"))
                return "packer round-trip fallback";
            if (type.copyDelegate.Contains("CopyMethod") && type.implementsIDuplicate)
                return "IDuplicate hook";
            return type.copyDelegate;
        }

        private static string DescribeComparison(OperationResult left, OperationResult right)
        {
            if (!IsMeasured(left))
                return left == null ? "missing" : $"skipped: {left.notes}";
            if (!IsMeasured(right))
                return right == null ? "missing baseline" : $"baseline skipped: {right.notes}";

            var ratio = left.medianNanoseconds / Math.Max(0.0001, right.medianNanoseconds);
            var relation = ratio <= 1 ? "faster" : "slower";
            return $"{FormatNumber(ratio)}x {relation}";
        }

        private static bool IsMeasured(OperationResult operation)
        {
            return operation != null && !operation.skipped;
        }

        private static string BuildJson(BenchmarkReport report)
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
            sb.AppendLine("\"warnings\": [");
            for (int i = 0; i < report.warnings.Count; i++)
            {
                Indent(sb, 2);
                AppendJsonString(sb, report.warnings[i]);
                sb.AppendLine(i == report.warnings.Count - 1 ? "" : ",");
            }
            Indent(sb, 1);
            sb.AppendLine("],");

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

        private static void AppendTypeJson(StringBuilder sb, TypeBenchmarkResult type, int indent)
        {
            Indent(sb, indent);
            sb.AppendLine("{");
            AppendJsonField(sb, "name", type.name, indent + 1, comma: true);
            AppendJsonField(sb, "stateType", type.stateType, indent + 1, comma: true);
            AppendJsonField(sb, "copyDelegate", type.copyDelegate, indent + 1, comma: true);
            AppendJsonField(sb, "containsReferences", type.containsReferences, indent + 1, comma: true);
            AppendJsonField(sb, "implementsIDuplicate", type.implementsIDuplicate, indent + 1, comma: true);
            AppendJsonField(sb, "implementsIPurrEquatable", type.implementsIPurrEquatable, indent + 1, comma: true);
            AppendJsonField(sb, "hasPacker", type.hasPacker, indent + 1, comma: true);
            AppendJsonField(sb, "hasDeltaPacker", type.hasDeltaPacker, indent + 1, comma: true);

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

        private static void AppendOperationJson(StringBuilder sb, OperationResult op, int indent)
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
        public sealed class BenchmarkReport
        {
            public string generatedAtUtc;
            public string unityVersion;
            public string projectPath;
            public string outputDirectory;
            public string jsonPath;
            public string markdownPath;
            public readonly List<string> warnings = new();
            public readonly List<TypeBenchmarkResult> types = new();
        }

        [Serializable]
        public sealed class TypeBenchmarkResult
        {
            public string name;
            public string stateType;
            public string copyDelegate;
            public bool containsReferences;
            public bool implementsIDuplicate;
            public bool implementsIPurrEquatable;
            public bool hasPacker;
            public bool hasDeltaPacker;
            public readonly List<OperationResult> operations = new();
        }

        [Serializable]
        public sealed class OperationResult
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

            public static OperationResult Skipped(string name, int iterations, string notes)
            {
                return new OperationResult
                {
                    name = name,
                    iterations = iterations,
                    samples = 0,
                    skipped = true,
                    serializedBits = -1,
                    notes = notes
                };
            }

            public static OperationResult FromSamples(
                string name,
                int iterations,
                double[] timings,
                double[] allocations,
                long serializedBits,
                string notes)
            {
                return new OperationResult
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

        private sealed class ValueContext<T> : IDisposable where T : struct, IDisposable
        {
            public T value;

            public ValueContext(T value)
            {
                this.value = value;
            }

            public void Dispose()
            {
                value.Dispose();
                value = default;
            }
        }

        private sealed class EqualityContext<T> : IDisposable where T : struct, IDisposable
        {
            public T left;
            public T right;

            public EqualityContext(T left, T right)
            {
                this.left = left;
                this.right = right;
            }

            public void Dispose()
            {
                left.Dispose();
                right.Dispose();
                left = default;
                right = default;
            }
        }

        private sealed class DeltaContext<T> : IDisposable where T : struct, IDisposable
        {
            public T oldValue;
            public T newValue;

            public DeltaContext(T oldValue, T newValue)
            {
                this.oldValue = oldValue;
                this.newValue = newValue;
            }

            public void Dispose()
            {
                oldValue.Dispose();
                newValue.Dispose();
                oldValue = default;
                newValue = default;
            }
        }

        private sealed class FullStateContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            public FULL_STATE<T> fullState;

            public FullStateContext(FULL_STATE<T> fullState)
            {
                this.fullState = fullState;
            }

            public void Dispose()
            {
                fullState.Dispose();
                fullState = default;
            }
        }

        private sealed class HistoryWriteContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            private readonly FULL_STATE<T>[] _values;
            private readonly History<FULL_STATE<T>> _history;
            private FULL_STATE<T> _prototype;

            private HistoryWriteContext(FULL_STATE<T>[] values, FULL_STATE<T> prototype)
            {
                _values = values;
                _prototype = prototype;
                _history = new History<FULL_STATE<T>>(values.Length + 1);
            }

            public static HistoryWriteContext<T> Create(int iterations, Func<T> create)
            {
                var prototype = CreateFullState(create());
                var values = new FULL_STATE<T>[iterations];

                for (int i = 0; i < iterations; i++)
                    values[i] = prototype.DeepCopy();

                return new HistoryWriteContext<T>(values, prototype);
            }

            public void Write(int index)
            {
                _history.Write((ulong)index + 1, _values[index]);
                _values[index] = default;
            }

            public void Dispose()
            {
                _history.Clear();
                for (int i = 0; i < _values.Length; i++)
                {
                    _values[i].Dispose();
                    _values[i] = default;
                }

                _prototype.Dispose();
                _prototype = default;
            }
        }

        private sealed class SaveContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            public FULL_STATE<T> fullState;
            public readonly History<FULL_STATE<T>> history;

            private SaveContext(FULL_STATE<T> fullState, int historyCapacity)
            {
                this.fullState = fullState;
                history = new History<FULL_STATE<T>>(historyCapacity);
                history.Write(0, fullState.DeepCopy());
            }

            public static SaveContext<T> Create(Func<T> create, int historyCapacity)
            {
                return new SaveContext<T>(CreateFullState(create()), historyCapacity);
            }

            public void Dispose()
            {
                history.Clear();
                fullState.Dispose();
                fullState = default;
            }
        }

        private sealed class AlternatingSaveContext<T> : IDisposable where T : struct, IPredictedData<T>
        {
            private FULL_STATE<T> _stateA;
            private FULL_STATE<T> _stateB;
            public readonly History<FULL_STATE<T>> history;

            private AlternatingSaveContext(FULL_STATE<T> stateA, FULL_STATE<T> stateB, int historyCapacity)
            {
                _stateA = stateA;
                _stateB = stateB;
                history = new History<FULL_STATE<T>>(historyCapacity);
                history.Write(0, _stateB.DeepCopy());
            }

            public static AlternatingSaveContext<T> Create(
                Func<T> create,
                Func<T> createDifferent,
                int historyCapacity)
            {
                return new AlternatingSaveContext<T>(
                    CreateFullState(create()),
                    CreateFullState(createDifferent()),
                    historyCapacity);
            }

            public ref FULL_STATE<T> GetAlternatingState(int index)
            {
                if ((index & 1) == 0)
                    return ref _stateA;
                return ref _stateB;
            }

            public void Dispose()
            {
                history.Clear();
                _stateA.Dispose();
                _stateB.Dispose();
                _stateA = default;
                _stateB = default;
            }
        }
    }
}
