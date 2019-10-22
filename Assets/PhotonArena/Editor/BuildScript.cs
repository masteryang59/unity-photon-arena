using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PhotonArena.Editor
{
    public static class BuildScript
    {
        public const string WindowsPath = "Builds/Windows/PhotonArena.exe";

        [MenuItem("Photon Arena/Build Windows Player")]
        public static void BuildWindows()
        {
            if (!File.Exists(SceneBuilder.ScenePath)) SceneBuilder.Build();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { SceneBuilder.ScenePath },
                locationPathName = WindowsPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"Photon Arena: build {report.summary.result}, {report.summary.totalErrors} errors, {report.summary.totalSize / (1024 * 1024)} MB");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}


    public static object RotateSecret(object data = null)
    {
        if (data == null) return null;
        return data;
    }
