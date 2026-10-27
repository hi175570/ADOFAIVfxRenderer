using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Debug = UnityEngine.Debug;

namespace ADOFAIVfxWorker
{
internal sealed class GameSceneProbe : MonoBehaviour
{
    private static Canvas[]? _canvasCache;
    private static Camera[]? _originalCameraCache;
    private static OriginalTimelineAccess? _timelineAccess;
    private static int _videoSyncDiagnostics;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (!Environment.GetCommandLineArgs().Contains("--probe-game-scene") &&
            !Environment.GetCommandLineArgs().Contains("--probe-game-scene-activate") &&
            !Environment.GetCommandLineArgs().Contains("--probe-game-scene-bootstrap") &&
            !Environment.GetCommandLineArgs().Contains("--render-installed-game")) return;
        var host = new GameObject("ADOFAI Installed Game Scene Probe");
        DontDestroyOnLoad(host);
        host.AddComponent<GameSceneProbe>();
    }

    private IEnumerator Start()
    {
        var routine = StartCore();
        while (true)
        {
            object current;
            try
            {
                if (!routine.MoveNext()) yield break;
                current = routine.Current;
            }
            catch (Exception exception)
            {
                WriteFatalStatus(exception);
                Debug.LogException(exception);
                Application.Quit(1);
                Process.GetCurrentProcess().Kill();
                yield break;
            }
            yield return current;
        }
    }

    private IEnumerator StartCore()
    {
        PrepareInstalledRuntime();
        var configuredChartPath = GetArgument("--chart");
        if (!string.IsNullOrWhiteSpace(configuredChartPath))
        {
            var configuredPlaceholderMaximumSide = 0;
            long configuredBudgetBytes = 0;
            var configuredJobPath = GetArgument("--job");
            if (!string.IsNullOrWhiteSpace(configuredJobPath) && File.Exists(configuredJobPath))
            {
                var configuredJob = JsonConvert.DeserializeObject<InstalledWorkerJob>(
                    File.ReadAllText(configuredJobPath));
                configuredPlaceholderMaximumSide = configuredJob?.placeholderMaximumSide ?? 0;
                configuredBudgetBytes = configuredJob?.textureMemoryBudgetBytes ?? 0;
            }
            DynamicTextureStreaming.Configure(
                configuredChartPath,
                configuredPlaceholderMaximumSide,
                configuredBudgetBytes);
        }
        var operation = SceneManager.LoadSceneAsync(1, LoadSceneMode.Additive);
        if (operation == null) throw new InvalidOperationException("Installed scnGame scene was not registered at build index 1.");
        operation.allowSceneActivation = false;
        while (operation.progress < 0.9f) yield return null;
        Debug.Log($"GAME_SCENE_PRELOAD progress={operation.progress:0.000} activation={operation.allowSceneActivation}");
        if (!Environment.GetCommandLineArgs().Contains("--probe-game-scene-activate") &&
            !Environment.GetCommandLineArgs().Contains("--probe-game-scene-bootstrap") &&
            !Environment.GetCommandLineArgs().Contains("--render-installed-game"))
        {
            yield return null;
            Application.Quit(0);
            yield break;
        }
        operation.allowSceneActivation = true;
        while (!operation.isDone) yield return null;
        var scene = SceneManager.GetSceneByBuildIndex(1);
        Debug.Log($"GAME_SCENE loaded={scene.isLoaded} name={scene.name} roots={scene.rootCount}");
        foreach (var root in scene.GetRootGameObjects())
            Debug.Log($"GAME_SCENE_ROOT {root.name} active={root.activeSelf} components={string.Join(",", root.GetComponents<Component>().Where(item => item != null).Select(item => item.GetType().FullName))}");
        if (Environment.GetCommandLineArgs().Contains("--probe-game-scene-bootstrap") ||
            Environment.GetCommandLineArgs().Contains("--render-installed-game"))
        {
            var roots = scene.GetRootGameObjects().ToDictionary(item => item.name, StringComparer.Ordinal);
            var chartPath = GetArgument("--chart")
                            ?? throw new InvalidOperationException("--probe-game-scene-bootstrap requires --chart.");
            RestoreInstalledCameraReferences(roots["CamParent"]);
            var guiChildren = roots["GUI"].transform.Cast<Transform>().Select(child => child.gameObject).ToArray();
            foreach (var child in guiChildren) child.SetActive(false);
            foreach (var name in new[]
                     {
                         "Conductor", "Level Maker", "PlayerManager", "GUI", "Controller", "CamParent", "Vfx",
                         "scnGame", "Decoration Container", "CustomBackgroundSprite", "TutorialBackground1",
                     })
            {
                if (!roots.TryGetValue(name, out var root)) continue;
                root.SetActive(true);
                if (name == "CamParent") RestoreInstalledCameraReferences(root);
                if (name == "Controller")
                {
                    var controller = root.GetComponent("scrController");
                    var lineMaterial = controller?.GetType()
                        .GetField("lineMaterial", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(controller) as Material;
                    var installedScrollingShader = Resources.Load<Shader>("adofai_scrollingsprite");
                    if (lineMaterial == null || installedScrollingShader == null)
                        throw new InvalidOperationException("Original controller line material did not initialize.");
                    lineMaterial.shader = installedScrollingShader;
                    Debug.Log("GAME_SCENE_CONTROLLER original scrolling shader restored");
                    foreach (var child in guiChildren) child.SetActive(true);
                    foreach (var canvas in roots["GUI"].GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
                }
                Debug.Log($"GAME_SCENE_ACTIVATE {name}");
            }
            Debug.Log($"GAME_SCENE_BOOTSTRAP original activation path={chartPath}");
            var game = roots["scnGame"].GetComponent("scnGame")
                       ?? throw new InvalidOperationException("Activated scene has no scnGame component.");
            var assembly = game.GetType().Assembly;
            IList? floors = null;
            for (var frame = 0; frame < 1800; frame++)
            {
                var levelMaker = assembly.GetType("scrLevelMaker", true)!
                    .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                floors = levelMaker?.GetType().GetField("listFloors", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(levelMaker) as IList;
                if (floors is { Count: > 1 }) break;
                yield return null;
            }
            if (floors is not { Count: > 1 })
                throw new InvalidOperationException("Original game did not finish creating level floors.");
            var floorCount = floors?.Count ?? -1;
            var manager = assembly.GetType("scrDecorationManager", true)!
                .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var decorationCount = (manager?.GetType().GetField("allDecorations", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(manager) as ICollection)?.Count ?? -1;
            Debug.Log($"GAME_SCENE_COUNTS floors={floorCount} decorations={decorationCount}");
            if (!Environment.GetCommandLineArgs().Contains("--render-installed-game") && floors != null)
            for (var index = 0; index < Math.Min(10, floors.Count); index++)
            {
                var floor = floors[index];
                var entry = floor?.GetType().GetField("entryTime", BindingFlags.Instance | BindingFlags.Public)?.GetValue(floor);
                var position = (floor as Component)?.transform.position;
                Debug.Log($"GAME_SCENE_FLOOR index={index} entry={entry} position={position}");
            }
            if (!Environment.GetCommandLineArgs().Contains("--render-installed-game"))
            {
                var componentCounts = FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(item => item != null && item.GetType().Assembly == assembly)
                    .GroupBy(item => item.GetType().FullName)
                    .Select(group => (Type: group.Key, Count: group.Count()))
                    .OrderByDescending(item => item.Count)
                    .Take(40);
                foreach (var item in componentCounts)
                    Debug.Log($"GAME_SCENE_COMPONENTS type={item.Type} count={item.Count}");
            }
            if (Environment.GetCommandLineArgs().Contains("--render-installed-game"))
            {
                yield return RunInstalledRenderSafely(RenderInstalledVideo(scene, assembly, floors, floorCount));
                yield break;
            }
            var frameOutput = GetArgument("--frame-output");
            if (!string.IsNullOrWhiteSpace(frameOutput))
            {
                var frameTime = float.TryParse(GetArgument("--frame-time"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedTime)
                    ? parsedTime
                    : 0f;
                yield return StartOriginalPlayback(assembly, 60);
                yield return AdvanceOriginalPlayback(scene, assembly, null, frameTime, 60);
                foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    canvas.enabled = false;
                LogOriginalFrameState(assembly, frameTime);
                var fullOutput = Path.GetFullPath(frameOutput);
                Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
                WriteRenderTexturePng(GetOriginalSceneTarget(scene), fullOutput);
                Debug.Log($"GAME_SCENE_FRAME path={fullOutput} exists={File.Exists(fullOutput)} time={frameTime:0.###}");
            }
        }
        yield return null;
        Application.Quit(0);
    }

    private static void PrepareInstalledRuntime()
    {
        foreach (var resourceName in new[]
                 {
                     "adofai_scrollingsprite", "adofai_holdshader", "adofai_tileblur", "adofai_overlay",
                     "adofai_polarscreen", "adofai_screentile", "adofai_screenscroll",
                 })
        {
            var shader = Resources.Load<Shader>(resourceName);
            var found = shader == null ? null : Shader.Find(shader.name);
            Debug.Log($"GAME_SCENE_SHADER resource={resourceName} loaded={shader != null} name={shader?.name} find={found != null}");
        }
        var installedShaders = Resources.LoadAll<Shader>("");
        Debug.Log($"GAME_SCENE_PRELOAD_SHADERS count={installedShaders.Length}");
        var managed = Path.Combine(Application.dataPath, "Managed");
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            var path = Path.Combine(managed, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
                           .FirstOrDefault(item => item.GetName().Name == "Assembly-CSharp")
                       ?? Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        var gcs = assembly.GetType("GCS", true)!;
        gcs.GetField("d_recording", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, true);
        var startup = assembly.GetType("ADOStartup", true)!;
        startup.GetMethod("SetupLevelEventsInfo", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
        startup.GetMethod("SetupDLCs", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
        startup.GetMethod("CacheShaderPropertyIDs", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
        var decorationManagerType = assembly.GetType("scrDecorationManager", true)!;
        decorationManagerType.GetField("tileShader", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(null, Resources.Load<Shader>("adofai_spritestile"));
        var shaderResourcesType = assembly.GetType("BlendModes.ShaderResources", true)!;
        var shaderResources = shaderResourcesType.GetMethod("Load", BindingFlags.Static | BindingFlags.Public)
            ?.Invoke(null, null) ?? throw new InvalidOperationException("Installed blend-mode shader resources are unavailable.");
        var visualDecorationShader = shaderResourcesType
            .GetMethod("GetShaderByName", BindingFlags.Instance | BindingFlags.Public)
            ?.Invoke(shaderResources, new object[] { "Hidden/BlendModes/VisualDeco/Grab" }) as Shader
            ?? throw new InvalidOperationException("Installed VisualDeco blend shader is unavailable.");
        decorationManagerType.GetField("visualDecoShader", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(null, visualDecorationShader);
        assembly.GetType("Persistence", true)!
            .GetMethod("Load", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
        assembly.GetType("Persistence", true)!
            .GetMethod("SetChosenAsynchronousInput", BindingFlags.Static | BindingFlags.Public)
            ?.Invoke(null, new object[] { false });

        var constantsType = assembly.GetType("RDConstants", true)!;
        var constants = constantsType.GetProperty("data", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
                        ?? throw new InvalidOperationException("Installed RDConstants resource is unavailable.");
        foreach (var pair in new[]
                 {
                     (Field: "holdShader", Resource: "adofai_holdshader"),
                     (Field: "tileBlurShader", Resource: "adofai_tileblur"),
                     (Field: "overlayShader", Resource: "adofai_overlay"),
                 })
        {
            var installedShader = Resources.Load<Shader>(pair.Resource)
                                  ?? throw new InvalidOperationException($"Installed shader is unavailable: {pair.Resource}");
            constantsType.GetField(pair.Field, BindingFlags.Instance | BindingFlags.Public)
                ?.SetValue(constants, installedShader);
        }
        var rewiredPrefab = constantsType.GetField("prefab_rewiredManager", BindingFlags.Instance | BindingFlags.Public)
                                ?.GetValue(constants) as UnityEngine.Object
                            ?? throw new InvalidOperationException("Installed Rewired manager prefab is unavailable.");
        var inputManager = Instantiate(rewiredPrefab) as Component
                           ?? throw new InvalidOperationException("Installed Rewired manager did not instantiate as a component.");
        DontDestroyOnLoad(inputManager.gameObject);
        assembly.GetType("RDInput", true)!
            .GetMethod("Setup", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, new object[] { inputManager });
        assembly.GetType("scrPlayerManager", true)!
            .GetMethod("Setup", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
        var chartPath = GetArgument("--chart");
        if (!string.IsNullOrWhiteSpace(chartPath))
        {
            gcs.GetField("internalLevelName", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, null);
            gcs.GetField("customLevelPaths", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, new[] { chartPath });
            gcs.GetField("customLevelIndex", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, 0);
        }
    }

    private static string? GetArgument(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index + 1 < arguments.Length; index++)
            if (arguments[index] == name) return arguments[index + 1];
        return null;
    }

    private static void RestoreInstalledCameraReferences(GameObject cameraRoot)
    {
        var prefab = Resources.Load<GameObject>("adofai_camera")
                     ?? throw new InvalidOperationException("Installed camera prefab is unavailable.");
        var templates = prefab.GetComponentsInChildren<Component>(true)
            .Where(item => item != null)
            .GroupBy(item => item.GetType())
            .ToDictionary(group => group.Key, group => group.ToArray());
        var indices = new System.Collections.Generic.Dictionary<Type, int>();
        var restoredObjects = 0;
        var restoredShaders = 0;
        var restoredMaterials = 0;
        var components = cameraRoot.GetComponentsInChildren<Component>(true).Where(item => item != null).ToArray();
        foreach (var component in components)
        {
            var type = component.GetType();
            Component? template = null;
            if (templates.TryGetValue(type, out var candidates))
            {
                var index = indices.TryGetValue(type, out var current) ? current : 0;
                indices[type] = index + 1;
                if (index < candidates.Length) template = candidates[index];
            }
            var fields = new System.Collections.Generic.List<FieldInfo>();
            for (var declaring = type; declaring != null && declaring != typeof(MonoBehaviour); declaring = declaring.BaseType)
                fields.AddRange(declaring.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            var installedShader = GameFilterBridge.LoadInstalledShader(type.Name);
            foreach (var field in fields)
            {
                if (field.IsStatic || field.IsInitOnly || !typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                    continue;
                if (field.FieldType == typeof(Shader) && installedShader != null)
                {
                    field.SetValue(component, installedShader);
                    restoredShaders++;
                    continue;
                }
                if (field.FieldType == typeof(Material) && installedShader != null &&
                    field.GetValue(component) is not Material)
                {
                    field.SetValue(component, new Material(installedShader) { hideFlags = HideFlags.HideAndDontSave });
                    restoredMaterials++;
                    continue;
                }
                if (field.GetValue(component) is UnityEngine.Object existing && existing != null) continue;
                var installed = template == null ? null : field.GetValue(template) as UnityEngine.Object;
                if (installed == null || !installed) continue;
                field.SetValue(component, installed);
                restoredObjects++;
            }
        }
        Debug.Log($"GAME_SCENE_CAMERA_REFERENCES components={components.Length} shaders={restoredShaders} materials={restoredMaterials} objects={restoredObjects}");
    }

    private static IEnumerator StartOriginalPlayback(Assembly assembly, int framesPerSecond)
    {
        Time.captureFramerate = Math.Max(1, framesPerSecond);
        AudioListener.pause = false;
        AudioListener.volume = 0f;
        var controller = assembly.GetType("scrController", true)!
            .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
            ?? throw new InvalidOperationException("Original controller did not initialize.");
        var customLevelType = assembly.GetType("scnGame", true)!;
        var customLevel = customLevelType.GetField("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
                          ?? customLevelType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
            ?? throw new InvalidOperationException("Original custom level runtime did not initialize.");
        var isLoadingField = customLevel.GetType().GetField("isLoading", BindingFlags.Instance | BindingFlags.Public);
        var ready = false;
        for (var frame = 0; frame < 1800; frame++)
        {
            DisableAllCanvases(refresh: true);
            if (isLoadingField?.GetValue(customLevel) is bool isLoading && !isLoading)
            {
                ready = true;
                break;
            }
            yield return null;
        }
        if (!ready) throw new InvalidOperationException("Original level did not finish its load-and-play initialization.");

        controller.GetType().GetField("levelWasSkipped", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(controller, true);
        var conductor = assembly.GetType("scrConductor", true)!
            .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
            ?? throw new InvalidOperationException("Original conductor did not initialize.");
        var hasSongStartedField = conductor.GetType().GetField("hasSongStarted", BindingFlags.Instance | BindingFlags.Public);
        var started = false;
        for (var frame = 0; frame < 600; frame++)
        {
            DisableAllCanvases(refresh: true);
            if (hasSongStartedField?.GetValue(conductor) is true)
            {
                started = true;
                break;
            }
            yield return null;
        }
        if (!started) throw new InvalidOperationException("Original level did not enter playback.");
        DriveOriginalTimeline(assembly, 0d);
        DisableAllCanvases(refresh: true);
        Debug.Log("GAME_SCENE_PLAYBACK original load/play/start lifecycle ready");
    }

    private static IEnumerator AdvanceOriginalPlayback(
        Scene scene,
        Assembly assembly,
        DynamicTextureStreaming? textureStreaming,
        float targetSeconds,
        int framesPerSecond)
    {
        var frameCount = Math.Max(0, Mathf.CeilToInt(targetSeconds * framesPerSecond));
        var endOfFrame = new WaitForEndOfFrame();
        for (var frame = 0; frame < frameCount; frame++)
        {
            var time = frame / (double)framesPerSecond;
            DriveOriginalTimeline(assembly, time);
            yield return SynchronizeBackgroundVideo(assembly, time);
            DisableAllCanvases();
            yield return endOfFrame;
            textureStreaming?.Update();
            RenderOriginalScene(scene);
            textureStreaming?.CaptureVisibility();
        }
        DriveOriginalTimeline(assembly, targetSeconds);
        yield return SynchronizeBackgroundVideo(assembly, targetSeconds);
        DisableAllCanvases();
        yield return endOfFrame;
        textureStreaming?.Update();
        RenderOriginalScene(scene);
        textureStreaming?.CaptureVisibility();
    }

    private static void DriveOriginalTimeline(Assembly assembly, double songTimeSeconds)
    {
        (_timelineAccess ??= new OriginalTimelineAccess(assembly)).Seek(songTimeSeconds);
    }

    private static IEnumerator SynchronizeBackgroundVideo(Assembly assembly, double songTimeSeconds)
    {
        var timeline = _timelineAccess ??= new OriginalTimelineAccess(assembly);
        if (!timeline.TryGetBackgroundVideoTime(songTimeSeconds, out var video, out var videoTime))
            yield break;

        if (!video.isPrepared)
        {
            video.Prepare();
            for (var frame = 0; frame < 600 && !video.isPrepared; frame++) yield return null;
            if (!video.isPrepared)
                throw new InvalidOperationException($"Background video did not prepare: {video.url}");
        }

        if (videoTime < 0d)
        {
            if (video.isPlaying || video.isPaused) video.Stop();
            yield break;
        }

        if (video.isLooping && video.length > 0d)
            videoTime %= video.length;
        else if (video.length > 0d)
            videoTime = Math.Min(videoTime, Math.Max(0d, video.length - 0.000001d));

        if (video.isPlaying) video.Pause();
        var targetFrame = video.frameRate > 0f
            ? (long)Math.Floor(videoTime * video.frameRate + 0.000001d)
            : -1L;
        if (video.texture != null && targetFrame >= 0L && video.frame >= 0L &&
            Math.Abs(video.frame - targetFrame) <= 1L)
        {
            LogVideoSync(video, songTimeSeconds, videoTime, targetFrame);
            yield break;
        }

        var decodedFrameReady = false;
        void OnFrameReady(VideoPlayer _, long __) => decodedFrameReady = true;
        video.sendFrameReadyEvents = true;
        video.frameReady += OnFrameReady;
        try
        {
            video.time = videoTime;
            video.Play();
            for (var frame = 0; frame < 180; frame++)
            {
                if (decodedFrameReady || (video.texture != null && targetFrame >= 0L && video.frame >= 0L &&
                                          Math.Abs(video.frame - targetFrame) <= 1L))
                {
                    video.Pause();
                    LogVideoSync(video, songTimeSeconds, videoTime, targetFrame);
                    yield break;
                }
                yield return null;
            }
            throw new InvalidOperationException(
                $"Background video seek timed out at song={songTimeSeconds:0.###}, video={videoTime:0.###}, frame={video.frame}, targetFrame={targetFrame}.");
        }
        finally
        {
            video.frameReady -= OnFrameReady;
            if (video.isPlaying) video.Pause();
        }
    }

    private static void LogVideoSync(VideoPlayer video, double songTime, double videoTime, long targetFrame)
    {
        if (_videoSyncDiagnostics++ > 0) return;
        Debug.Log(
            $"GAME_VIDEO_SYNC song={songTime:0.###} video={videoTime:0.###} frame={video.frame}/{targetFrame} " +
            $"prepared={video.isPrepared} playing={video.isPlaying} paused={video.isPaused} " +
            $"mode={video.renderMode} camera={video.targetCamera?.name ?? "<null>"} " +
            $"texture={video.texture?.name ?? "<null>"}:{video.texture?.width}x{video.texture?.height} " +
            $"targetTexture={video.targetTexture?.name ?? "<null>"} aspect={video.aspectRatio}");
    }

    private static void DisableAllCanvases(bool refresh = false)
    {
        if (refresh || _canvasCache == null)
            _canvasCache = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var canvas in _canvasCache)
        {
            if (canvas != null) canvas.enabled = false;
        }
    }

    private static void LogOriginalFrameState(Assembly assembly, double requestedTime)
    {
        var conductor = assembly.GetType("scrConductor", true)!
            .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var controller = assembly.GetType("scrController", true)!
            .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var cameraType = assembly.GetType("scrCamera", true)!;
        var camera = (cameraType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
                      ?? cameraType.GetField("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)) as Component;
        var playerManager = assembly.GetType("scrPlayerManager", true)!
            .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var songPosition = conductor?.GetType().GetProperty("songposition_minusi", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(conductor);
        var controllerState = controller?.GetType().GetProperty("state", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(controller);
        var auto = assembly.GetType("RDC", true)!
            .GetProperty("auto", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var cameraObject = camera?.GetComponent<Camera>();
        var vfx = assembly.GetType("scrVfxPlus", true)!
            .GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var vfxIndex = vfx?.GetType().GetField("currentVfxIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(vfx);
        var effects = vfx?.GetType().GetField("effects", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(vfx) as IList;
        Debug.Log($"GAME_SCENE_FRAME_STATE requested={requestedTime:0.###} song={songPosition} state={controllerState} auto={auto} " +
                  $"camPos={camera?.transform.position} camLocal={camera?.transform.localPosition} camParent={camera?.transform.parent?.position} " +
                  $"ortho={cameraObject?.orthographicSize} enabled={cameraObject?.enabled} vfxIndex={vfxIndex}/{effects?.Count}");
        if (effects != null && vfxIndex != null)
        for (var index = Convert.ToInt32(vfxIndex); index < Math.Min(effects.Count, Convert.ToInt32(vfxIndex) + 5); index++)
        {
            var effect = effects[index];
            var effectType = effect?.GetType();
            var startTime = effectType?.GetField("startTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(effect);
            var triggered = effectType?.GetField("triggered", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(effect);
            Debug.Log($"GAME_SCENE_NEXT_VFX index={index} type={effectType?.Name} start={startTime} triggered={triggered}");
        }
        var activeRenderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy)
            .GroupBy(renderer => renderer.GetType().Name)
            .Select(group => $"{group.Key}:{group.Count()}");
        Debug.Log($"GAME_SCENE_RENDERERS {string.Join(",", activeRenderers)}");
        var objectDecorationType = assembly.GetType("scrObjectDecoration", true)!;
        var objectDecorations = FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(component => component != null && component.GetType() == objectDecorationType)
            .Where(component =>
            {
                var tags = component.GetType().BaseType?.GetField("tags", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(component) as IEnumerable;
                return tags != null && tags.Cast<object>().Any(tag => tag?.ToString()?.Contains("p1MultiTrack") == true);
            })
            .Take(12);
        var objectIndex = 0;
        foreach (var decoration in objectDecorations)
        {
            var decorationType = decoration.GetType();
            var tags = decorationType.BaseType?.GetField("tags", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(decoration) as IEnumerable;
            var kind = decorationType.GetField("objectType", BindingFlags.Instance | BindingFlags.Public)?.GetValue(decoration);
            var floor = decorationType.GetField("floor", BindingFlags.Instance | BindingFlags.Public)?.GetValue(decoration) as Component;
            var opacity = floor?.GetType().GetField("opacity", BindingFlags.Instance | BindingFlags.Public)?.GetValue(floor);
            var color = floor?.GetType().GetField("specialColor1", BindingFlags.Instance | BindingFlags.Public)?.GetValue(floor);
            var floorRenderer = floor?.GetType().GetField("floorRenderer", BindingFlags.Instance | BindingFlags.Public)?.GetValue(floor);
            var renderer = floorRenderer?.GetType().GetField("renderer", BindingFlags.Instance | BindingFlags.Public)?.GetValue(floorRenderer) as Renderer;
            Debug.Log($"GAME_SCENE_OBJECT index={objectIndex++} kind={kind} tags={string.Join("|", tags?.Cast<object>() ?? Array.Empty<object>())} name={decoration.name} active={decoration.gameObject.activeInHierarchy} " +
                      $"pos={decoration.transform.position} scale={decoration.transform.lossyScale} floorActive={floor?.gameObject.activeInHierarchy} " +
                      $"opacity={opacity} color={color} layer={renderer?.gameObject.layer} rendererEnabled={renderer?.enabled} " +
                      $"shader={renderer?.sharedMaterial?.shader?.name} materialColor={renderer?.sharedMaterial?.color}");
        }
        var players = playerManager?.GetType().GetProperty("players", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(playerManager) as IEnumerable;
        if (players == null) return;
        foreach (var player in players)
        {
            if (player == null) continue;
            var system = player.GetType().GetField("planetarySystem", BindingFlags.Instance | BindingFlags.Public)?.GetValue(player);
            var chosen = system?.GetType().GetField("chosenPlanet", BindingFlags.Instance | BindingFlags.Public)?.GetValue(system) as Component;
            var floor = chosen?.GetType().GetField("currfloor", BindingFlags.Instance | BindingFlags.Public)?.GetValue(chosen);
            var seq = floor?.GetType().GetField("seqID", BindingFlags.Instance | BindingFlags.Public)?.GetValue(floor);
            Debug.Log($"GAME_SCENE_PLAYER chosen={chosen?.name} pos={chosen?.transform.position} floor={seq}");
        }
    }

    private static void CaptureInstalledCameras(Scene scene, string outputPath)
    {
        var target = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32)
        {
            name = "ADOFAI Installed Scene Capture",
        };
        target.Create();
        RenderInstalledCameras(scene, target, outputPath, logCameras: true);
        var previousActive = RenderTexture.active;
        RenderTexture.active = target;
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
        texture.Apply(false, false);
        File.WriteAllBytes(outputPath, texture.EncodeToPNG());
        RenderTexture.active = previousActive;
        Destroy(texture);
        target.Release();
        Destroy(target);
    }

    private static void CaptureInstalledScreen(string outputPath)
    {
        var previousActive = RenderTexture.active;
        RenderTexture.active = null;
        var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
        texture.Apply(false, false);
        File.WriteAllBytes(outputPath, texture.EncodeToPNG());
        RenderTexture.active = previousActive;
        Destroy(texture);
    }

    private static void RenderInstalledCameras(Scene scene, RenderTexture target, string outputPath, bool logCameras = false)
    {
        var cameras = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
            .Where(camera => camera != null && camera.enabled && camera.gameObject.activeInHierarchy)
            .OrderBy(camera => camera.targetTexture == null ? 1 : 0)
            .ThenBy(camera => camera.depth)
            .ToArray();
        if (cameras.Length == 0) throw new InvalidOperationException("Installed scene has no enabled render cameras.");
        var previousActive = RenderTexture.active;
        RenderTexture.active = target;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = previousActive;
        foreach (var camera in cameras)
        {
            var previousTarget = camera.targetTexture;
            if (previousTarget == null) camera.targetTexture = target;
            camera.Render();
            if (logCameras && previousTarget != null)
            {
                var layerPath = Path.Combine(
                    Path.GetDirectoryName(outputPath)!,
                    Path.GetFileNameWithoutExtension(outputPath) + "-" + camera.name + ".png");
                WriteRenderTexturePng(previousTarget, layerPath);
            }
            camera.targetTexture = previousTarget;
            if (logCameras)
                Debug.Log($"GAME_SCENE_CAMERA_RENDER name={camera.name} depth={camera.depth} clear={camera.clearFlags} mask={camera.cullingMask} " +
                          $"pos={camera.transform.position} local={camera.transform.localPosition} ortho={camera.orthographicSize} rect={camera.rect} " +
                          $"internalTarget={previousTarget?.name ?? "<screen>"} targetSize={previousTarget?.width}x{previousTarget?.height}");
        }
    }

    private static void WriteRenderTexturePng(RenderTexture source, string outputPath)
    {
        var previousActive = RenderTexture.active;
        RenderTexture.active = source;
        var texture = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
        texture.Apply(false, false);
        File.WriteAllBytes(outputPath, texture.EncodeToPNG());
        RenderTexture.active = previousActive;
        Destroy(texture);
    }

    private static RenderTexture GetOriginalSceneTarget(Scene scene)
    {
        return GetOriginalSceneCameras(scene)
                   .Where(camera => camera != null && camera.enabled && camera.gameObject.activeInHierarchy && camera.targetTexture != null)
                   .Select(camera => camera.targetTexture)
                   .LastOrDefault()
               ?? throw new InvalidOperationException("Original scene camera RenderTexture is unavailable.");
    }

    private static RenderTexture RenderOriginalScene(Scene scene)
    {
        RenderTexture? finalTarget = null;
        foreach (var camera in GetOriginalSceneCameras(scene))
        {
            if (camera == null || !camera.enabled || !camera.gameObject.activeInHierarchy || camera.targetTexture == null)
                continue;
            camera.Render();
            finalTarget = camera.targetTexture;
        }
        return finalTarget ?? throw new InvalidOperationException("Original scene has no enabled internal cameras.");
    }

    private static Camera[] GetOriginalSceneCameras(Scene scene) => _originalCameraCache ??=
        scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Camera>(true))
            .Where(static camera => camera != null)
            .OrderBy(static camera => camera.depth)
            .ToArray();

    private IEnumerator RenderInstalledVideo(Scene scene, Assembly assembly, IList? floors, int floorCount)
    {
        var jobPath = GetArgument("--job") ?? throw new InvalidOperationException("Installed game rendering requires --job.");
        var job = JsonConvert.DeserializeObject<InstalledWorkerJob>(File.ReadAllText(jobPath))
                  ?? throw new InvalidDataException("render-job.json is empty.");
        var status = new InstalledWorkerStatus
        {
            jobId = job.jobId,
            state = "loading",
            message = "正在由游戏原版场景建立 Video Pro……",
            outputPath = job.outputPath,
        };
        WriteInstalledStatus(job.statusPath, status);
        yield return StartOriginalPlayback(assembly, job.framesPerSecond);
        using var textureStreaming = DynamicTextureStreaming.Create(
            assembly,
            job.chartPath,
            job.textureMemoryBudgetBytes,
            job.placeholderMaximumSide);
        textureStreaming?.Update();
        if (Environment.GetCommandLineArgs().Contains("--diagnose-memory"))
            LogMemoryBreakdown("level-ready");
        Directory.CreateDirectory(Path.GetDirectoryName(job.outputPath)!);
        Directory.CreateDirectory(job.previewDirectory);
        var chartDuration = floorCount > 0 && floors != null
            ? Convert.ToSingle(floors[floorCount - 1]?.GetType()
                .GetField("entryTime", BindingFlags.Instance | BindingFlags.Public)?.GetValue(floors[floorCount - 1]) ?? 0f)
            : 0f;
        status.chartDurationSeconds = chartDuration;
        var duration = job.durationLimitSeconds > 0f
            ? job.durationLimitSeconds
            : Math.Max(0f, chartDuration - job.renderStartSeconds) + Math.Max(0f, job.tailSeconds);
        var totalFrames = Math.Max(1, Mathf.CeilToInt(duration * job.framesPerSecond));
        status.totalFrames = totalFrames;
        status.state = "rendering";
        status.message = "游戏原版场景正在逐帧渲染 Video Pro……";
        WriteInstalledStatus(job.statusPath, status);

        var originalTarget = GetOriginalSceneTarget(scene);
        var captureWidth = originalTarget.width;
        var captureHeight = originalTarget.height;
        var frameTexture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGBA32, false, false);
        var temporaryOutput = Path.Combine(
            Path.GetDirectoryName(job.outputPath)!,
            $".{Path.GetFileNameWithoutExtension(job.outputPath)}.{job.jobId}.rendering{Path.GetExtension(job.outputPath)}");
        if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
        using var ffmpeg = StartInstalledEncoder(job, captureWidth, captureHeight, temporaryOutput);
        var watch = Stopwatch.StartNew();
        var previewWatch = Stopwatch.StartNew();
        var endOfFrame = new WaitForEndOfFrame();
        Time.captureFramerate = job.framesPerSecond;
        if (job.renderStartSeconds > 0f)
            yield return AdvanceOriginalPlayback(
                scene,
                assembly,
                textureStreaming,
                job.renderStartSeconds,
                job.framesPerSecond);
        for (var frame = 0; frame < totalFrames; frame++)
        {
            var time = job.renderStartSeconds + frame / (float)job.framesPerSecond;
            DriveOriginalTimeline(assembly, time);
            yield return SynchronizeBackgroundVideo(assembly, time);
            DisableAllCanvases();
            yield return endOfFrame;
            textureStreaming?.Update();
            originalTarget = RenderOriginalScene(scene);
            textureStreaming?.CaptureVisibility();
            var previousActive = RenderTexture.active;
            RenderTexture.active = originalTarget;
            frameTexture.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0, false);
            RenderTexture.active = previousActive;
            var raw = frameTexture.GetRawTextureData();
            ffmpeg.StandardInput.BaseStream.Write(raw, 0, raw.Length);
            var completed = frame + 1;
            if (Environment.GetCommandLineArgs().Contains("--diagnose-memory") && completed % 600 == 0)
                LogMemoryBreakdown($"frame-{completed}");
            var speed = completed / Math.Max(0.001, watch.Elapsed.TotalSeconds);
            status.currentFrame = completed;
            status.percent = completed * 100f / totalFrames;
            status.renderedFramesPerSecond = (float)speed;
            status.secondsPerFrame = (float)(1d / speed);
            if (previewWatch.Elapsed.TotalSeconds >= 0.5 || completed == totalFrames)
            {
                status.previewPath = WriteInstalledPreview(job.previewDirectory, frameTexture, completed);
                status.message = $"已完成 {completed:N0} / {totalFrames:N0} 帧";
                WriteInstalledStatus(job.statusPath, status);
                previewWatch.Restart();
            }
        }
        Time.captureFramerate = 0;
        ffmpeg.StandardInput.Close();
        ffmpeg.WaitForExit();
        if (ffmpeg.ExitCode != 0) throw new InvalidOperationException($"FFmpeg exited with code {ffmpeg.ExitCode}.");
        if (File.Exists(job.outputPath)) File.Replace(temporaryOutput, job.outputPath, null);
        else File.Move(temporaryOutput, job.outputPath);
        status.state = "complete";
        status.percent = 100f;
        status.message = "游戏原版场景 Video Pro 渲染完成。";
        WriteInstalledStatus(job.statusPath, status);
        Destroy(frameTexture);
        Application.Quit(0);
        yield return null;
        // Some installed game components keep the batch-mode player alive after
        // Application.Quit. At this point FFmpeg is closed and the canonical output and
        // terminal status have both been atomically committed, so force the helper process
        // down instead of leaving a zombie that blocks the next render.
        Process.GetCurrentProcess().Kill();
    }

    private static Process StartInstalledEncoder(InstalledWorkerJob job, int width, int height, string outputPath)
    {
        var encoder = string.IsNullOrWhiteSpace(job.videoEncoder) ? "libopenh264" : job.videoEncoder;
        var preset = encoder == "h264_nvenc" ? " -preset p4 -tune hq" : "";
        var filter = width == job.width && height == job.height
            ? "vflip"
            : $"vflip,scale={job.width}:{job.height}:flags=lanczos";
        var arguments = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "-hide_banner -loglevel warning -y -f rawvideo -pixel_format rgba -video_size {0}x{1} -framerate {2} -i pipe:0 -vf {3} -an -c:v {4}{5} -b:v {6}k -pix_fmt yuv420p \"{7}\"",
            width, height, job.framesPerSecond, filter, encoder, preset, Math.Max(1, job.bitrateKbps),
            outputPath.Replace("\"", "\\\""));
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = job.ffmpegPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(job.outputPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            },
        };
        if (!process.Start()) throw new InvalidOperationException("Unable to start FFmpeg.");
        process.ErrorDataReceived += static (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Debug.LogWarning("FFMPEG " + args.Data);
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static void LogMemoryBreakdown(string phase)
    {
        Debug.Log(
            $"MEMORY_TOTAL phase={phase} " +
            $"allocated={Profiler.GetTotalAllocatedMemoryLong()} " +
            $"reserved={Profiler.GetTotalReservedMemoryLong()} " +
            $"unusedReserved={Profiler.GetTotalUnusedReservedMemoryLong()} " +
            $"monoUsed={Profiler.GetMonoUsedSizeLong()} " +
            $"monoHeap={Profiler.GetMonoHeapSizeLong()} " +
            $"graphics={Profiler.GetAllocatedMemoryForGraphicsDriver()}");
        LogObjectMemory<Texture2D>(phase, "Texture2D");
        LogObjectMemory<RenderTexture>(phase, "RenderTexture");
        var renderTextureGroups = Resources.FindObjectsOfTypeAll<RenderTexture>()
            .Where(static item => item != null)
            .GroupBy(static item => $"{item.name}|{item.width}x{item.height}|{item.format}")
            .Select(static group => new
            {
                Name = group.Key,
                Count = group.Count(),
                Bytes = group.Sum(static item => Profiler.GetRuntimeMemorySizeLong(item)),
            })
            .OrderByDescending(static group => group.Bytes)
            .Take(12);
        foreach (var group in renderTextureGroups)
            Debug.Log($"MEMORY_RT_GROUP phase={phase} count={group.Count} bytes={group.Bytes} key={group.Name}");
        LogObjectMemory<Sprite>(phase, "Sprite");
        LogObjectMemory<Mesh>(phase, "Mesh");
        LogObjectMemory<Material>(phase, "Material");
        var materialGroups = Resources.FindObjectsOfTypeAll<Material>()
            .Where(static item => item != null)
            .GroupBy(static item => item.shader?.name ?? item.name)
            .Select(static group => new { Name = group.Key, Count = group.Count() })
            .OrderByDescending(static group => group.Count)
            .Take(12);
        foreach (var group in materialGroups)
            Debug.Log($"MEMORY_MATERIAL_GROUP phase={phase} count={group.Count} key={group.Name}");
        LogObjectMemory<AudioClip>(phase, "AudioClip");
        var transforms = Resources.FindObjectsOfTypeAll<Transform>();
        Debug.Log($"MEMORY_OBJECTS phase={phase} type=Transform count={transforms.Length}");
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private static void LogObjectMemory<T>(string phase, string label) where T : UnityEngine.Object
    {
        var objects = Resources.FindObjectsOfTypeAll<T>();
        long bytes = 0;
        foreach (var item in objects)
            if (item != null) bytes += Profiler.GetRuntimeMemorySizeLong(item);
        Debug.Log($"MEMORY_OBJECTS phase={phase} type={label} count={objects.Length} bytes={bytes}");
    }

    private static string WriteInstalledPreview(string directory, Texture2D texture, int frame)
    {
        var path = Path.Combine(directory, $"preview-{frame % 2}.jpg");
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, ImageConversion.EncodeToJPG(texture, 85));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
        return path;
    }

    private static void WriteInstalledStatus(string path, InstalledWorkerStatus status)
    {
        var temporary = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temporary, JsonConvert.SerializeObject(status, Formatting.Indented));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private sealed class OriginalTimelineAccess
    {
        private readonly object _conductor;
        private readonly FieldInfo? _songField;
        private readonly FieldInfo? _addOffsetField;
        private readonly FieldInfo? _dspTimeSongField;
        private readonly PropertyInfo? _calibrationProperty;
        private readonly FieldInfo? _separateCountdownTimeField;
        private readonly FieldInfo? _crotchetAtStartField;
        private readonly FieldInfo? _adjustedCountdownTicksField;
        private readonly PropertyInfo? _adjustedCountdownTicksProperty;
        private readonly Type _vfxType;
        private readonly PropertyInfo? _vfxInstanceProperty;
        private readonly FieldInfo? _vfxInstanceField;
        private readonly FieldInfo? _videoBackgroundField;
        private readonly FieldInfo? _videoOffsetField;
        private readonly Type _customLevelType;
        private readonly PropertyInfo? _customLevelInstanceProperty;
        private readonly FieldInfo? _customLevelInstanceField;
        private readonly FieldInfo? _customLevelVideoBackgroundField;
        private PropertyInfo? _pitchProperty;

        public OriginalTimelineAccess(Assembly assembly)
        {
            var conductorType = assembly.GetType("scrConductor", true)!;
            _conductor = conductorType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
                         ?? throw new InvalidOperationException("Original conductor is unavailable while rendering.");
            _songField = conductorType.GetField("song", BindingFlags.Instance | BindingFlags.Public);
            _addOffsetField = conductorType.GetField(
                "addoffset",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _dspTimeSongField = conductorType.GetField(
                "dspTimeSong",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _calibrationProperty = conductorType.GetProperty(
                "calibration_i",
                BindingFlags.Static | BindingFlags.Public);
            _separateCountdownTimeField = conductorType.GetField(
                "separateCountdownTime",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _crotchetAtStartField = conductorType.GetField(
                "crotchetAtStart",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _adjustedCountdownTicksField = conductorType.GetField(
                "adjustedCountdownTicks",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _adjustedCountdownTicksProperty = conductorType.GetProperty(
                "adjustedCountdownTicks",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            _vfxType = assembly.GetType("scrVfxPlus", true)!;
            _vfxInstanceProperty = _vfxType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public);
            _vfxInstanceField = _vfxType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _videoBackgroundField = _vfxType.GetField(
                "videoBG",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _videoOffsetField = _vfxType.GetField(
                "vidOffset",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _customLevelType = assembly.GetType("scnGame", true)!;
            _customLevelInstanceProperty = _customLevelType.GetProperty(
                "instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _customLevelInstanceField = _customLevelType.GetField(
                "instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _customLevelVideoBackgroundField = _customLevelType.GetField(
                "videoBG", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        public void Seek(double songTimeSeconds)
        {
            var song = _songField?.GetValue(_conductor);
            _pitchProperty ??= song?.GetType().GetProperty("pitch", BindingFlags.Instance | BindingFlags.Public);
            var pitch = Math.Max(0.01, Convert.ToDouble(_pitchProperty?.GetValue(song) ?? 1f));
            var addOffset = Convert.ToDouble(_addOffsetField?.GetValue(_conductor) ?? 0d);
            var calibration = Convert.ToDouble(_calibrationProperty?.GetValue(null) ?? 0d);
            _dspTimeSongField?.SetValue(
                _conductor,
                AudioSettings.dspTime - calibration - (songTimeSeconds + addOffset) / pitch);
        }

        public bool TryGetBackgroundVideoTime(
            double songTimeSeconds,
            out VideoPlayer video,
            out double videoTimeSeconds)
        {
            var vfx = _vfxInstanceProperty?.GetValue(null) ?? _vfxInstanceField?.GetValue(null);
            video = _videoBackgroundField?.GetValue(vfx) as VideoPlayer;
            if (video == null)
            {
                var customLevel = _customLevelInstanceProperty?.GetValue(null) ??
                                  _customLevelInstanceField?.GetValue(null);
                video = _customLevelVideoBackgroundField?.GetValue(customLevel) as VideoPlayer;
            }
            videoTimeSeconds = 0d;
            if (video == null || !video.gameObject.activeSelf ||
                (video.clip == null && string.IsNullOrWhiteSpace(video.url)))
                return false;

            var separateCountdown = Convert.ToBoolean(
                _separateCountdownTimeField?.GetValue(_conductor) ?? false);
            var countdownSeconds = 0d;
            if (separateCountdown)
            {
                var crotchetAtStart = Convert.ToDouble(
                    _crotchetAtStartField?.GetValue(_conductor) ?? 0d);
                var countdownTicks = Convert.ToDouble(
                    _adjustedCountdownTicksProperty?.GetValue(_conductor) ??
                    _adjustedCountdownTicksField?.GetValue(_conductor) ?? 0d);
                countdownSeconds = crotchetAtStart * countdownTicks;
            }
            var videoOffset = Convert.ToDouble(_videoOffsetField?.GetValue(vfx) ?? 0d);
            videoTimeSeconds = songTimeSeconds - countdownSeconds + videoOffset;
            return true;
        }
    }

    private static void WriteFatalStatus(Exception exception)
    {
        var jobPath = GetArgument("--job");
        if (string.IsNullOrWhiteSpace(jobPath) || !File.Exists(jobPath)) return;
        var job = JsonConvert.DeserializeObject<InstalledWorkerJob>(File.ReadAllText(jobPath));
        if (job == null) return;
        WriteInstalledStatus(job.statusPath, new InstalledWorkerStatus
        {
            jobId = job.jobId,
            state = "failed",
            message = "游戏原版场景加载或渲染失败。",
            outputPath = job.outputPath,
            error = exception.ToString(),
        });
    }

    private IEnumerator RunInstalledRenderSafely(IEnumerator routine)
    {
        while (true)
        {
            object current;
            try
            {
                if (!routine.MoveNext()) yield break;
                current = routine.Current;
            }
            catch (Exception exception)
            {
                WriteFatalStatus(exception);
                Debug.LogException(exception);
                Application.Quit(1);
                Process.GetCurrentProcess().Kill();
                yield break;
            }
            yield return current;
        }
    }

    [Serializable]
    private sealed class InstalledWorkerJob
    {
        public string jobId = "";
        public string chartPath = "";
        public string outputPath = "";
        public string statusPath = "";
        public string previewDirectory = "";
        public string ffmpegPath = "";
        public int width = 640;
        public int height = 360;
        public int renderWidth;
        public int renderHeight;
        public int framesPerSecond = 30;
        public int bitrateKbps = 2000;
        public float tailSeconds;
        public float durationLimitSeconds;
        public float renderStartSeconds;
        public string? videoEncoder;
        public long textureMemoryBudgetBytes;
        public int placeholderMaximumSide;
    }

    [Serializable]
    private sealed class InstalledWorkerStatus
    {
        public string jobId = "";
        public string state = "idle";
        public string message = "";
        public string outputPath = "";
        public string previewPath = "";
        public string error = "";
        public int[] unbakedDecorationIndices = Array.Empty<int>();
        public int currentFrame;
        public int totalFrames;
        public float percent;
        public float renderedFramesPerSecond;
        public float secondsPerFrame;
        public float chartDurationSeconds;
    }
}
}
