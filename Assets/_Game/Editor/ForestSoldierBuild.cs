using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ForestSoldier.EditorTools
{
    /// <summary>
    /// Desktop build. Menu: Forest Soldier / Build macOS.
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod ForestSoldier.EditorTools.ForestSoldierBuild.BuildMac
    /// Output: Builds/macOS/ForestSoldier.app (Builds/ is git-ignored).
    /// </summary>
    public static class ForestSoldierBuild
    {
        private const string ScenePath = "Assets/_Game/Scenes/ForestRange.unity";
        private const string MacPath = "Builds/macOS/ForestSoldier.app";

        [MenuItem("Forest Soldier/Build macOS")]
        public static void BuildMacFromMenu()
        {
            var report = BuildMac();
            if (report.summary.result == BuildResult.Succeeded) EditorUtility.RevealInFinder(MacPath);
        }

        public static BuildReport BuildMac()
        {
            ConfigurePlayer();
            Directory.CreateDirectory(Path.GetDirectoryName(MacPath));
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = MacPath,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"[ForestSoldierBuild] {s.result}: {s.outputPath}, {s.totalSize / (1024f * 1024f):F1} MB, " +
                      $"{s.totalErrors} errors, {s.totalWarnings} warnings, {s.totalTime.TotalSeconds:F0} s");
            if (Application.isBatchMode && s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
            return report;
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Forest Soldier";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultIsNativeResolution = false;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.enableFrameTimingStats = true; // the benchmark reads CPU/GPU frame times
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Standalone, "com.forestsoldier.prototype");
        }
    }
}
