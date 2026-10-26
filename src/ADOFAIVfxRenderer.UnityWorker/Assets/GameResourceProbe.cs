using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace ADOFAIVfxWorker
{
public static class GameResourceProbe
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Run()
    {
        var arguments = Environment.GetCommandLineArgs();
        var marker = Array.IndexOf(arguments, "--probe-game-resources");
        if (marker < 0) return;

        var outputPath = marker + 1 < arguments.Length ? arguments[marker + 1] : "game-resource-probe.json";
        var result = new
        {
            legacyGaussianBlur = ProbeShader("CameraFilterPackLegacy/Blur_GaussianBlur", "camerafilterpack_blur_gaussianblur"),
            advancedBlurFocus = ProbeShader("CameraFilterPack/Blur_Focus", "camerafilterpack_blur_focus"),
            visionTunnel = ProbeShader("CameraFilterPack/Vision_Tunnel", "camerafilterpack_vision_tunnel"),
            blizzardTexture = Resources.Load<Texture2D>("CameraFilterPack_Blizzard1") != null,
        };
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(result, Formatting.Indented));
        Application.Quit(0);
    }

    private static object ProbeShader(string name, string resourceName)
    {
        var loaded = Resources.Load<Shader>(resourceName);
        var all = Resources.LoadAll<Shader>(resourceName);
        var shader = Shader.Find(name);
        return new
        {
            resourceLoaded = loaded != null,
            resourceName = loaded != null ? loaded.name : "",
            resourceSupported = loaded != null && loaded.isSupported,
            allResources = all.Select(static item => new { item.name, item.isSupported }).ToArray(),
            found = shader != null,
            supported = shader != null && shader.isSupported,
            actualName = shader != null ? shader.name : "",
        };
    }
}
}
