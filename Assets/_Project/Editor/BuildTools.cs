using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ToLive.EditorTools
{
    /// <summary>Builds a Windows development player into Builds/ (git-ignored).</summary>
    public static class BuildTools
    {
        [MenuItem("Tools/To Live/Build Windows Development Player", priority = 40)]
        public static string BuildDevPlayer()
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/_Project/Scenes/Main.unity" },
                locationPathName = "Builds/Windows/ToLive.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            var msg = $"{s.result} in {s.totalTime.TotalSeconds:0}s, {s.totalErrors} errors, size {s.totalSize / (1024 * 1024)} MB";
            Debug.Log("[ToLive] Build: " + msg);
            return msg;
        }
    }
}
