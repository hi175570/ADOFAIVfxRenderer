using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ADOFAIVfxWorker
{
internal sealed class GameSceneProbe : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (!Environment.GetCommandLineArgs().Contains("--probe-game-scene")) return;
        var host = new GameObject("ADOFAI Installed Game Scene Probe");
        DontDestroyOnLoad(host);
        host.AddComponent<GameSceneProbe>();
    }

    private IEnumerator Start()
    {
        var operation = SceneManager.LoadSceneAsync(1, LoadSceneMode.Additive);
        if (operation == null) throw new InvalidOperationException("Installed scnGame scene was not registered at build index 1.");
        operation.allowSceneActivation = false;
        while (operation.progress < 0.9f) yield return null;
        Debug.Log($"GAME_SCENE_PRELOAD progress={operation.progress:0.000} activation={operation.allowSceneActivation}");
        yield return null;
        Application.Quit(0);
        yield break;
        #pragma warning disable CS0162
        operation.allowSceneActivation = true;
        while (!operation.isDone) yield return null;
        var scene = SceneManager.GetSceneByBuildIndex(1);
        Debug.Log($"GAME_SCENE loaded={scene.isLoaded} name={scene.name} roots={scene.rootCount}");
        foreach (var root in scene.GetRootGameObjects())
            Debug.Log($"GAME_SCENE_ROOT {root.name} active={root.activeSelf} components={string.Join(",", root.GetComponents<Component>().Where(item => item != null).Select(item => item.GetType().FullName))}");
        yield return null;
        Application.Quit(0);
        #pragma warning restore CS0162
    }
}
}
