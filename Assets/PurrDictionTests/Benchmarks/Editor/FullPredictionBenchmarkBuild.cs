using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PurrNet.Prediction.Benchmarks.Editor
{
    /// <summary>Build once, then run both cadence modes with run-full-prediction-bench.ps1.</summary>
    public static class FullPredictionBenchmarkBuild
    {
        public static void BuildWindowsDev()
        {
            var target = NamedBuildTarget.Standalone;
            var previousBackend = PlayerSettings.GetScriptingBackend(target);
            int exitCode = 1;

            try
            {
                var playerPath = Path.GetFullPath(GetArgument("-fpPlayerPath") ??
                    "test-results/full-prediction-player/PurrDictionTests.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(playerPath) ?? ".");
                PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.Mono2x);

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/PredictionTests/Bootstrap.unity" },
                    locationPathName = playerPath,
                    target = BuildTarget.StandaloneWindows64,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = BuildOptions.Development
                });

                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException(
                        $"Full prediction benchmark build failed: {report.summary.result}, " +
                        $"errors={report.summary.totalErrors}");

                File.WriteAllText(playerPath + ".build.json", JsonUtility.ToJson(new BuildMetadata
                {
                    unityVersion = Application.unityVersion,
                    scriptingBackend = "Mono",
                    developmentBuild = true,
                    target = "StandaloneWindows64",
                    scene = "Assets/PredictionTests/Bootstrap.unity",
                    builtAtUtc = DateTime.UtcNow.ToString("O")
                }, true));
                Debug.Log($"Full prediction benchmark build succeeded: {playerPath}");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(target, previousBackend);
            }

            EditorApplication.Exit(exitCode);
        }

        private static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        [Serializable]
        private sealed class BuildMetadata
        {
            public string unityVersion;
            public string scriptingBackend;
            public bool developmentBuild;
            public string target;
            public string scene;
            public string builtAtUtc;
        }
    }
}
