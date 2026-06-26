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
    public static class PredictionScenarioBenchmarkRunner
    {
        private const string BootstrapScene = "Assets/PredictionTests/Bootstrap.unity";
        private const string DefaultOutputDirectory = "Builds/PurrDictionScenarioBenchmarks";
        private const int DefaultClientCount = 1;
        private const int DefaultTimeoutSeconds = 900;
        private const int DefaultLatencyMinMs = 40;
        private const int DefaultLatencyMaxMs = 80;
        private const int DefaultHistoryStressObjects = 256;
        private const int DefaultHistoryStressPayload = 32;
        private const int DefaultHistoryStressListPayload = 32;
        private const int DefaultHistoryStressTicks = 240;

        [MenuItem("Tools/PurrDiction/Analysis/Run Scenario Benchmarks", false, -87)]
        public static void RunFromMenu()
        {
            RunInternal(exitEditor: false);
        }

        public static void RunFromCommandLine()
        {
            RunInternal(exitEditor: true);
        }

        private static void RunInternal(bool exitEditor)
        {
            var exitCode = 0;

            try
            {
                var options = ScenarioBenchmarkOptions.FromCommandLine();
                var report = Run(options);
                exitCode = report.success ? 0 : -1;

                Debug.Log($"PurrDiction scenario benchmarks wrote:\n{report.jsonPath}\n{report.markdownPath}");
                if (!report.success)
                    Debug.LogError("PurrDiction scenario benchmarks failed. See report for process exit codes and scenario failures.");
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

        private static ScenarioBenchmarkReport Run(ScenarioBenchmarkOptions options)
        {
            Directory.CreateDirectory(options.outputDirectory);

            var playerPath = options.playerPath;
            if (string.IsNullOrEmpty(playerPath))
                playerPath = Path.Combine(options.outputDirectory, "Player", GetPlayerFileName(options.buildTarget));

            if (!options.skipBuild)
                BuildPlayer(playerPath, options);

            var processResults = RunPlayers(playerPath, options);
            var report = new ScenarioBenchmarkReport
            {
                generatedAtUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                projectPath = GetProjectRoot(),
                bootstrapScene = BootstrapScene,
                playerPath = playerPath,
                clientCount = options.clientCount,
                latencyMinMs = options.latencyMinMs,
                latencyMaxMs = options.latencyMaxMs,
                timeoutSeconds = options.timeoutSeconds,
                historyStressObjects = options.historyStressObjects,
                historyStressPayload = options.historyStressPayload,
                historyStressListPayload = options.historyStressListPayload,
                historyStressTicks = options.historyStressTicks,
                processes = processResults.ToArray()
            };

            report.success = IsSuccessful(report);
            report.jsonPath = Path.Combine(options.outputDirectory, "prediction-scenario-benchmarks.json");
            report.markdownPath = Path.Combine(options.outputDirectory, "prediction-scenario-benchmarks.md");

            File.WriteAllText(report.jsonPath, JsonUtility.ToJson(report, true));
            File.WriteAllText(report.markdownPath, BuildMarkdown(report));
            return report;
        }

        private static void BuildPlayer(string playerPath, ScenarioBenchmarkOptions options)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(playerPath) ?? options.outputDirectory);

            var buildOptions = options.developmentBuild ? BuildOptions.Development : BuildOptions.None;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { BootstrapScene },
                locationPathName = playerPath,
                target = options.buildTarget,
                options = buildOptions
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Scenario benchmark player build failed: {report.summary.result} ({report.summary.totalErrors} errors)");
            }
        }

        private static List<ScenarioProcessResult> RunPlayers(string playerPath, ScenarioBenchmarkOptions options)
        {
            var processes = new List<RunningScenarioProcess>();
            var expectedConnections = options.clientCount + 1;

            processes.Add(StartPlayer(
                playerPath,
                options,
                "host",
                0,
                $"-role host -count {expectedConnections}"));

            Thread.Sleep(1000);

            for (var i = 0; i < options.clientCount; i++)
            {
                processes.Add(StartPlayer(
                    playerPath,
                    options,
                    "client",
                    i + 1,
                    "-role client -serverHost 127.0.0.1"));
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < options.timeoutSeconds)
            {
                var allExited = true;
                for (var i = 0; i < processes.Count; i++)
                {
                    if (!processes[i].process.HasExited)
                    {
                        allExited = false;
                        break;
                    }
                }

                if (allExited)
                    break;

                Thread.Sleep(250);
            }

            var results = new List<ScenarioProcessResult>(processes.Count);
            for (var i = 0; i < processes.Count; i++)
            {
                var running = processes[i];
                var timedOut = !running.process.HasExited;

                if (timedOut)
                {
                    try
                    {
                        running.process.Kill();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Failed to kill timed-out scenario benchmark process {running.role}: {e.Message}");
                    }
                }

                var exitCode = timedOut ? -1 : running.process.ExitCode;
                running.process.Dispose();

                results.Add(new ScenarioProcessResult
                {
                    role = running.role,
                    index = running.index,
                    exitCode = exitCode,
                    timedOut = timedOut,
                    resultsPath = running.resultsPath,
                    logPath = running.logPath,
                    scenarios = ReadScenarioResults(running.resultsPath)
                });
            }

            return results;
        }

        private static RunningScenarioProcess StartPlayer(
            string playerPath,
            ScenarioBenchmarkOptions options,
            string role,
            int index,
            string roleArguments)
        {
            var stem = index == 0 ? role : $"{role}-{index}";
            var resultsPath = Path.GetFullPath(Path.Combine(options.outputDirectory, $"{stem}.json"));
            var logPath = Path.GetFullPath(Path.Combine(options.outputDirectory, $"{stem}.log"));

            var arguments =
                "-batchmode -nographics " +
                $"{roleArguments} " +
                $"-port {options.port} " +
                $"-latencyMin {options.latencyMinMs} " +
                $"-latencyMax {options.latencyMaxMs} " +
                "-profileScenarios " +
                "-includeHistoryStressScenario " +
                $"-historyStressObjects {options.historyStressObjects} " +
                $"-historyStressPayload {options.historyStressPayload} " +
                $"-historyStressListPayload {options.historyStressListPayload} " +
                $"-historyStressTicks {options.historyStressTicks} " +
                $"-results {Quote(resultsPath)} " +
                $"-logFile {Quote(logPath)}";

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = playerPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(playerPath) ?? Directory.GetCurrentDirectory()
            });

            if (process == null)
                throw new InvalidOperationException($"Failed to start scenario benchmark player '{playerPath}'.");

            return new RunningScenarioProcess
            {
                process = process,
                role = role,
                index = index,
                resultsPath = resultsPath,
                logPath = logPath
            };
        }

        private static ScenarioDetailsDto[] ReadScenarioResults(string path)
        {
            if (!File.Exists(path))
                return Array.Empty<ScenarioDetailsDto>();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<ScenarioDetailsDto>();

            var wrapped = JsonUtility.FromJson<ScenarioDetailsArrayDto>("{\"items\":" + json + "}");
            return wrapped.items ?? Array.Empty<ScenarioDetailsDto>();
        }

        private static bool IsSuccessful(ScenarioBenchmarkReport report)
        {
            for (var i = 0; i < report.processes.Length; i++)
            {
                var process = report.processes[i];
                if (process.exitCode != 0 || process.timedOut || process.scenarios.Length == 0)
                    return false;

                for (var j = 0; j < process.scenarios.Length; j++)
                {
                    if (!process.scenarios[j].result.success)
                        return false;
                }
            }

            return true;
        }

        private static string BuildMarkdown(ScenarioBenchmarkReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# PurrDiction Scenario Benchmarks");
            sb.AppendLine();
            sb.AppendLine($"Generated: `{report.generatedAtUtc}`");
            sb.AppendLine($"Unity: `{report.unityVersion}`");
            sb.AppendLine($"Player: `{report.playerPath}`");
            sb.AppendLine($"Clients: `{report.clientCount}`");
            sb.AppendLine($"Latency: `{report.latencyMinMs}-{report.latencyMaxMs} ms`");
            sb.AppendLine($"History stress: `{report.historyStressObjects} objects, array payload {report.historyStressPayload}, list payload {report.historyStressListPayload}, {report.historyStressTicks} ticks`");
            sb.AppendLine($"Success: `{report.success}`");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| Process | Exit | Scenario | Result | Duration ms | Max Objects | Save Calls | Non-event Saves | Event Saves | Sent B | Received B | SaveHistory ms | SaveHistory samples |");
            sb.AppendLine("|---|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

            for (var i = 0; i < report.processes.Length; i++)
            {
                var process = report.processes[i];
                var processName = process.index == 0 ? process.role : $"{process.role}-{process.index}";

                if (process.scenarios.Length == 0)
                {
                    sb.Append("| ");
                    sb.Append(processName);
                    sb.Append(" | ");
                    sb.Append(process.exitCode.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine(" | *(no results)* | fail |  |  |  |  |  |  |  |  |  |  |");
                    continue;
                }

                for (var j = 0; j < process.scenarios.Length; j++)
                {
                    var scenario = process.scenarios[j];
                    var saveHistory = FindMarker(scenario, "PredictionManager.SaveHistory");

                    sb.Append("| ");
                    sb.Append(processName);
                    sb.Append(" | ");
                    sb.Append(process.exitCode.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" | ");
                    sb.Append(EscapeMarkdown(scenario.name));
                    sb.Append(" | ");
                    sb.Append(scenario.result.success ? "ok" : EscapeMarkdown(scenario.result.message));
                    sb.Append(" | ");
                    sb.Append(FormatNumber(scenario.durationInMs));
                    sb.Append(" | ");
                    sb.Append(scenario.performance.world.maxSpawnedIdentities.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" | ");
                    sb.Append(scenario.performance.history.saveCalls.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" | ");
                    sb.Append(scenario.performance.history.nonEventSaveCalls.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" | ");
                    sb.Append(scenario.performance.history.eventHandlerSaveCalls.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" | ");
                    sb.Append(scenario.dataSent.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" | ");
                    sb.Append(scenario.dataReceived.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" | ");
                    sb.Append(FormatNumber(saveHistory.elapsedMilliseconds));
                    sb.Append(" | ");
                    sb.Append(saveHistory.sampleBlockCount.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine(" |");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Command");
            sb.AppendLine();
            sb.AppendLine("```powershell");
            sb.AppendLine("Unity.exe -batchmode -projectPath C:\\wkspaces\\unity\\riten\\PurrDiction -executeMethod PurrNet.Prediction.Benchmarks.Editor.PredictionScenarioBenchmarkRunner.RunFromCommandLine -quit -logFile Builds\\PurrDictionScenarioBenchmarks\\scenario-benchmark-editor.log");
            sb.AppendLine("```");

            return sb.ToString();
        }

        private static ScenarioMarkerDetailsDto FindMarker(ScenarioDetailsDto scenario, string markerName)
        {
            var markers = scenario.performance.markers;
            if (markers == null)
                return default;

            for (var i = 0; i < markers.Length; i++)
            {
                if (markers[i].name == markerName)
                    return markers[i];
            }

            return default;
        }

        private static string GetPlayerFileName(BuildTarget buildTarget)
        {
            return buildTarget == BuildTarget.StandaloneWindows64
                ? "PurrDictionScenarioBenchmarks.exe"
                : "PurrDictionScenarioBenchmarks";
        }

        private static BuildTarget GetDefaultBuildTarget()
        {
            return Application.platform switch
            {
                RuntimePlatform.WindowsEditor => BuildTarget.StandaloneWindows64,
                RuntimePlatform.LinuxEditor => BuildTarget.StandaloneLinux64,
                RuntimePlatform.OSXEditor => BuildTarget.StandaloneOSX,
                _ => EditorUserBuildSettings.activeBuildTarget
            };
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

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("#,0.###", CultureInfo.InvariantCulture);
        }

        private static string EscapeMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private sealed class ScenarioBenchmarkOptions
        {
            public string outputDirectory;
            public string playerPath;
            public bool skipBuild;
            public bool developmentBuild;
            public BuildTarget buildTarget;
            public int clientCount;
            public int timeoutSeconds;
            public int latencyMinMs;
            public int latencyMaxMs;
            public int port;
            public int historyStressObjects;
            public int historyStressPayload;
            public int historyStressListPayload;
            public int historyStressTicks;

            public static ScenarioBenchmarkOptions FromCommandLine()
            {
                var outputDirectory = NormalizeProjectPath(GetArgument("-purrdictionScenarioOutput") ?? DefaultOutputDirectory);
                var playerPath = NormalizeProjectPath(GetArgument("-purrdictionScenarioPlayer"));
                var clientCount = Math.Max(0, GetIntArgument("-purrdictionScenarioClients", DefaultClientCount));

                return new ScenarioBenchmarkOptions
                {
                    outputDirectory = outputDirectory,
                    playerPath = playerPath,
                    skipBuild = HasArgument("-purrdictionScenarioSkipBuild"),
                    developmentBuild = !HasArgument("-purrdictionScenarioRelease"),
                    buildTarget = GetDefaultBuildTarget(),
                    clientCount = clientCount,
                    timeoutSeconds = Math.Max(30, GetIntArgument("-purrdictionScenarioTimeout", DefaultTimeoutSeconds)),
                    latencyMinMs = Math.Max(0, GetIntArgument("-purrdictionScenarioLatencyMin", DefaultLatencyMinMs)),
                    latencyMaxMs = Math.Max(0, GetIntArgument("-purrdictionScenarioLatencyMax", DefaultLatencyMaxMs)),
                    port = GetIntArgument("-purrdictionScenarioPort", new System.Random().Next(24000, 32000)),
                    historyStressObjects = Math.Max(1, GetIntArgument("-purrdictionHistoryStressObjects", DefaultHistoryStressObjects)),
                    historyStressPayload = Math.Max(0, GetIntArgument("-purrdictionHistoryStressPayload", DefaultHistoryStressPayload)),
                    historyStressListPayload = Math.Max(0, GetIntArgument("-purrdictionHistoryStressListPayload", DefaultHistoryStressListPayload)),
                    historyStressTicks = Math.Max(1, GetIntArgument("-purrdictionHistoryStressTicks", DefaultHistoryStressTicks))
                };
            }
        }

        [Serializable]
        private sealed class ScenarioBenchmarkReport
        {
            public string generatedAtUtc;
            public string unityVersion;
            public string projectPath;
            public string bootstrapScene;
            public string playerPath;
            public int clientCount;
            public int latencyMinMs;
            public int latencyMaxMs;
            public int timeoutSeconds;
            public int historyStressObjects;
            public int historyStressPayload;
            public int historyStressListPayload;
            public int historyStressTicks;
            public bool success;
            public string jsonPath;
            public string markdownPath;
            public ScenarioProcessResult[] processes;
        }

        [Serializable]
        private sealed class ScenarioProcessResult
        {
            public string role;
            public int index;
            public int exitCode;
            public bool timedOut;
            public string resultsPath;
            public string logPath;
            public ScenarioDetailsDto[] scenarios;
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
            public double durationInMs;
            public ulong dataSent;
            public ulong dataReceived;
            public ScenarioPerformanceDetailsDto performance;
        }

        [Serializable]
        private struct ScenarioResultDto
        {
            public bool success;
            public string message;
        }

        [Serializable]
        private struct ScenarioPerformanceDetailsDto
        {
            public ScenarioMarkerDetailsDto[] markers;
            public ScenarioHistoryDetailsDto history;
            public ScenarioWorldDetailsDto world;
        }

        [Serializable]
        private struct ScenarioMarkerDetailsDto
        {
            public string name;
            public long elapsedNanoseconds;
            public long sampleBlockCount;
            public double elapsedMilliseconds;
            public double averageNanoseconds;
        }

        [Serializable]
        private struct ScenarioHistoryDetailsDto
        {
            public long saveCalls;
            public long nonEventSaveCalls;
            public long eventHandlerSaveCalls;
        }

        [Serializable]
        private struct ScenarioWorldDetailsDto
        {
            public int sampledFrames;
            public int maxSpawnedIdentities;
            public int finalSpawnedIdentities;
            public double averageSpawnedIdentities;
        }

        private sealed class RunningScenarioProcess
        {
            public Process process;
            public string role;
            public int index;
            public string resultsPath;
            public string logPath;
        }

        private static string NormalizeProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(GetProjectRoot(), path));
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }
    }
}
