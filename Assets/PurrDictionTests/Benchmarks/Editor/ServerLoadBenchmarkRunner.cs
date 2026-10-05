using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    /// <summary>
    /// Builds the PredictionTests player once and runs the ServerLoadBenchmarkScenario
    /// across a configurable client/object/visibility/latency matrix, collecting server
    /// frame-write, acknowledgement-lag, and bandwidth measurements for each case.
    /// </summary>
    public static class ServerLoadBenchmarkRunner
    {
        private const string BootstrapScene = "Assets/PredictionTests/Bootstrap.unity";
        private const string DefaultOutputDirectory = "Builds/ServerLoadBenchmark";
        private const string DefaultLatencies = "0,50,100,200";
        private const string DefaultVisibilityModes = "none";
        private const int DefaultClientCount = 3;
        private const int DefaultObjects = 200;
        private const int DefaultSeconds = 20;
        private const int DefaultRunTimeoutSeconds = 420;
        private const int DefaultMaxRuns = 32;
        private const int DefaultVisibilityPercent = 25;
        private const int DefaultVisibilityChurnPercent = 10;
        private const int DefaultVisibilityChurnTicks = 30;
        private const int DefaultDeleteChurnPerSecond = 10;

        [MenuItem("Tools/PurrDiction/Analysis/Run Server Load Latency Sweep", false, -86)]
        public static void RunFromMenu()
        {
            RunInternal(exitEditor: false);
        }

        [MenuItem("Tools/PurrDiction/Analysis/Run Server Load Visibility Sweep", false, -85)]
        public static void RunVisibilityPresetFromMenu()
        {
            RunInternal(exitEditor: false, ConfigureVisibilityPreset);
        }

        public static void RunFromCommandLine()
        {
            RunInternal(exitEditor: true);
        }

        private static void ConfigureVisibilityPreset(Options options)
        {
            options.outputDirectory = Path.Combine(
                options.outputDirectory,
                "VisibilityPreset");
            options.clientCounts = new[]
            {
                Math.Max(1, GetIntArgument("-slbClients", DefaultClientCount))
            };
            options.objectCounts = new[]
            {
                Math.Max(1, GetIntArgument("-slbObjects", DefaultObjects))
            };
            options.latenciesMs = new[] { 0 };
            options.visibilityModes = new[] { "none", "static", "churn", "acquire-churn" };
            options.maxRuns = Math.Max(options.maxRuns, 4);
        }

        private static void RunInternal(
            bool exitEditor,
            Action<Options> configure = null)
        {
            var exitCode = 0;

            try
            {
                var options = Options.FromCommandLine();
                configure?.Invoke(options);
                var success = Run(options);
                exitCode = success ? 0 : -1;
            }
            catch (Exception e)
            {
                exitCode = -1;
                Debug.LogException(e);
            }
            finally
            {
                if (exitEditor || Application.isBatchMode)
                    EditorApplication.Exit(exitCode);
            }
        }

        private static bool Run(Options options)
        {
            Directory.CreateDirectory(options.outputDirectory);

            var runCount = ValidateRunCountAndPorts(options);
            var cases = BuildCases(options, runCount);

            var playerPath = options.playerPath;
            if (string.IsNullOrEmpty(playerPath))
                playerPath = Path.Combine(options.outputDirectory, "Player", "PurrDictionTests.exe");

            if (!options.skipBuild)
                BuildPlayer(playerPath);

            var rows = new List<SweepRow>(cases.Count);
            var success = true;

            for (var i = 0; i < cases.Count; i++)
            {
                var sweepCase = cases[i];
                Debug.Log(
                    $"[ServerLoadBenchmark] Running {i + 1}/{cases.Count}: " +
                    $"{sweepCase.clientCount} clients, {sweepCase.objects} objects, " +
                    $"{sweepCase.latencyMs}ms, visibility={sweepCase.visibilityMode}");

                SweepRow row;
                try
                {
                    row = RunOneCase(playerPath, options, sweepCase, i);
                }
                catch (Exception exception)
                {
                    row = CreateSweepRow(options, sweepCase, i);
                    AppendExceptionFailure(row, "case", exception);
                }

                rows.Add(row);
                success &= row.success;
            }

            WriteReport(options, playerPath, rows, success);
            return success;
        }

        private static List<SweepCase> BuildCases(Options options, int runCount)
        {
            var cases = new List<SweepCase>(runCount);

            for (var clientIndex = 0; clientIndex < options.clientCounts.Length; clientIndex++)
            {
                for (var objectIndex = 0; objectIndex < options.objectCounts.Length; objectIndex++)
                {
                    for (var visibilityIndex = 0; visibilityIndex < options.visibilityModes.Length; visibilityIndex++)
                    {
                        for (var latencyIndex = 0; latencyIndex < options.latenciesMs.Length; latencyIndex++)
                        {
                            cases.Add(new SweepCase
                            {
                                clientCount = options.clientCounts[clientIndex],
                                objects = options.objectCounts[objectIndex],
                                visibilityMode = options.visibilityModes[visibilityIndex],
                                latencyMs = options.latenciesMs[latencyIndex]
                            });
                        }
                    }
                }
            }

            return cases;
        }

        private static int ValidateRunCountAndPorts(Options options)
        {
            long runCount = options.clientCounts.Length;
            runCount *= options.objectCounts.Length;
            runCount *= options.visibilityModes.Length;
            runCount *= options.latenciesMs.Length;

            if (runCount > options.maxRuns)
            {
                throw new InvalidOperationException(
                    $"Server load benchmark matrix expands to {runCount} runs, above the " +
                    $"-slbMaxRuns limit of {options.maxRuns}. Reduce a CSV dimension or pass " +
                    $"-slbMaxRuns {runCount} explicitly.");
            }

            long finalPort = (long)options.basePort + Math.Max(0, runCount - 1);
            if (options.basePort < 1 || finalPort > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Server load benchmark ports {options.basePort}..{finalPort} fall outside " +
                    $"the valid range 1..{ushort.MaxValue}. Choose a lower -slbBasePort.");
            }

            return checked((int)runCount);
        }

        private static void BuildPlayer(string playerPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(playerPath) ?? ".");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { BootstrapScene },
                locationPathName = playerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Server load benchmark player build failed: {report.summary.result} ({report.summary.totalErrors} errors)");
            }
        }

        private static SweepRow RunOneCase(
            string playerPath,
            Options options,
            SweepCase sweepCase,
            int runIndex)
        {
            var runDir = Path.Combine(
                options.outputDirectory,
                BuildRunDirectoryName(sweepCase, runIndex));
            var row = CreateSweepRow(options, sweepCase, runIndex);
            var processes = new List<(Process process, string role, string resultsPath)>();
            string hostResultsPath = null;

            try
            {
                Directory.CreateDirectory(runDir);

                var expectedConnections = sweepCase.clientCount + 1;
                var port = options.basePort + runIndex;
                var sharedArgs =
                    "-batchmode -nographics -serverLoadBenchmark " +
                    $"-count {expectedConnections} -port {port} -connectTimeout 180 " +
                    $"-benchObjects {sweepCase.objects} -benchSeconds {options.seconds} " +
                    $"-benchInputEvery {options.inputEvery} " +
                    $"-latencyMin {sweepCase.latencyMs} -latencyMax {sweepCase.latencyMs} " +
                    BuildVisibilityArguments(options, sweepCase.visibilityMode) +
                    (options.packetLoss > 0 ? $"-packetLoss {options.packetLoss} " : "");

                var host = StartPlayer(
                    playerPath,
                    runDir,
                    "host",
                    sharedArgs + "-role host");
                processes.Add(host);
                hostResultsPath = host.resultsPath;
                Thread.Sleep(1000);

                for (var i = 0; i < sweepCase.clientCount; i++)
                {
                    processes.Add(StartPlayer(
                        playerPath,
                        runDir,
                        $"client-{i + 1}",
                        sharedArgs + "-role client -serverHost 127.0.0.1"));
                }

                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed.TotalSeconds < options.runTimeoutSeconds)
                {
                    var allExited = true;
                    foreach (var entry in processes)
                    {
                        if (!entry.process.HasExited)
                        {
                            allExited = false;
                            break;
                        }
                    }

                    if (allExited)
                        break;

                    Thread.Sleep(250);
                }

                foreach (var entry in processes)
                {
                    if (!entry.process.HasExited)
                    {
                        row.success = false;
                        AppendFailure(row, $"{entry.role} timed out");
                    }
                    else if (entry.process.ExitCode != 0)
                    {
                        row.success = false;
                        AppendFailure(
                            row,
                            $"{entry.role} exited with {entry.process.ExitCode}");
                    }
                }
            }
            catch (Exception exception)
            {
                AppendExceptionFailure(row, "case execution", exception);
            }
            finally
            {
                CleanupProcesses(processes, row);
            }

            if (hostResultsPath == null)
                return row;

            try
            {
                var hostResult = ExtractBenchResult(hostResultsPath);
                if (hostResult.found)
                {
                    row.benchMessage = hostResult.message;
                    row.hostDataSent = hostResult.dataSent;
                    row.hostDataReceived = hostResult.dataReceived;
                    PopulateBenchMetrics(row);
                    if (!hostResult.success)
                    {
                        row.success = false;
                        AppendFailure(
                            row,
                            string.IsNullOrEmpty(hostResult.message)
                                ? "host benchmark scenario failed"
                                : $"host benchmark scenario failed: {hostResult.message}");
                    }
                    else if (string.IsNullOrWhiteSpace(hostResult.message))
                    {
                        row.success = false;
                        AppendFailure(row, "host results missing bench message");
                    }
                    else if (row.visibilityValidatedPlayers != row.clientCount + 1)
                    {
                        row.success = false;
                        AppendFailure(
                            row,
                            "final visibility validation count mismatch " +
                            $"({row.visibilityValidatedPlayers}/{row.clientCount + 1})");
                    }
                }
                else
                {
                    row.success = false;
                    AppendFailure(row, "host results missing benchmark scenario");
                }
            }
            catch (Exception exception)
            {
                AppendExceptionFailure(row, "host result read", exception);
            }

            var clientMessages = new List<string>();
            foreach (var entry in processes)
            {
                if (entry.role == "host")
                    continue;

                try
                {
                    var clientResult = ExtractBenchResult(entry.resultsPath);
                    if (!clientResult.found)
                    {
                        row.success = false;
                        AppendFailure(
                            row,
                            $"{entry.role} results missing benchmark scenario");
                    }
                    else if (!clientResult.success)
                    {
                        row.success = false;
                        AppendFailure(
                            row,
                            string.IsNullOrEmpty(clientResult.message)
                                ? $"{entry.role} benchmark scenario failed"
                                : $"{entry.role} benchmark scenario failed: {clientResult.message}");
                    }
                    else if (string.IsNullOrWhiteSpace(clientResult.message))
                    {
                        row.success = false;
                        AppendFailure(
                            row,
                            $"{entry.role} results missing client bench message");
                    }
                    else
                    {
                        clientMessages.Add(clientResult.message);
                    }
                }
                catch (Exception exception)
                {
                    AppendExceptionFailure(row, $"{entry.role} result read", exception);
                }
            }

            PopulateClientMetrics(row, clientMessages);

            return row;
        }

        private static SweepRow CreateSweepRow(
            Options options,
            SweepCase sweepCase,
            int runIndex)
        {
            var runDirectory = Path.Combine(
                options.outputDirectory,
                BuildRunDirectoryName(sweepCase, runIndex));
            return new SweepRow
            {
                runIndex = runIndex,
                latencyMs = sweepCase.latencyMs,
                clientCount = sweepCase.clientCount,
                objects = sweepCase.objects,
                visibilityMode = sweepCase.visibilityMode,
                visibilityPercent = sweepCase.visibilityMode is "none" or "deletechurn"
                    ? 100
                    : options.visibilityPercent,
                deleteChurnPerSecond = sweepCase.visibilityMode == "deletechurn"
                    ? options.deleteChurnPerSecond
                    : 0,
                visibilityChurnPercent =
                    sweepCase.visibilityMode is "churn" or "acquire-churn"
                        ? options.visibilityChurnPercent
                        : 0,
                visibilityChurnTicks =
                    sweepCase.visibilityMode is "churn" or "acquire-churn"
                        ? options.visibilityChurnTicks
                        : 0,
                runDirectory = runDirectory,
                success = true
            };
        }

        private static void CleanupProcesses(
            List<(Process process, string role, string resultsPath)> processes,
            SweepRow row)
        {
            foreach (var entry in processes)
            {
                try
                {
                    if (!entry.process.HasExited)
                        entry.process.Kill();
                }
                catch (Exception exception)
                {
                    AppendExceptionFailure(
                        row,
                        $"{entry.role} termination",
                        exception);
                }

                try
                {
                    if (!entry.process.WaitForExit(10_000))
                    {
                        row.success = false;
                        AppendFailure(
                            row,
                            $"{entry.role} did not exit within 10 seconds of cleanup");
                    }
                }
                catch (Exception exception)
                {
                    AppendExceptionFailure(
                        row,
                        $"{entry.role} exit wait",
                        exception);
                }
                finally
                {
                    try
                    {
                        entry.process.Dispose();
                    }
                    catch (Exception exception)
                    {
                        AppendExceptionFailure(
                            row,
                            $"{entry.role} disposal",
                            exception);
                    }
                }
            }
        }

        private static void AppendExceptionFailure(
            SweepRow row,
            string operation,
            Exception exception)
        {
            row.success = false;
            var failure =
                $"{operation} failed ({exception.GetType().Name}): {exception.Message}";
            AppendFailure(row, failure);
            Debug.LogWarning($"[ServerLoadBenchmark] {failure}");
        }

        private static string BuildRunDirectoryName(SweepCase sweepCase, int runIndex)
        {
            return $"run-{runIndex + 1:000}-clients-{sweepCase.clientCount}" +
                   $"-objects-{sweepCase.objects}-latency-{sweepCase.latencyMs}" +
                   $"-visibility-{sweepCase.visibilityMode}";
        }

        private static string BuildVisibilityArguments(Options options, string visibilityMode)
        {
            if (visibilityMode == "none")
                return string.Empty;

            if (visibilityMode == "deletechurn")
                return $"-benchDeleteChurn {options.deleteChurnPerSecond} ";

            return $"-benchVisibilityMode {visibilityMode} " +
                   $"-benchVisibilityPercent {options.visibilityPercent} " +
                   $"-benchVisibilityChurnPercent {options.visibilityChurnPercent} " +
                   $"-benchVisibilityChurnTicks {options.visibilityChurnTicks} ";
        }

        private static void AppendFailure(SweepRow row, string failure)
        {
            if (string.IsNullOrEmpty(row.failure))
                row.failure = failure;
            else
                row.failure += "; " + failure;
        }

        private static SweepMarker[] ParseMarkerSections(
            string message,
            out string[] header)
        {
            header = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(message))
                return Array.Empty<SweepMarker>();

            var sections = message.Split(
                new[] { " | " },
                StringSplitOptions.RemoveEmptyEntries);
            if (sections.Length == 0)
                return Array.Empty<SweepMarker>();

            header = sections[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var markers = new List<SweepMarker>(Math.Max(0, sections.Length - 1));
            for (var i = 1; i < sections.Length; i++)
            {
                var values = sections[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (values.Length == 0)
                    continue;

                markers.Add(new SweepMarker
                {
                    name = values[0],
                    totalMilliseconds = ReadDoubleMetric(values, "totalMs"),
                    sampleBlocks = ReadLongMetric(values, "blocks"),
                    microsecondsPerTick = ReadDoubleMetric(values, "perTickUs")
                });
            }

            return markers.ToArray();
        }

        private static void PopulateClientMetrics(SweepRow row, List<string> clientMessages)
        {
            row.clientBenchMessages = clientMessages.ToArray();

            var order = new List<string>();
            var totals = new Dictionary<string, SweepMarker>();
            ulong ticksSum = 0;
            double framesPerSecondSum = 0;
            var reportCount = 0;

            foreach (var message in clientMessages)
            {
                var markers = ParseMarkerSections(message, out var header);
                if (header.Length == 0)
                    continue;

                reportCount++;
                ticksSum += ReadUnsignedMetric(header, "ticks");
                framesPerSecondSum += ReadDoubleMetric(header, "framesPerSecond");

                foreach (var marker in markers)
                {
                    if (!totals.TryGetValue(marker.name, out var total))
                    {
                        total = new SweepMarker { name = marker.name };
                        totals[marker.name] = total;
                        order.Add(marker.name);
                    }

                    total.totalMilliseconds += marker.totalMilliseconds;
                    total.sampleBlocks += marker.sampleBlocks;
                    total.microsecondsPerTick += marker.microsecondsPerTick;
                }
            }

            row.clientReportCount = reportCount;
            if (reportCount == 0)
            {
                row.clientTicksAverage = 0;
                row.clientFramesPerSecondAverage = 0;
                row.clientMarkers = Array.Empty<SweepMarker>();
                return;
            }

            row.clientTicksAverage = ticksSum / (double)reportCount;
            row.clientFramesPerSecondAverage = framesPerSecondSum / reportCount;

            var averaged = new SweepMarker[order.Count];
            for (var i = 0; i < order.Count; i++)
            {
                var total = totals[order[i]];
                averaged[i] = new SweepMarker
                {
                    name = total.name,
                    totalMilliseconds = total.totalMilliseconds / reportCount,
                    sampleBlocks = total.sampleBlocks / reportCount,
                    microsecondsPerTick = total.microsecondsPerTick / reportCount
                };
            }

            row.clientMarkers = averaged;
        }

        private static void PopulateBenchMetrics(SweepRow row)
        {
            row.markers = ParseMarkerSections(row.benchMessage, out var header);
            if (header.Length == 0)
                return;

            row.elapsedTicks = ReadUnsignedMetric(header, "ticks");
            row.ackLagAverageTicks = ReadDoubleMetric(header, "ackLagAvg");
            row.ackLagMaximumTicks = ReadUnsignedMetric(header, "ackLagMax");
            row.fullFramesSent = ReadUnsignedMetric(header, "fullFrames");
            row.deleteChurnDeletes = ReadLongMetric(header, "deleteChurnDeletes");
            row.deleteChurnRespawns = ReadLongMetric(header, "deleteChurnRespawns");
            row.windowDataSent = ReadUnsignedMetric(header, "windowDataSent");
            row.windowDataReceived = ReadUnsignedMetric(header, "windowDataReceived");
            row.visibilityValidatedPlayers = (int)ReadUnsignedMetric(
                header,
                "visibilityValidatedPlayers");
            row.visibilityCappedChurnPercent = (int)(
                ReadMetric(header, "visibilityCappedChurnPercent") != null
                    ? ReadUnsignedMetric(header, "visibilityCappedChurnPercent")
                    : ReadUnsignedMetric(header, "visibilityEffectiveChurnPercent"));
            row.visibilityMutationCalls = ReadLongMetric(
                header,
                "visibilityMutationCalls");
            row.visibilityAcquisitions = ReadLongMetric(
                header,
                "visibilityAcquisitions");
            row.visibilityReleases = ReadLongMetric(
                header,
                "visibilityReleases");
            row.visibilityActiveHandles = ReadLongMetric(
                header,
                "visibilityActiveHandles");
            row.visibilityEpochs = ReadLongMetric(
                header,
                "visibilityEpochs");
        }

        private static string ReadMetric(string[] values, string name)
        {
            var prefix = name + "=";
            for (var i = 1; i < values.Length; i++)
            {
                if (values[i].StartsWith(prefix, StringComparison.Ordinal))
                    return values[i].Substring(prefix.Length);
            }

            return null;
        }

        private static double ReadDoubleMetric(string[] values, string name)
        {
            return double.TryParse(
                ReadMetric(values, name),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result)
                ? result
                : 0;
        }

        private static long ReadLongMetric(string[] values, string name)
        {
            return long.TryParse(
                ReadMetric(values, name),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result)
                ? result
                : 0;
        }

        private static ulong ReadUnsignedMetric(string[] values, string name)
        {
            return ulong.TryParse(
                ReadMetric(values, name),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result)
                ? result
                : 0;
        }

        private static double FindMarkerPerTickUs(SweepRow row, string markerName)
        {
            return FindMarkerPerTickUs(row.markers, markerName);
        }

        private static double FindClientMarkerPerTickUs(SweepRow row, string markerName)
        {
            return FindMarkerPerTickUs(row.clientMarkers, markerName);
        }

        private static double FindMarkerPerTickUs(SweepMarker[] markers, string markerName)
        {
            if (markers == null)
                return 0;

            for (var i = 0; i < markers.Length; i++)
            {
                if (markers[i].name == markerName)
                    return markers[i].microsecondsPerTick;
            }

            return 0;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static (Process process, string role, string resultsPath) StartPlayer(string playerPath, string runDir, string stem, string roleArguments)
        {
            var resultsPath = Path.GetFullPath(Path.Combine(runDir, $"{stem}.json"));
            var logPath = Path.GetFullPath(Path.Combine(runDir, $"{stem}.log"));

            if (File.Exists(resultsPath))
                File.Delete(resultsPath);
            if (File.Exists(logPath))
                File.Delete(logPath);

            var arguments =
                $"{roleArguments} " +
                $"-results \"{resultsPath}\" " +
                $"-logFile \"{logPath}\"";

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = playerPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Directory.GetCurrentDirectory()
            });

            if (process == null)
                throw new InvalidOperationException($"Failed to start benchmark player '{playerPath}'.");

            return (process, stem, resultsPath);
        }

        private static BenchResultExtraction ExtractBenchResult(string resultsPath)
        {
            if (!File.Exists(resultsPath))
                return default;

            var json = File.ReadAllText(resultsPath);
            if (string.IsNullOrWhiteSpace(json))
                return default;

            var wrapped = JsonUtility.FromJson<ScenarioDetailsArrayDto>("{\"items\":" + json + "}");
            if (wrapped.items == null)
                return default;

            foreach (var item in wrapped.items)
            {
                if (item.name != "ServerLoadBenchmarkScenario")
                    continue;

                return new BenchResultExtraction
                {
                    found = true,
                    success = item.result.success,
                    message = item.result.message,
                    dataSent = item.dataSent,
                    dataReceived = item.dataReceived
                };
            }

            return default;
        }

        private static void WriteReport(
            Options options,
            string playerPath,
            List<SweepRow> rows,
            bool success)
        {
            var generatedAtUtc = DateTime.UtcNow.ToString("O");
            var markdownPath = Path.GetFullPath(
                Path.Combine(options.outputDirectory, "server-load-sweep.md"));
            var jsonPath = Path.GetFullPath(
                Path.Combine(options.outputDirectory, "server-load-sweep.json"));
            var report = new SweepReport
            {
                schemaVersion = 2,
                generatedAtUtc = generatedAtUtc,
                unityVersion = Application.unityVersion,
                playerPath = Path.GetFullPath(playerPath),
                outputDirectory = Path.GetFullPath(options.outputDirectory),
                markdownPath = markdownPath,
                jsonPath = jsonPath,
                success = success,
                seconds = options.seconds,
                inputEvery = options.inputEvery,
                packetLoss = options.packetLoss,
                visibilityPercent = options.visibilityPercent,
                visibilityChurnPercent = options.visibilityChurnPercent,
                visibilityChurnTicks = options.visibilityChurnTicks,
                deleteChurnPerSecond = options.deleteChurnPerSecond,
                clientCounts = options.clientCounts,
                objectCounts = options.objectCounts,
                latenciesMs = options.latenciesMs,
                visibilityModes = options.visibilityModes,
                rows = rows.ToArray()
            };

            File.WriteAllText(jsonPath, JsonUtility.ToJson(report, true));

            var sb = new StringBuilder();
            sb.AppendLine("# Server Load Benchmark Matrix");
            sb.AppendLine();
            sb.AppendLine($"Generated: `{generatedAtUtc}`");
            sb.AppendLine($"Unity: `{Application.unityVersion}`");
            sb.AppendLine($"Clients: `{string.Join(",", options.clientCounts)}`");
            sb.AppendLine($"Objects: `{string.Join(",", options.objectCounts)}`");
            sb.AppendLine($"Latencies: `{string.Join(",", options.latenciesMs)} ms`");
            sb.AppendLine($"Visibility modes: `{string.Join(",", options.visibilityModes)}`");
            sb.AppendLine(
                $"Static/churn parameters: `{options.visibilityPercent}% visible, " +
                $"{options.visibilityChurnPercent}% churn every {options.visibilityChurnTicks} ticks`");
            if (Array.IndexOf(options.visibilityModes, "deletechurn") >= 0)
            {
                sb.AppendLine(
                    $"Delete churn parameters: `{options.deleteChurnPerSecond} " +
                    "deletes+respawns per second`");
            }
            sb.AppendLine($"Seconds: `{options.seconds}` PacketLoss: `{options.packetLoss}%`");
            sb.AppendLine($"Success: `{success}`");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| Run | Clients | Objects | Latency ms | Visibility | Delete churn /s | Ack avg ticks | Ack max ticks | Full frames | Client frames/s | Input history us/tick | Visibility events us/tick | Commit visibility us/tick | Hierarchy projection us/tick | Frame write us/tick | Client replay us/tick | Client rollback us/tick | Client input read us/tick | Host window sent B | Host window received B | Validated players | Result |");
            sb.AppendLine("|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");

            foreach (var row in rows)
            {
                sb.Append("| ").Append(row.runIndex + 1);
                sb.Append(" | ").Append(row.clientCount);
                sb.Append(" | ").Append(row.objects);
                sb.Append(" | ").Append(row.latencyMs);
                sb.Append(" | ").Append(row.visibilityMode);
                sb.Append(" | ").Append(row.deleteChurnPerSecond);
                sb.Append(" | ").Append(FormatNumber(row.ackLagAverageTicks));
                sb.Append(" | ").Append(row.ackLagMaximumTicks);
                sb.Append(" | ").Append(row.fullFramesSent);
                sb.Append(" | ").Append(FormatNumber(row.clientFramesPerSecondAverage));
                sb.Append(" | ").Append(FormatNumber(
                    FindMarkerPerTickUs(row, "WriteInputHistory")));
                sb.Append(" | ").Append(FormatNumber(
                    FindMarkerPerTickUs(row, "ServerLoadBenchmark.ApplyVisibilityEvents")));
                sb.Append(" | ").Append(FormatNumber(
                    FindMarkerPerTickUs(row, "CommitVisibilityChanges")));
                sb.Append(" | ").Append(FormatNumber(
                    FindMarkerPerTickUs(row, "BuildVisibilityHierarchyProjection")));
                sb.Append(" | ").Append(FormatNumber(
                    FindMarkerPerTickUs(row, "WriteFrameOnServer")));
                sb.Append(" | ").Append(FormatNumber(
                    FindClientMarkerPerTickUs(row, "ReplayToLatestTick")));
                sb.Append(" | ").Append(FormatNumber(
                    FindClientMarkerPerTickUs(row, "RollbackToFrame")));
                sb.Append(" | ").Append(FormatNumber(
                    FindClientMarkerPerTickUs(row, "ReadInputHistory")));
                sb.Append(" | ").Append(row.windowDataSent);
                sb.Append(" | ").Append(row.windowDataReceived);
                sb.Append(" | ").Append(row.visibilityValidatedPlayers);
                sb.Append(" | ").Append(row.success ? "ok" : EscapeMarkdown(row.failure));
                sb.AppendLine(" |");
            }

            sb.AppendLine();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                sb.AppendLine(
                    $"## Run {row.runIndex + 1}: {row.clientCount} clients / " +
                    $"{row.objects} objects / {row.latencyMs} ms / {row.visibilityMode}");
                sb.AppendLine();
                sb.AppendLine($"Artifacts: `{row.runDirectory}`");
                sb.AppendLine(
                    $"Timed window bytes: `{row.windowDataSent} sent / " +
                    $"{row.windowDataReceived} received`");
                sb.AppendLine(
                    $"Whole-scenario host bytes: `{row.hostDataSent} sent / " +
                    $"{row.hostDataReceived} received`");
                sb.AppendLine(
                    $"Final visibility signatures validated: " +
                    $"`{row.visibilityValidatedPlayers} players`");
                sb.AppendLine(
                    $"Timed window full frames: `{row.fullFramesSent}`");
                if (row.visibilityMode is "churn" or "acquire-churn")
                {
                    sb.AppendLine(
                        $"Capped visibility churn: `{row.visibilityCappedChurnPercent}%`");
                }
                if (row.deleteChurnPerSecond > 0)
                {
                    sb.AppendLine(
                        $"Delete churn: `{row.deleteChurnPerSecond}/s, " +
                        $"{row.deleteChurnDeletes} deletes / " +
                        $"{row.deleteChurnRespawns} respawns`");
                }
                if (!row.success)
                    sb.AppendLine($"FAILED: {row.failure}");
                if (row.benchMessage != null)
                    sb.AppendLine($"```\n{row.benchMessage.Replace(" | ", "\n")}\n```");
                if (row.clientBenchMessages != null)
                {
                    for (var c = 0; c < row.clientBenchMessages.Length; c++)
                    {
                        sb.AppendLine($"Client {c + 1}:");
                        sb.AppendLine(
                            $"```\n{row.clientBenchMessages[c].Replace(" | ", "\n")}\n```");
                    }
                }
                sb.AppendLine();
            }

            File.WriteAllText(markdownPath, sb.ToString());
            Debug.Log(
                $"[ServerLoadBenchmark] Reports written to {markdownPath} and {jsonPath}\n{sb}");
        }

        private static string EscapeMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static bool HasArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static int GetIntArgument(string name, int fallback)
        {
            var value = GetArgument(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
        }

        private static int GetValidatedIntArgument(
            string name,
            int fallback,
            int minimum,
            int maximum)
        {
            var raw = GetArgument(name);
            if (raw == null)
                return fallback;

            if (!int.TryParse(
                    raw,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value) || value < minimum || value > maximum)
            {
                throw new ArgumentException(
                    $"{name} must be an integer between {minimum} and {maximum}; " +
                    $"invalid value '{raw}'.");
            }

            return value;
        }

        private static int[] ParsePositiveCsvArgument(
            string argumentName,
            int scalarFallback,
            int minimum)
        {
            var raw = GetArgument(argumentName);
            if (string.IsNullOrWhiteSpace(raw))
                return new[] { scalarFallback };

            var values = new List<int>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (!int.TryParse(
                        part.Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value) || value < minimum)
                {
                    throw new ArgumentException(
                        $"{argumentName} must be a comma-separated list of integers >= {minimum}; " +
                        $"invalid value '{part.Trim()}'.");
                }

                values.Add(value);
            }

            if (values.Count == 0)
                throw new ArgumentException($"{argumentName} must contain at least one value.");

            return values.ToArray();
        }

        private static string[] ParseVisibilityModes()
        {
            var raw = GetArgument("-slbVisibilityModes") ?? DefaultVisibilityModes;
            var modes = new List<string>();
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var value = part.Trim().ToLowerInvariant();
                var mode = value switch
                {
                    "none" => "none",
                    "default" => "none",
                    "static" => "static",
                    "stable" => "static",
                    "churn" => "churn",
                    "acquire-churn" => "acquire-churn",
                    "acquire" => "acquire-churn",
                    "lease-churn" => "acquire-churn",
                    "deletechurn" => "deletechurn",
                    "delete-churn" => "deletechurn",
                    "delete" => "deletechurn",
                    _ => null
                };

                if (mode == null)
                {
                    throw new ArgumentException(
                        $"-slbVisibilityModes contains unsupported mode '{part.Trim()}'. " +
                        "Expected none, static, churn, acquire-churn, or deletechurn.");
                }

                modes.Add(mode);
            }

            if (modes.Count == 0)
                throw new ArgumentException("-slbVisibilityModes must contain at least one mode.");

            return modes.ToArray();
        }
        private sealed class Options
        {
            public string outputDirectory;
            public string playerPath;
            public bool skipBuild;
            public int inputEvery;
            public int seconds;
            public int packetLoss;
            public int basePort;
            public int runTimeoutSeconds;
            public int maxRuns;
            public int visibilityPercent;
            public int visibilityChurnPercent;
            public int visibilityChurnTicks;
            public int deleteChurnPerSecond;
            public int[] clientCounts;
            public int[] objectCounts;
            public int[] latenciesMs;
            public string[] visibilityModes;

            public static Options FromCommandLine()
            {
                var latenciesRaw = GetArgument("-slbLatencies") ?? DefaultLatencies;
                var parts = latenciesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var latencies = new List<int>();
                foreach (var part in parts)
                {
                    if (!int.TryParse(
                            part.Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var parsed) || parsed < 0)
                    {
                        throw new ArgumentException(
                            "-slbLatencies must be a comma-separated list of integers >= 0; " +
                            $"invalid value '{part.Trim()}'.");
                    }

                    latencies.Add(parsed);
                }

                if (latencies.Count == 0)
                    throw new ArgumentException("-slbLatencies must contain at least one value.");

                var output = GetArgument("-slbOutput") ?? DefaultOutputDirectory;
                if (!Path.IsPathRooted(output))
                    output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", output));

                var player = GetArgument("-slbPlayer");
                if (!string.IsNullOrEmpty(player) && !Path.IsPathRooted(player))
                    player = Path.GetFullPath(Path.Combine(Application.dataPath, "..", player));

                var scalarClientCount = Math.Max(
                    1,
                    GetIntArgument("-slbClients", DefaultClientCount));
                var scalarObjectCount = Math.Max(
                    1,
                    GetIntArgument("-slbObjects", DefaultObjects));

                return new Options
                {
                    outputDirectory = output,
                    playerPath = player,
                    skipBuild = HasArgument("-slbSkipBuild"),
                    inputEvery = Math.Max(0, GetIntArgument("-slbInputEvery", 1)),
                    seconds = Math.Max(5, GetIntArgument("-slbSeconds", DefaultSeconds)),
                    packetLoss = Math.Max(0, GetIntArgument("-slbPacketLoss", 0)),
                    basePort = GetIntArgument("-slbBasePort", new System.Random().Next(24000, 32000)),
                    runTimeoutSeconds = Math.Max(60, GetIntArgument("-slbRunTimeout", DefaultRunTimeoutSeconds)),
                    maxRuns = Math.Max(1, GetIntArgument("-slbMaxRuns", DefaultMaxRuns)),
                    visibilityPercent = GetValidatedIntArgument(
                        "-slbVisibilityPercent",
                        DefaultVisibilityPercent,
                        0,
                        100),
                    visibilityChurnPercent = GetValidatedIntArgument(
                        "-slbVisibilityChurnPercent",
                        DefaultVisibilityChurnPercent,
                        0,
                        100),
                    visibilityChurnTicks = GetValidatedIntArgument(
                        "-slbVisibilityChurnTicks",
                        DefaultVisibilityChurnTicks,
                        1,
                        int.MaxValue),
                    deleteChurnPerSecond = GetValidatedIntArgument(
                        "-slbDeleteChurn",
                        DefaultDeleteChurnPerSecond,
                        1,
                        int.MaxValue),
                    clientCounts = ParsePositiveCsvArgument(
                        "-slbClientCounts",
                        scalarClientCount,
                        1),
                    objectCounts = ParsePositiveCsvArgument(
                        "-slbObjectCounts",
                        scalarObjectCount,
                        1),
                    latenciesMs = latencies.ToArray(),
                    visibilityModes = ParseVisibilityModes()
                };
            }
        }

        private sealed class SweepCase
        {
            public int latencyMs;
            public int clientCount;
            public int objects;
            public string visibilityMode;
        }

        [Serializable]
        private sealed class SweepRow
        {
            public int runIndex;
            public int latencyMs;
            public int clientCount;
            public int objects;
            public string visibilityMode;
            public int visibilityPercent;
            public int visibilityChurnPercent;
            public int visibilityChurnTicks;
            public int deleteChurnPerSecond;
            public long deleteChurnDeletes;
            public long deleteChurnRespawns;
            public string runDirectory;
            public ulong hostDataSent;
            public ulong hostDataReceived;
            public ulong windowDataSent;
            public ulong windowDataReceived;
            public ulong fullFramesSent;
            public int visibilityValidatedPlayers;
            public ulong elapsedTicks;
            public double ackLagAverageTicks;
            public ulong ackLagMaximumTicks;
            public int visibilityCappedChurnPercent;
            public long visibilityMutationCalls;
            public long visibilityAcquisitions;
            public long visibilityReleases;
            public long visibilityActiveHandles;
            public long visibilityEpochs;
            public SweepMarker[] markers;
            public int clientReportCount;
            public double clientTicksAverage;
            public double clientFramesPerSecondAverage;
            public SweepMarker[] clientMarkers;
            public string[] clientBenchMessages;
            public bool success;
            public string failure;
            public string benchMessage;
        }

        [Serializable]
        private sealed class SweepMarker
        {
            public string name;
            public double totalMilliseconds;
            public long sampleBlocks;
            public double microsecondsPerTick;
        }

        [Serializable]
        private sealed class SweepReport
        {
            public int schemaVersion;
            public string generatedAtUtc;
            public string unityVersion;
            public string playerPath;
            public string outputDirectory;
            public string markdownPath;
            public string jsonPath;
            public bool success;
            public int seconds;
            public int inputEvery;
            public int packetLoss;
            public int visibilityPercent;
            public int visibilityChurnPercent;
            public int visibilityChurnTicks;
            public int deleteChurnPerSecond;
            public int[] clientCounts;
            public int[] objectCounts;
            public int[] latenciesMs;
            public string[] visibilityModes;
            public SweepRow[] rows;
        }

        private struct BenchResultExtraction
        {
            public bool found;
            public bool success;
            public string message;
            public ulong dataSent;
            public ulong dataReceived;
        }

        [Serializable]
        private sealed class ScenarioDetailsArrayDto
        {
            public ScenarioDetailsDto[] items;
        }

        [Serializable]
        private struct ScenarioDetailsDto
        {
            public string name;
            public ScenarioResultDto result;
            public ulong dataSent;
            public ulong dataReceived;
        }

        [Serializable]
        private struct ScenarioResultDto
        {
            public bool success;
            public string message;
        }
    }
}
