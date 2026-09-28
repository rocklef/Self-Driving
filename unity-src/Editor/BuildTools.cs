using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildTools
{
    public const string ExePath = "../Build/RobotaxiSim.exe";

    [MenuItem("Self Driving/Build Windows Player")]
    public static void BuildWindows()
    {
        PlayerSettings.productName = "Robotaxi Simulation";
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1600;
        PlayerSettings.defaultScreenHeight = 900;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;   // keep driving when the window loses focus
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/SelfDrivingDemo.unity" }, ExePath,
            BuildTarget.StandaloneWindows64, BuildOptions.None);
        Debug.Log($"BuildTools: {report.summary.result}, {report.summary.totalErrors} errors, output {report.summary.outputPath}");
        if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
