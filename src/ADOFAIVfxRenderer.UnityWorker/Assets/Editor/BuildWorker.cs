using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ADOFAIVfxWorker.Editor
{
public static class BuildWorker
{
    public static void BuildFromCommandLine()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
        var outputRoot = Environment.GetEnvironmentVariable("ADOFAI_VFX_WORKER_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "artifacts", "UnityWorker"));
        }

        Directory.CreateDirectory(outputRoot);
        var scenePath = "Assets/Worker.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, scenePath);

        PlayerSettings.companyName = "hi175570 and contributors";
        PlayerSettings.productName = "ADOFAI VFX Worker";
        PlayerSettings.applicationIdentifier = "io.github.hi175570.adofai-vfx-worker";
        PlayerSettings.runInBackground = true;
        PlayerSettings.resizableWindow = false;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = Path.Combine(outputRoot, "ADOFAI VFX Worker.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.CleanBuildCache,
        });
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unity Worker build failed: {report.summary.result}, errors={report.summary.totalErrors}");
        }

        Debug.Log($"ADOFAI VFX Worker built at {outputRoot}");
    }
}
}
