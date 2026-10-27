using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using ADOFAIVfxTimeline;
using Debug = UnityEngine.Debug;

namespace ADOFAIVfxWorker
{
public sealed class WorkerRuntime : MonoBehaviour
{
    private const float TileSize = 1.5f;
    private const float DefaultCameraOrthoSize = 5f;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector2Int> _sourceTextureSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private Material? _tileMaterial;
    private Type? _gameBlendEffectType;
    private PropertyInfo? _gameBlendModeProperty;
    private PropertyInfo? _gameBlendPointSamplingProperty;
    private readonly List<DecorationState> _decorations = new();
    private readonly List<ParticleState> _particles = new();
    private readonly List<ParticleControlEvent> _particleEvents = new();
    private readonly List<MoveEvent> _moves = new();
    private readonly List<MoveEvent> _activeMoves = new();
    private readonly List<CameraEvent> _cameraEvents = new();
    private readonly List<CameraEvent> _activeCameraEvents = new();
    private readonly CameraLogicalState _cameraState = new();
    private readonly List<BackgroundEvent> _backgroundEvents = new();
    private readonly HashSet<int> _unbakedDecorationIndices = new();
    private WorkerJob _job = null!;
    private WorkerStatus _status = null!;
    private Transform _cameraParent = null!;
    private Camera _camera = null!;
    private Camera _backgroundCamera = null!;
    private Camera _staticBackgroundCamera = null!;
    private Process? _ffmpeg;
    private string? _encodingOutputPath;
    private int _nextMove;
    private int _nextCameraEvent;
    private int _nextBackgroundEvent;
    private int _nextParticleEvent;
    private int _eventSequence;
    private float[] _floorTimes = Array.Empty<float>();
    private float[] _floorBpms = Array.Empty<float>();
    private Vector2[] _floorPositions = Array.Empty<Vector2>();
    private Vector2[] _positionedFloorPositions = Array.Empty<Vector2>();
    private Vector2[] _followFrom = Array.Empty<Vector2>();
    private Vector2[] _followTarget = Array.Empty<Vector2>();
    private float[] _followDuration = Array.Empty<float>();
    private AdoTimelineResult _timeline = null!;
    private string _chartDirectory = "";
    private float _pitch = 1f;
    private float _initialZoomRatio = 1f;
    private SpriteRenderer? _backgroundRenderer;
    private OfflineEffectController? _effects;
    private OfflineEffectController? _backgroundEffects;
    private GameTrackRenderer? _trackRenderer;
    private bool _cameraFollowsPlayer;
    private Vector2 _lastPlayerFollowPosition;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (Environment.GetCommandLineArgs().Contains("--probe-game-resources") ||
            Environment.GetCommandLineArgs().Contains("--probe-game-scene") ||
            Environment.GetCommandLineArgs().Contains("--probe-game-scene-activate") ||
            Environment.GetCommandLineArgs().Contains("--probe-game-scene-bootstrap") ||
            Environment.GetCommandLineArgs().Contains("--render-installed-game")) return;
        var host = new GameObject("ADOFAI VFX Worker Runtime");
        DontDestroyOnLoad(host);
        host.AddComponent<WorkerRuntime>();
    }

    private void Awake()
    {
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        HideWindow();
        StartCoroutine(Run());
    }

    private void Update() => HideWindow();

    private IEnumerator Run()
    {
        var jobPath = GetJobPath();
        try
        {
            _job = JsonConvert.DeserializeObject<WorkerJob>(File.ReadAllText(jobPath))
                   ?? throw new InvalidDataException("render-job.json is empty.");
            _status = new WorkerStatus
            {
                jobId = _job.jobId,
                state = "loading",
                message = "独立 Unity Worker 正在解析谱面与贴图……",
                outputPath = _job.outputPath,
            };
            WriteStatus();
            BuildScene();
        }
        catch (Exception exception)
        {
            Fail(exception);
            yield break;
        }

        yield return null;
        yield return RunSafely(RenderFrames());
    }

    private void BuildScene()
    {
        var originalChart = JObject.Parse(NormalizeLegacyJson(File.ReadAllText(_job.chartPath)));
        var chart = JObject.Parse(GameChartConverter.LoadCurrentFormat(_job.chartPath, _job.gameManagedDirectory));
        Debug.Log(
            $"GAME_CHART_CONVERTER sourceVersion={originalChart["settings"]?["version"]} " +
            $"currentVersion={chart["settings"]?["version"]} " +
            $"angles={(originalChart["angleData"] as JArray)?.Count}->{(chart["angleData"] as JArray)?.Count} " +
            $"actions={(originalChart["actions"] as JArray)?.Count}->{(chart["actions"] as JArray)?.Count} " +
            $"decorations={(originalChart["decorations"] as JArray)?.Count}->{(chart["decorations"] as JArray)?.Count}");
        Debug.Log(
            "GAME_CHART_CONVERTER_ACTIONS " +
            string.Join(",", (chart["actions"] as JArray)?.OfType<JObject>()
                .GroupBy(static action => action.Value<string>("eventType") ?? "")
                .OrderByDescending(static group => group.Count())
                .Select(static group => group.Key + "=" + group.Count()) ?? Array.Empty<string>()));
        _chartDirectory = Path.GetDirectoryName(_job.chartPath)!;
        var settings = (JObject?)chart["settings"] ?? new JObject();
        var bpm = settings.Value<float?>("bpm") ?? 100f;
        _pitch = Math.Max(0.01f, (settings.Value<float?>("pitch") ?? 100f) / 100f);
        var actions = (JArray?)chart["actions"] ?? new JArray();
        InitializeGameBlendMode();
        BuildFloorTimeline(chart, settings, actions, bpm);
        _status.chartDurationSeconds = (float)_timeline.DurationSeconds;

        var cameraParentObject = new GameObject("CamParent");
        _cameraParent = cameraParentObject.transform;
        var cameraPrefab = Resources.Load<GameObject>("adofai_camera");
        GameObject cameraObject;
        if (cameraPrefab != null)
        {
            var prefabWasActive = cameraPrefab.activeSelf;
            cameraPrefab.SetActive(false);
            cameraObject = Instantiate(cameraPrefab);
            cameraPrefab.SetActive(prefabWasActive);
            cameraObject.name = "Render Camera";
            cameraObject.SetActive(false);
            StripInstalledCameraGameplayBehaviours(cameraObject);
        }
        else
        {
            cameraObject = new GameObject("Render Camera");
        }
        cameraObject.transform.SetParent(_cameraParent, false);
        _camera = cameraObject.GetComponent<Camera>() ?? cameraObject.AddComponent<Camera>();
        _camera.orthographic = true;
        _initialZoomRatio = Math.Max(0.01f, (settings.Value<float?>("zoom") ?? 100f) / 100f);
        _camera.orthographicSize = DefaultCameraOrthoSize * _initialZoomRatio;
        var initialCameraOffset = ReadVector2(settings["position"], Vector2.zero) * TileSize;
        var initialCameraMode = settings.Value<string>("relativeTo") ?? "Player";
        _cameraState.Initialize(initialCameraMode, settings.Value<float?>("rotation") ?? 0f);
        var initialCameraBase = initialCameraMode == "Tile" ? _positionedFloorPositions[0] : Vector2.zero;
        _cameraParent.position = new Vector3(initialCameraBase.x + initialCameraOffset.x, initialCameraBase.y + initialCameraOffset.y, 0f);
        _camera.transform.localPosition = initialCameraMode == "Player"
            ? new Vector3(_positionedFloorPositions[0].x, _positionedFloorPositions[0].y, -10f)
            : new Vector3(0f, 0f, -10f);
        _camera.transform.localRotation = Quaternion.Euler(0f, 0f, _cameraState.Rotation);
        _camera.clearFlags = CameraClearFlags.Nothing;
        _camera.backgroundColor = ParseColor(settings.Value<string>("backgroundColor") ?? "000000ff");
        _camera.aspect = RenderWidth / (float)RenderHeight;
        _camera.cullingMask = MainCameraMask;
        _camera.depth = -1f;
        _camera.enabled = false;

        var movingBackgroundTransform = cameraObject.transform.Find("BGMovingCam");
        var movingBackgroundObject = movingBackgroundTransform != null
            ? movingBackgroundTransform.gameObject
            : new GameObject("BGMovingCam");
        if (movingBackgroundTransform == null) movingBackgroundObject.transform.SetParent(cameraObject.transform, false);
        _backgroundCamera = movingBackgroundObject.GetComponent<Camera>() ?? movingBackgroundObject.AddComponent<Camera>();
        ConfigureBackgroundCamera(_camera, _backgroundCamera);
        _backgroundCamera.clearFlags = CameraClearFlags.Depth;
        _backgroundCamera.cullingMask = 1 << BackgroundLayer;
        _backgroundCamera.depth = -1.11f;
        _backgroundCamera.enabled = false;

        var staticBackgroundTransform = cameraObject.transform.Find("BGStaticCam");
        var staticBackgroundObject = staticBackgroundTransform != null
            ? staticBackgroundTransform.gameObject
            : new GameObject("BGStaticCam");
        if (staticBackgroundTransform == null) staticBackgroundObject.transform.SetParent(cameraObject.transform, false);
        _staticBackgroundCamera = staticBackgroundObject.GetComponent<Camera>() ?? staticBackgroundObject.AddComponent<Camera>();
        ConfigureBackgroundCamera(_camera, _staticBackgroundCamera);
        _staticBackgroundCamera.clearFlags = CameraClearFlags.SolidColor;
        _staticBackgroundCamera.backgroundColor = _camera.backgroundColor;
        _staticBackgroundCamera.cullingMask = 1 << StaticBackgroundLayer;
        _staticBackgroundCamera.depth = -1.63f;
        _staticBackgroundCamera.enabled = false;
        if (_job.includeFilters || _job.includeEffects)
        {
            _effects = cameraObject.AddComponent<OfflineEffectController>();
            _effects.Configure(_job.bakedFilterKeys, _job.gameManagedDirectory, "Foreground", cameraObject);
            _backgroundEffects = movingBackgroundObject.AddComponent<OfflineEffectController>();
            _backgroundEffects.Configure(_job.bakedFilterKeys, _job.gameManagedDirectory, "Background", movingBackgroundObject);
        }
        _cameraFollowsPlayer = _cameraState.FollowMode;
        _lastPlayerFollowPosition = PlayerFollowPositionAt(0f);

        if (_job.includeTrack)
            _trackRenderer = GameTrackRenderer.Create(
                _job.gameManagedDirectory,
                _floorPositions,
                _floorTimes,
                _floorBpms,
                _pitch,
                settings,
                actions);

        var backgroundObject = new GameObject("Custom Background");
        backgroundObject.layer = BackgroundLayer;
        _backgroundRenderer = backgroundObject.AddComponent<SpriteRenderer>();
        _backgroundRenderer.sortingOrder = short.MinValue;
        _backgroundRenderer.enabled = false;

        var retainedDecorationIndices = new HashSet<int>(_job.retainedDecorationIndices ?? Array.Empty<int>());
        var chartDecorations = (JArray?)chart["decorations"] ?? new JArray();
        for (var decorationIndex = 0; decorationIndex < chartDecorations.Count; decorationIndex++)
        {
            var token = chartDecorations[decorationIndex];
            if (token is JObject decoration && decoration.Value<string>("eventType") == "AddDecoration")
            {
                if (!retainedDecorationIndices.Contains(decorationIndex)) AddDecoration(decoration, decorationIndex);
            }
            else if (token is JObject particle && particle.Value<string>("eventType") == "AddParticle")
            {
                if (!retainedDecorationIndices.Contains(decorationIndex)) AddParticle(particle, decorationIndex);
            }
        }
        ResolveMaskingTargets();

        var repeatRules = BuildRepeatRules(actions);
        foreach (var token in actions)
        {
            if (token is not JObject action)
            {
                continue;
            }

            var eventType = action.Value<string>("eventType");
            foreach (var repeat in GetRepeatedPlacements(action, repeatRules, _floorTimes.Length))
            {
                if (eventType == "MoveDecorations")
                {
                    AddMoveEvent(action, repeat.Floor, repeat.AngleOffset);
                }
                else if (eventType == "MoveCamera")
                {
                    AddCameraEvent(action, repeat.Floor, repeat.AngleOffset);
                }
                else if (eventType == "CustomBackground")
                {
                    AddBackgroundEvent(action, repeat.Floor, repeat.AngleOffset);
                }
                else if (eventType is "SetParticle" or "EmitParticle")
                {
                    AddParticleEvent(action, repeat.Floor, repeat.AngleOffset);
                }
                else if (_trackRenderer != null && GameTrackRenderer.Supports(eventType))
                {
                    _trackRenderer.AddEvent(
                        action,
                        repeat.Floor,
                        repeat.AngleOffset,
                        _timeline,
                        _pitch,
                        _eventSequence++);
                }
                else if (_effects != null && OfflineEffectController.Supports(eventType))
                {
                    _effects.AddEvent(action, repeat.Floor, repeat.AngleOffset, _timeline, _pitch, _eventSequence++);
                    _backgroundEffects?.AddEvent(action, repeat.Floor, repeat.AngleOffset, _timeline, _pitch, _eventSequence++);
                }
            }
        }

        _moves.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Order.CompareTo(right.Order));
        _cameraEvents.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Order.CompareTo(right.Order));
        _backgroundEvents.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Order.CompareTo(right.Order));
        _particleEvents.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Order.CompareTo(right.Order));
        _trackRenderer?.Prepare();
        _effects?.Prepare();
        _backgroundEffects?.Prepare();
        cameraObject.SetActive(true);
        ApplyTimeline(0f);
    }

    private static string NormalizeLegacyJson(string json)
    {
        var normalized = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                normalized.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"')
            {
                inString = true;
                normalized.Append(character);
                continue;
            }
            if (character == ',')
            {
                var next = index + 1;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                if (next < json.Length && json[next] == ',') continue;
            }
            normalized.Append(character);
        }
        return normalized.ToString();
    }

    private const int ForegroundLayer = 7;
    private const int PlanetLayer = 8;
    private const int FloorLayer = 9;
    private const int BackgroundLayer = 10;
    private const int StaticBackgroundLayer = 11;
    private const int CameraLayer = 12;
    private const int MainCameraMask =
        (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) |
        (1 << ForegroundLayer) | (1 << PlanetLayer) | (1 << FloorLayer) |
        (1 << CameraLayer) | (1 << 13) | (1 << 14) | (1 << 15) |
        (1 << 16) | (1 << 19);

    private static void ConfigureBackgroundCamera(Camera source, Camera destination)
    {
        destination.orthographic = source.orthographic;
        // This matches the installed game's level scene: BGMovingCam and
        // BGStaticCam are zero-local-transform children of Camera, but each
        // keeps its own serialized orthographic size at 5. MoveCamera zoom is
        // not copied into the background cameras. Copying a chart's initial
        // zoom here made backgrounds far too wide/tall.
        destination.orthographicSize = DefaultCameraOrthoSize;
        destination.aspect = source.aspect;
        destination.nearClipPlane = source.nearClipPlane;
        destination.farClipPlane = source.farClipPlane;
    }

    private static void StripInstalledCameraGameplayBehaviours(GameObject cameraRoot)
    {
        foreach (var behaviour in cameraRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null) continue;
            DestroyImmediate(behaviour);
        }
    }

    private void BuildFloorTimeline(JObject chart, JObject settings, JArray actions, float initialBpm)
    {
        var angles = ReadAngles(chart);
        var timelineActions = new List<TimelineActionData>(actions.Count);
        var order = 0;
        foreach (var action in actions.OfType<JObject>())
        {
            timelineActions.Add(new TimelineActionData
            {
                Order = order++,
                EventType = action.Value<string>("eventType") ?? "",
                Floor = action.Value<int?>("floor") ?? 0,
                AngleOffset = action.Value<double?>("angleOffset") ?? 0d,
                SpeedType = action.Value<string>("speedType") ?? "",
                BeatsPerMinute = action.Value<double?>("beatsPerMinute") ?? initialBpm,
                BpmMultiplier = action.Value<double?>("bpmMultiplier") ?? 1d,
                Duration = action.Value<double?>("duration") ?? 0d,
                Planets = action.Value<string>("planets") ?? "",
            });
        }

        _timeline = AdoTimelineCalculator.Calculate(angles, timelineActions, initialBpm, _pitch);
        _floorTimes = _timeline.FloorTimesSeconds.Select(static value => (float)value).ToArray();
        _floorBpms = _timeline.FloorBpms.Select(static value => (float)value).ToArray();
        _floorPositions = AdoGeometryCalculator.BuildFloorPositions(angles, TileSize)
            .Select(static position => new Vector2((float)position.X, (float)position.Y))
            .ToArray();
        _positionedFloorPositions = GameTrackRenderer.BuildPositionedFloorPositions(
            _floorPositions,
            settings,
            actions);
        BuildPlayerFollowTimeline();
    }

    private void BuildPlayerFollowTimeline()
    {
        _followFrom = new Vector2[_floorPositions.Length];
        _followTarget = new Vector2[_floorPositions.Length];
        _followDuration = new float[_floorPositions.Length];
        _followFrom[0] = _positionedFloorPositions[0];
        _followTarget[0] = _positionedFloorPositions[0];
        _followDuration[0] = 0f;
        for (var floor = 1; floor < _floorPositions.Length; floor++)
        {
            _followFrom[floor] = EvaluateFollowSegment(floor - 1, _floorTimes[floor]);
            _followTarget[floor] = _positionedFloorPositions[floor];
            _followDuration[floor] = 120f / Math.Max(0.000001f, _floorBpms[floor] * _pitch);
        }
    }

    private Vector2 PlayerFollowPositionAt(float time)
    {
        if (_floorTimes.Length == 0) return Vector2.zero;
        var floor = Array.BinarySearch(_floorTimes, time);
        if (floor < 0) floor = Math.Max(0, ~floor - 1);
        while (floor + 1 < _floorTimes.Length && _floorTimes[floor + 1] <= time) floor++;
        return EvaluateFollowSegment(floor, time);
    }

    private Vector2 EvaluateFollowSegment(int floor, float time)
    {
        floor = Math.Clamp(floor, 0, _followFrom.Length - 1);
        var duration = _followDuration[floor];
        var amount = duration <= 0f ? 1f : Mathf.Clamp01((time - _floorTimes[floor]) / duration);
        return Vector2.LerpUnclamped(_followFrom[floor], _followTarget[floor], amount);
    }

    private static List<double> ReadAngles(JObject chart)
    {
        if (chart["angleData"] is JArray angleData && angleData.Count > 0)
            return angleData.Select(static token => token.Value<double>()).ToList();

        var map = new Dictionary<char, double>
        {
            ['p'] = 15d, ['J'] = 30d, ['E'] = 45d, ['T'] = 60d, ['o'] = 75d,
            ['U'] = 90d, ['q'] = 105d, ['G'] = 120d, ['Q'] = 135d, ['H'] = 150d,
            ['W'] = 165d, ['L'] = 180d, ['x'] = 195d, ['N'] = 210d, ['Z'] = 225d,
            ['F'] = 240d, ['V'] = 255d, ['D'] = 270d, ['Y'] = 285d, ['B'] = 300d,
            ['C'] = 315d, ['M'] = 330d, ['A'] = 345d, ['R'] = 360d, ['!'] = 999d,
        };
        var pathData = chart.Value<string>("pathData") ?? "";
        var result = new List<double>(pathData.Length);
        foreach (var symbol in pathData)
            if (map.TryGetValue(symbol, out var angle)) result.Add(angle);
        if (result.Count == 0)
            throw new InvalidDataException("Chart has neither angleData nor a supported pathData.");
        return result;
    }

    private void AddDecoration(JObject data, int sourceIndex)
    {
        var depth = data.Value<int?>("depth") ?? 0;
        if ((depth >= 0 && !_job.includeBackgroundDecorations) ||
            (depth < 0 && !_job.includeForegroundDecorations))
        {
            _unbakedDecorationIndices.Add(sourceIndex);
            return;
        }
        var imageName = data.Value<string>("decorationImage");
        Sprite? sprite = null;
        if (!string.IsNullOrWhiteSpace(imageName))
        {
            var imagePath = Path.Combine(_chartDirectory, imageName);
            if (File.Exists(imagePath)) sprite = GetSprite(imagePath);
            if (sprite == null) _unbakedDecorationIndices.Add(sourceIndex);
        }

        var gameObject = new GameObject(imageName);
        var childObject = new GameObject($"{imageName} Sprite");
        childObject.transform.SetParent(gameObject.transform, false);
        childObject.layer = depth >= 0 ? FloorLayer : ForegroundLayer;
        var renderer = childObject.AddComponent<SpriteRenderer>();
        renderer.sortingLayerName = depth >= 0 ? "Bg" : "Default";
        renderer.sprite = sprite;
        renderer.sharedMaterial = GetGameTileMaterial();
        var maskingType = data.Value<string>("maskingType") ?? "None";
        var spriteMask = childObject.AddComponent<SpriteMask>();
        spriteMask.sprite = renderer.sprite;
        spriteMask.alphaCutoff = 0.001f;
        spriteMask.enabled = false;
        renderer.maskInteraction = SpriteMaskInteraction.None;
        if (maskingType is "None" or "")
            ApplyTiling(renderer, ReadVector2(data["tile"], Vector2.one));
        ApplyBlendMode(
            renderer,
            data.Value<string>("blendMode") ?? "None",
            data.Value<bool?>("imageSmoothing") ?? false);
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorPositions.Length - 1);
        var localPosition = ReadVector2(data["position"], Vector2.zero);
        var relativeTo = data.Value<string>("relativeTo") ?? "Tile";
        var startPosition = relativeTo switch
        {
            "Tile" => _positionedFloorPositions[floor] + localPosition * TileSize,
            "RedPlanet" or "BluePlanet" or "GreenPlanet" => _positionedFloorPositions[floor] + localPosition * TileSize,
            "Camera" or "CameraAspect" => localPosition,
            _ => localPosition * TileSize,
        };
        var rotation = data.Value<float?>("rotation") ?? 0f;
        var scale = ReadVector2(data["scale"], new Vector2(100f, 100f)) / 100f;
        var color = ParseColor(data.Value<string>("color") ?? "ffffffff");
        var opacity = (data.Value<float?>("opacity") ?? 100f) / 100f;
        renderer.sortingOrder = Math.Clamp(-depth, short.MinValue + 1, short.MaxValue);
        var visible = data.Value<bool?>("visible") ?? true;

        var tags = SplitTags(data.Value<string>("tag"));
        var parallax = ReadVector2(data["parallax"], Vector2.zero) / 100f;
        var pivotOffset = ReadVector2(data["pivotOffset"], Vector2.zero) *
                          (relativeTo is "Camera" or "CameraAspect" ? 1f : TileSize);
        var parallaxOffset = parallax == Vector2.zero
            ? Vector2.zero
            : ReadVector2(data["parallaxOffset"], Vector2.zero) * TileSize;
        var scaleMultiplier = data.Value<float?>("scaleMultiplier") ?? 1f;
        var originalSize = ReadVector2(data["originalSize"], Vector2.zero);
        var textureScaleMultiplier = sprite != null && originalSize.x != 0f && originalSize.y != 0f
            ? new Vector2(originalSize.x / sprite.texture.width, originalSize.y / sprite.texture.height)
            : Vector2.one;
        var state = new DecorationState(
            sourceIndex,
            gameObject.transform,
            childObject.transform,
            renderer,
            tags,
            relativeTo,
            startPosition,
            pivotOffset,
            parallax,
            parallaxOffset,
            rotation,
            scale,
            color,
            opacity,
            data.Value<bool?>("lockRotation") ?? false,
            data.Value<bool?>("lockScale") ?? false,
            scaleMultiplier,
            textureScaleMultiplier,
            depth,
            spriteMask,
            maskingType,
            data.Value<string>("maskingTarget") ?? "",
            visible,
            data.Value<bool?>("useMaskingDepth") ?? false,
            data.Value<int?>("maskingFrontDepth") ?? depth,
            data.Value<int?>("maskingBackDepth") ?? depth,
            _initialZoomRatio);
        state.ApplyVisual(_camera);
        _decorations.Add(state);
    }

    private void AddParticle(JObject data, int sourceIndex)
    {
        var depth = data.Value<int?>("depth") ?? 0;
        if ((depth >= 0 && !_job.includeBackgroundDecorations) ||
            (depth < 0 && !_job.includeForegroundDecorations))
        {
            _unbakedDecorationIndices.Add(sourceIndex);
            return;
        }

        var imageName = data.Value<string>("decorationImage") ?? "";
        Texture2D? texture = null;
        if (!string.IsNullOrWhiteSpace(imageName))
        {
            var imagePath = Path.Combine(_chartDirectory, imageName);
            if (File.Exists(imagePath)) texture = GetTexture(imagePath);
            if (texture == null) _unbakedDecorationIndices.Add(sourceIndex);
        }

        var prefab = Resources.Load<GameObject>("adofai_particledecoration");
        if (prefab == null)
            throw new InvalidOperationException("Installed game particle decoration prefab is unavailable.");

        var prefabWasActive = prefab.activeSelf;
        prefab.SetActive(false);
        var root = Instantiate(prefab);
        prefab.SetActive(prefabWasActive);
        root.name = string.IsNullOrWhiteSpace(imageName) ? $"Particle {sourceIndex}" : imageName;
        root.SetActive(false);
        foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            if (behaviour != null) DestroyImmediate(behaviour);

        var particleSystem = root.GetComponentInChildren<ParticleSystem>(true)
            ?? throw new InvalidOperationException("Installed game particle decoration prefab has no ParticleSystem.");
        var renderer = particleSystem.GetComponent<ParticleSystemRenderer>()
            ?? throw new InvalidOperationException("Installed game particle decoration prefab has no ParticleSystemRenderer.");
        if (texture != null) renderer.material.mainTexture = texture;
        renderer.sortingLayerName = depth >= 0 ? "Bg" : "Default";
        renderer.sortingOrder = Math.Clamp(-depth, short.MinValue + 1, short.MaxValue);
        particleSystem.gameObject.layer = depth >= 0 ? FloorLayer : ForegroundLayer;

        var main = particleSystem.main;
        particleSystem.randomSeed = (uint)Math.Max(0, data.Value<int?>("randomSeed") ?? 0);
        particleSystem.useAutoRandomSeed = particleSystem.randomSeed == 0;
        main.duration = Math.Max(0.01f, data.Value<float?>("playDuration") ?? 5f);
        main.loop = data.Value<bool?>("loop") ?? false;
        main.maxParticles = Math.Max(0, data.Value<int?>("maxParticles") ?? 1000);
        main.startLifetime = ToRandomCurve(ReadFloatPair(data["particleLifetime"], new Vector2(1f, 1f)));
        main.startSize = ToRandomCurve(ReadFloatPair(data["particleSize"], new Vector2(100f, 100f)), 0.01f);
        main.startRotation = ToRandomCurve(ReadFloatPair(data["startRotation"], Vector2.zero), Mathf.Deg2Rad);
        main.startColor = ParseMinMaxGradient(data["color"]);
        main.simulationSpace = (data.Value<string>("simulationSpace") ?? "Local") == "World"
            ? ParticleSystemSimulationSpace.World
            : ParticleSystemSimulationSpace.Local;
        main.simulationSpeed = Math.Max(0f, (data.Value<float?>("simulationSpeed") ?? 100f) / 100f * _pitch);

        var scale = ReadVector2(data["scale"], new Vector2(100f, 100f)) / 100f;
        var shape = particleSystem.shape;
        shape.shapeType = (data.Value<string>("shapeType") ?? "Circle") == "Rectangle"
            ? ParticleSystemShapeType.Rectangle
            : ParticleSystemShapeType.Circle;
        shape.radius = (data.Value<float?>("shapeRadius") ?? 1f) * TileSize;
        shape.scale = scale * TileSize;
        shape.arc = data.Value<float?>("arc") ?? 360f;
        if (Enum.TryParse(data.Value<string>("arcMode") ?? "Random", out ParticleSystemShapeMultiModeValue arcMode))
            shape.arcMode = arcMode;

        var emission = particleSystem.emission;
        emission.rateOverTime = ToRandomCurve(ReadFloatPair(data["emissionRate"], Vector2.zero));

        var velocity = ReadVelocityPair(data["velocity"]);
        var velocityOverLifetime = particleSystem.velocityOverLifetime;
        velocityOverLifetime.enabled = true;
        velocityOverLifetime.x = new ParticleSystem.MinMaxCurve(velocity.Item1.x * TileSize, velocity.Item2.x * TileSize);
        velocityOverLifetime.y = new ParticleSystem.MinMaxCurve(velocity.Item1.y * TileSize, velocity.Item2.y * TileSize);
        velocityOverLifetime.z = new ParticleSystem.MinMaxCurve(0f, 0f);

        var limitVelocity = particleSystem.limitVelocityOverLifetime;
        limitVelocity.enabled = true;
        limitVelocity.drag = ToLinearCurve(ReadFloatPair(data["velocityLimitOverLifetime"], Vector2.zero), 0.01f);

        var sizeOverLifetime = particleSystem.sizeOverLifetime;
        sizeOverLifetime.enabled = HasValue(data, "sizeOverLifetime");
        if (sizeOverLifetime.enabled)
            sizeOverLifetime.size = ToLinearCurve(ReadFloatPair(data["sizeOverLifetime"], new Vector2(100f, 100f)), 0.01f);

        var colorOverLifetime = particleSystem.colorOverLifetime;
        colorOverLifetime.enabled = HasValue(data, "colorOverLifetime");
        if (colorOverLifetime.enabled) colorOverLifetime.color = ParseMinMaxGradient(data["colorOverLifetime"]);

        var rotationOverLifetime = particleSystem.rotationOverLifetime;
        rotationOverLifetime.enabled = true;
        rotationOverLifetime.x = new ParticleSystem.MinMaxCurve(0f, 0f);
        rotationOverLifetime.y = new ParticleSystem.MinMaxCurve(0f, 0f);
        rotationOverLifetime.z = ToRandomCurve(ReadFloatPair(data["rotationOverTime"], Vector2.zero));

        var textureSheet = particleSystem.textureSheetAnimation;
        textureSheet.enabled = true;
        textureSheet.mode = ParticleSystemAnimationMode.Grid;
        textureSheet.rowMode = ParticleSystemAnimationRowMode.Random;
        textureSheet.cycleCount = 0;
        var tiling = ReadVector2(data["randomTextureTiling"], Vector2.one);
        textureSheet.numTilesX = Math.Max(1, Mathf.RoundToInt(tiling.x));
        textureSheet.numTilesY = Math.Max(1, Mathf.RoundToInt(tiling.y));
        textureSheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 1f);

        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorPositions.Length - 1);
        var localPosition = ReadVector2(data["position"], Vector2.zero);
        var relativeTo = data.Value<string>("relativeTo") ?? "Tile";
        var startPosition = relativeTo switch
        {
            "Tile" or "RedPlanet" or "BluePlanet" or "GreenPlanet" => _positionedFloorPositions[floor] + localPosition * TileSize,
            "Camera" or "CameraAspect" => localPosition,
            _ => localPosition * TileSize,
        };
        var pivotOffset = ReadVector2(data["pivotOffset"], Vector2.zero) *
                          (relativeTo is "Camera" or "CameraAspect" ? 1f : TileSize);
        var parallax = ReadVector2(data["parallax"], Vector2.zero) / 100f;
        var parallaxOffset = parallax == Vector2.zero
            ? Vector2.zero
            : ReadVector2(data["parallaxOffset"], Vector2.zero) * TileSize;

        root.SetActive(true);
        particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        particleSystem.Pause(false);
        var state = new ParticleState(
            sourceIndex,
            root.transform,
            particleSystem,
            renderer,
            SplitTags(data.Value<string>("tag")),
            relativeTo,
            startPosition,
            pivotOffset,
            parallax,
            parallaxOffset,
            data.Value<float?>("rotation") ?? 0f,
            data.Value<bool?>("lockRotation") ?? false,
            data.Value<bool?>("lockScale") ?? false,
            depth,
            _initialZoomRatio);
        state.ApplyVisual(_camera);
        if (data.Value<bool?>("autoPlay") ?? false) state.Start(0f);
        _particles.Add(state);
    }

    private void AddParticleEvent(JObject data, int repeatedFloor, float angleOffset)
    {
        var floor = Math.Clamp(repeatedFloor, 0, _floorTimes.Length - 1);
        var start = (float)_timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        _particleEvents.Add(new ParticleControlEvent(
            start,
            data.Value<string>("eventType") ?? "",
            SplitTags(data.Value<string>("tag")),
            data.Value<string>("targetMode") ?? "",
            Math.Max(0, data.Value<int?>("count") ?? 0),
            _eventSequence++));
    }

    private void ResolveMaskingTargets()
    {
        var masks = _decorations.Where(static decoration => decoration.MaskingType == "Mask").ToArray();
        foreach (var decoration in _decorations)
        {
            var hasMatchingMask = masks.Any(mask =>
                !string.IsNullOrWhiteSpace(mask.MaskingTarget) &&
                !string.Equals(mask.MaskingTarget, "NO TAG", StringComparison.Ordinal) &&
                decoration.Tags.Contains(mask.MaskingTarget));
            decoration.ConfigureMaskRelationship(hasMatchingMask);
        }
    }

    private Texture2D? GetTexture(string imagePath)
    {
        if (_textures.TryGetValue(imagePath, out var existing)) return existing;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        texture.name = Path.GetFileName(imagePath);
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(imagePath), markNonReadable: false))
        {
            Destroy(texture);
            return null;
        }
        _sourceTextureSizes[imagePath] = new Vector2Int(texture.width, texture.height);
        if (_job.textureMaximumSide > 0 &&
            Math.Max(texture.width, texture.height) > _job.textureMaximumSide)
        {
            texture = ResizeTexture(texture, _job.textureMaximumSide);
            texture.name = Path.GetFileName(imagePath);
        }
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Bilinear;
        _textures[imagePath] = texture;
        return texture;
    }

    private static Texture2D ResizeTexture(Texture2D source, int maximumSide)
    {
        var scale = maximumSide / (float)Math.Max(source.width, source.height);
        var width = Math.Max(1, Mathf.RoundToInt(source.width * scale));
        var height = Math.Max(1, Mathf.RoundToInt(source.height * scale));
        var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var resized = new Texture2D(width, height, TextureFormat.RGBA32, false);
            resized.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            resized.Apply(false, false);
            // Texture loading happens synchronously before the first rendered frame.
            // Destroy would defer every full-resolution source until frame end and defeat
            // the low-memory budget on charts with thousands of images.
            DestroyImmediate(source);
            return resized;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private Sprite? GetSprite(string imagePath)
    {
        if (_sprites.TryGetValue(imagePath, out var existing)) return existing;
        var texture = GetTexture(imagePath);
        if (texture == null) return null;
        var sourceSize = _sourceTextureSizes.TryGetValue(imagePath, out var dimensions)
            ? dimensions
            : new Vector2Int(texture.width, texture.height);
        var pixelsPerUnit = 100f * texture.width / Math.Max(1, sourceSize.x);
        var sprite = Sprite.Create(
            texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit);
        sprite.name = Path.GetFileName(imagePath);
        _sprites[imagePath] = sprite;
        return sprite;
    }

    private void ApplyTiling(SpriteRenderer renderer, Vector2 tile)
    {
        var properties = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(properties);
        properties.SetFloat("RepeatX", Math.Max(0.000001f, Math.Abs(tile.x)));
        properties.SetFloat("RepeatY", Math.Max(0.000001f, Math.Abs(tile.y)));
        renderer.SetPropertyBlock(properties);
    }

    private Material GetGameTileMaterial()
    {
        if (_tileMaterial == null)
        {
            var shader = Resources.Load<Shader>("adofai_spritestile");
            if (shader == null) throw new InvalidOperationException("Installed game shader is unavailable: Sprites/Tile");
            _tileMaterial = new Material(shader) { name = "ADOFAI Installed Sprites Tile" };
        }
        return _tileMaterial;
    }

    private void InitializeGameBlendMode()
    {
        var assemblyPath = Path.Combine(_job.gameManagedDirectory, "Assembly-CSharp.dll");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Installed game blend-mode assembly is unavailable.", assemblyPath);
        var assembly = Assembly.LoadFrom(assemblyPath);
        _gameBlendEffectType = assembly.GetType("BlendModes.BlendModeEffect", throwOnError: true)!;
        _gameBlendModeProperty = _gameBlendEffectType.GetProperty("BlendMode", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(_gameBlendEffectType.FullName, "BlendMode");
        _gameBlendPointSamplingProperty = _gameBlendEffectType.GetProperty("UsePointSampling", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(_gameBlendEffectType.FullName, "UsePointSampling");
    }

    private void ApplyBlendMode(SpriteRenderer renderer, string blendMode, bool imageSmoothing)
    {
        if (blendMode is "None" or "Normal" or "") return;
        if (_gameBlendEffectType == null || _gameBlendModeProperty == null || _gameBlendPointSamplingProperty == null)
            throw new InvalidOperationException("Installed game BlendModeEffect is not initialized.");
        try
        {
            var component = renderer.gameObject.AddComponent(_gameBlendEffectType);
            var gameBlendMode = Enum.Parse(_gameBlendModeProperty.PropertyType, blendMode, ignoreCase: false);
            _gameBlendModeProperty.SetValue(component, gameBlendMode);
            _gameBlendPointSamplingProperty.SetValue(component, !imageSmoothing);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Installed game blend mode could not be applied: {blendMode}", exception);
        }
    }

    private static Dictionary<int, Dictionary<string, RepeatRule>> BuildRepeatRules(JArray actions)
    {
        var result = new Dictionary<int, Dictionary<string, RepeatRule>>();
        foreach (var repeat in actions.OfType<JObject>().Where(static action =>
                     action.Value<string>("eventType") == "RepeatEvents"))
        {
            var floor = repeat.Value<int?>("floor") ?? 0;
            if (!result.TryGetValue(floor, out var byTag)) result[floor] = byTag = new(StringComparer.Ordinal);
            var rule = new RepeatRule(
                repeat.Value<string>("repeatType") ?? "Beat",
                Math.Max(0, (repeat.Value<string>("repeatType") ?? "Beat") == "Beat"
                    ? repeat.Value<int?>("repetitions") ?? 0
                    : repeat.Value<int?>("floorCount") ?? 0),
                Math.Max(0f, repeat.Value<float?>("interval") ?? 0f),
                Math.Max(1, repeat.Value<int?>("gapLength") ?? 1),
                repeat.Value<bool?>("executeOnCurrentFloor") ?? false);
            foreach (var tag in SplitTags(repeat.Value<string>("tag"))) byTag[tag] = rule;
        }
        return result;
    }

    private static IEnumerable<RepeatedPlacement> GetRepeatedPlacements(
        JObject action,
        Dictionary<int, Dictionary<string, RepeatRule>> rules,
        int floorCount)
    {
        var baseOffset = action.Value<float?>("angleOffset") ?? 0f;
        var floor = action.Value<int?>("floor") ?? 0;
        if (!rules.TryGetValue(floor, out var byTag) || string.IsNullOrWhiteSpace(action.Value<string>("eventTag")))
        {
            yield return new RepeatedPlacement(floor, baseOffset);
            yield break;
        }

        RepeatRule? matched = null;
        foreach (var tag in SplitTags(action.Value<string>("eventTag")))
        {
            if (byTag.TryGetValue(tag, out matched)) break;
        }
        if (matched == null)
        {
            yield return new RepeatedPlacement(floor, baseOffset);
            yield break;
        }

        for (var repetition = 0; repetition <= matched.Count; repetition++)
        {
            if (matched.Type == "Beat")
            {
                yield return new RepeatedPlacement(floor, baseOffset + matched.IntervalBeats * repetition * 180f);
            }
            else
            {
                var repeatedFloor = floor + repetition * matched.GapLength;
                if (repeatedFloor >= floorCount) yield break;
                yield return new RepeatedPlacement(repeatedFloor, baseOffset);
            }
        }
    }

    private void AddMoveEvent(JObject data, int repeatedFloor, float angleOffset)
    {
        var floor = Math.Clamp(repeatedFloor, 0, _floorTimes.Length - 1);
        var bpm = (float)_timeline.BpmAtFloorAngleOffset(floor, angleOffset);
        var start = (float)_timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f / (bpm * _pitch));
        var replaceSprite = HasValue(data, "decorationImage");
        Sprite? sprite = null;
        var replacementFailed = false;
        if (replaceSprite && data.Value<string>("decorationImage") is { Length: > 0 } imageName)
        {
            var imagePath = Path.Combine(_chartDirectory, imageName);
            if (File.Exists(imagePath)) sprite = GetSprite(imagePath);
            replacementFailed = sprite == null;
        }
        _moves.Add(new MoveEvent(
            start,
            duration,
            SplitTags(data.Value<string>("tag")),
            data,
            replaceSprite,
            sprite,
            replacementFailed,
            index => _unbakedDecorationIndices.Add(index),
            ResolveMaskingTargets,
            _eventSequence++));
    }

    private void AddCameraEvent(JObject data, int repeatedFloor, float angleOffset)
    {
        var floor = Math.Clamp(repeatedFloor, 0, _floorTimes.Length - 1);
        var bpm = (float)_timeline.BpmAtFloorAngleOffset(floor, angleOffset);
        var start = (float)_timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f / (bpm * _pitch));
        _cameraEvents.Add(new CameraEvent(start, duration, data, floor, _positionedFloorPositions[floor], _eventSequence++));
    }

    private void AddBackgroundEvent(JObject data, int repeatedFloor, float angleOffset)
    {
        var floor = Math.Clamp(repeatedFloor, 0, _floorTimes.Length - 1);
        var start = (float)_timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        _backgroundEvents.Add(new BackgroundEvent(start, data, _eventSequence++));
    }

    private IEnumerator RenderFrames()
    {
        RenderTexture? target = null;
        RenderTexture? staticStage = null;
        RenderTexture? backgroundStage = null;
        Texture2D? frameTexture = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_job.outputPath)!);
            Directory.CreateDirectory(_job.previewDirectory);
            var duration = _job.durationLimitSeconds > 0f
                ? _job.durationLimitSeconds
                : Math.Max(0f, (_floorTimes.Length == 0 ? 1f : _floorTimes[^1]) - _job.renderStartSeconds) +
                  Math.Max(0f, _job.tailSeconds);
            var totalFrames = Math.Max(1, Mathf.CeilToInt(duration * _job.framesPerSecond));
            _status.totalFrames = totalFrames;
            _status.state = "rendering";
            _status.message = "独立 Unity Worker 正在逐帧渲染……";
            WriteStatus();

            // CameraFilterPack advances its own TimeX from Unity's Time.deltaTime.
            // captureFramerate makes that delta exactly match the requested video
            // frame interval instead of the worker's wall-clock rendering speed.
            Time.captureFramerate = _job.framesPerSecond;
            StartEncoder();
            target = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32);
            target.Create();
            staticStage = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32);
            staticStage.Create();
            backgroundStage = new RenderTexture(RenderWidth, RenderHeight, 24, RenderTextureFormat.ARGB32);
            backgroundStage.Create();
            var previousTarget = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(true, true, _staticBackgroundCamera.backgroundColor);
            RenderTexture.active = previousTarget;
            frameTexture = new Texture2D(RenderWidth, RenderHeight, TextureFormat.RGBA32, false, false);
            var watch = Stopwatch.StartNew();
            var previewWatch = Stopwatch.StartNew();
            var diagnoseRenderFrame = GetArgumentInt("--diagnose-render-frame", -1);
            for (var frame = 0; frame < totalFrames; frame++)
            {
                var time = _job.renderStartSeconds + frame / (float)_job.framesPerSecond;
                ApplyTimeline(time);
                if (Environment.GetCommandLineArgs().Contains("--diagnose-decorations") && frame % 15 == 0)
                {
                    if (frame == 0) LogDecorationPipeline(time);
                    LogParticlePipeline(time);
                }
                var hallOfMirrors = _effects?.HallOfMirrorsEnabled == true;
                if (!hallOfMirrors) ClearRenderTexture(target, clearDepth: true, _staticBackgroundCamera.backgroundColor);
                if (frame == diagnoseRenderFrame)
                {
                    Debug.Log($"RENDER_PIPELINE frame={frame} time={time:0.000000} hall={_effects?.HallOfMirrorsEnabled == true} staticClear={_staticBackgroundCamera.clearFlags} backgroundClear={_backgroundCamera.clearFlags} mainClear={_camera.clearFlags}");
                    Debug.Log($"FOREGROUND_EFFECTS time={time:0.000000} {_effects?.DescribeActiveEffects()}");
                    Debug.Log($"BACKGROUND_EFFECTS time={time:0.000000} {_backgroundEffects?.DescribeActiveEffects()}");
                    LogDecorationPipeline(time);
                    WriteRenderDiagnostic(target, frame, "00-history-or-clear");
                }

                RenderCameraStackStage(_staticBackgroundCamera, target, staticStage);
                if (frame == diagnoseRenderFrame) WriteRenderDiagnostic(staticStage, frame, "01-after-static");
                RenderCameraStackStage(_backgroundCamera, staticStage, backgroundStage);
                if (frame == diagnoseRenderFrame) WriteRenderDiagnostic(backgroundStage, frame, "02-after-background");
                RenderCameraStackStage(_camera, backgroundStage, target);
                if (frame == diagnoseRenderFrame) WriteRenderDiagnostic(target, frame, "03-after-main");

                var previous = RenderTexture.active;
                RenderTexture.active = target;
                frameTexture.ReadPixels(new Rect(0f, 0f, RenderWidth, RenderHeight), 0, 0, false);
                frameTexture.Apply(false, false);
                RenderTexture.active = previous;
                var raw = frameTexture.GetRawTextureData();
                _ffmpeg!.StandardInput.BaseStream.Write(raw, 0, raw.Length);

                var completed = frame + 1;
                var elapsed = Math.Max(0.001, watch.Elapsed.TotalSeconds);
                var speed = completed / elapsed;
                _status.currentFrame = completed;
                _status.percent = completed * 100f / totalFrames;
                _status.renderedFramesPerSecond = (float)speed;
                _status.secondsPerFrame = (float)(1d / speed);
                if (previewWatch.Elapsed.TotalSeconds >= 0.5d || completed == totalFrames)
                {
                    _status.previewPath = WritePreview(ImageConversion.EncodeToJPG(frameTexture, 85), completed);
                    _status.message = $"已完成 {completed:N0} / {totalFrames:N0} 帧";
                    WriteStatus();
                    previewWatch.Restart();
                }
                yield return null;
            }
            _status.state = "encoding";
            _status.message = "正在完成视频编码……";
            WriteStatus();
            _ffmpeg!.StandardInput.Close();
            _ffmpeg.WaitForExit();
            if (_ffmpeg.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg exited with code {_ffmpeg.ExitCode}.");
            }

            CommitEncodedVideo();

            _status.state = "complete";
            _status.percent = 100f;
            _status.message = "独立 Unity Worker 渲染完成。";
            _status.unbakedDecorationIndices = _unbakedDecorationIndices.OrderBy(static index => index).ToArray();
            WriteStatus();
            yield return null;
            Application.Quit(0);
        }
        finally
        {
            Time.captureFramerate = 0;
            _staticBackgroundCamera.enabled = false;
            _backgroundCamera.enabled = false;
            _camera.enabled = false;
            _staticBackgroundCamera.targetTexture = null;
            _backgroundCamera.targetTexture = null;
            _camera.targetTexture = null;
            if (target != null) target.Release();
            if (staticStage != null) staticStage.Release();
            if (backgroundStage != null) backgroundStage.Release();
            if (frameTexture != null) Destroy(frameTexture);
        }
    }

    private static void RenderCameraStackStage(Camera camera, RenderTexture input, RenderTexture destination)
    {
        var oldTarget = camera.targetTexture;
        var oldClearFlags = camera.clearFlags;
        var oldBackground = camera.backgroundColor;
        var restoreInput = new CommandBuffer { name = "ADOFAI Restore Previous Camera Stage" };
        restoreInput.Blit(input, BuiltinRenderTextureType.CameraTarget);
        try
        {
            camera.targetTexture = destination;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeForwardOpaque, restoreInput);
            camera.Render();
        }
        finally
        {
            camera.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeForwardOpaque, restoreInput);
            restoreInput.Release();
            camera.targetTexture = oldTarget;
            camera.clearFlags = oldClearFlags;
            camera.backgroundColor = oldBackground;
        }
    }

    private static void ClearRenderTexture(RenderTexture target, bool clearDepth, Color color)
    {
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        GL.Clear(clearDepth, true, color);
        RenderTexture.active = previous;
    }


    private IEnumerator RunSafely(IEnumerator routine)
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
                Fail(exception);
                yield break;
            }
            yield return current;
        }
    }

    private void ApplyTimeline(float time)
    {
        while (_nextMove < _moves.Count && _moves[_nextMove].Start <= time)
        {
            AdvanceMoves(_moves[_nextMove].Start);
            var move = _moves[_nextMove++];
            for (var index = _activeMoves.Count - 1; index >= 0; index--)
            {
                _activeMoves[index].CompleteConflicts(move);
                if (_activeMoves[index].IsComplete) _activeMoves.RemoveAt(index);
            }
            move.Capture(_decorations);
            if (!move.Apply(move.Start)) _activeMoves.Add(move);
        }
        AdvanceMoves(time);

        while (_nextCameraEvent < _cameraEvents.Count && _cameraEvents[_nextCameraEvent].Start <= time)
        {
            AdvancePlayerFollow(_cameraEvents[_nextCameraEvent].Start);
            AdvanceCamera(_cameraEvents[_nextCameraEvent].Start);
            var cameraEvent = _cameraEvents[_nextCameraEvent++];
            for (var index = _activeCameraEvents.Count - 1; index >= 0; index--)
            {
                _activeCameraEvents[index].CompleteConflicts(
                    _cameraParent,
                    _camera,
                    _cameraState,
                    cameraEvent);
                if (_activeCameraEvents[index].IsComplete) _activeCameraEvents.RemoveAt(index);
            }
            cameraEvent.Capture(_cameraParent, _camera, _cameraState, PlayerFollowPositionAt(cameraEvent.Start));
            _cameraFollowsPlayer = _cameraState.FollowMode;
            if (!cameraEvent.Apply(_cameraParent, _camera, _cameraState, cameraEvent.Start))
                _activeCameraEvents.Add(cameraEvent);
        }
        AdvancePlayerFollow(time);
        AdvanceCamera(time);

        while (_nextBackgroundEvent < _backgroundEvents.Count && _backgroundEvents[_nextBackgroundEvent].Start <= time)
        {
            ApplyBackground(_backgroundEvents[_nextBackgroundEvent++].Data);
        }

        while (_nextParticleEvent < _particleEvents.Count && _particleEvents[_nextParticleEvent].Start <= time)
        {
            var particleEvent = _particleEvents[_nextParticleEvent++];
            foreach (var particle in _particles)
            {
                particle.AdvanceTo(particleEvent.Start);
                if (particleEvent.Tags.Overlaps(particle.Tags)) particleEvent.Apply(particle);
            }
        }

        _trackRenderer?.Apply(time);
        foreach (var decoration in _decorations) decoration.ApplyVisual(_camera);
        foreach (var particle in _particles)
        {
            particle.AdvanceTo(time);
            particle.ApplyVisual(_camera);
        }
        UpdateBackgroundTransform();
        _effects?.Apply(time);
        _backgroundEffects?.Apply(time);
        _staticBackgroundCamera.clearFlags = _effects?.HallOfMirrorsEnabled == true
            ? CameraClearFlags.Depth
            : CameraClearFlags.SolidColor;
    }

    private void AdvanceMoves(float time)
    {
        for (var index = _activeMoves.Count - 1; index >= 0; index--)
        {
            if (_activeMoves[index].Apply(time)) _activeMoves.RemoveAt(index);
        }
    }

    private void LogDecorationPipeline(float time)
    {
        foreach (var decoration in _decorations.OrderBy(static item => item.SourceIndex))
        {
            var renderer = decoration.Renderer;
            var material = renderer.sharedMaterial;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "DECORATION_PIPELINE time={0:0.000000} index={1} enabled={2} layer={3} sortingLayer={4}/{5} sortingOrder={6} alpha={7:0.000000} shader={8} sprite={9}",
                time,
                decoration.SourceIndex,
                renderer.enabled,
                renderer.gameObject.layer,
                renderer.sortingLayerName,
                renderer.sortingLayerID,
                renderer.sortingOrder,
                renderer.color.a,
                material != null && material.shader != null ? material.shader.name : "<none>",
                renderer.sprite != null ? renderer.sprite.name : "<none>"));
        }
    }

    private void LogParticlePipeline(float time)
    {
        foreach (var particle in _particles.OrderBy(static item => item.SourceIndex))
        {
            var renderer = particle.Renderer;
            var system = particle.System;
            var material = renderer.sharedMaterial;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "PARTICLE_PIPELINE time={0:0.000000} index={1} active={2} enabled={3} count={4} playing={5} paused={6} stopped={7} layer={8} sortingLayer={9}/{10} sortingOrder={11} position={12} scale={13} shader={14} texture={15}",
                time,
                particle.SourceIndex,
                system.gameObject.activeInHierarchy,
                renderer.enabled,
                system.particleCount,
                system.isPlaying,
                system.isPaused,
                system.isStopped,
                system.gameObject.layer,
                renderer.sortingLayerName,
                renderer.sortingLayerID,
                renderer.sortingOrder,
                system.transform.position,
                system.transform.lossyScale,
                material != null && material.shader != null ? material.shader.name : "<none>",
                material != null && material.mainTexture != null ? material.mainTexture.name : "<none>"));
        }
    }

    private void AdvanceCamera(float time)
    {
        for (var index = _activeCameraEvents.Count - 1; index >= 0; index--)
        {
            if (_activeCameraEvents[index].Apply(_cameraParent, _camera, _cameraState, time))
                _activeCameraEvents.RemoveAt(index);
        }
    }

    private void AdvancePlayerFollow(float time)
    {
        var followPosition = PlayerFollowPositionAt(time);
        if (_cameraFollowsPlayer)
        {
            _camera.transform.localPosition = new Vector3(followPosition.x, followPosition.y, -10f);
        }
        _lastPlayerFollowPosition = followPosition;
    }

    private void ApplyBackground(JObject data)
    {
        if (data.Value<string>("color") is { } color)
        {
            _camera.backgroundColor = ParseColor(color);
            _staticBackgroundCamera.backgroundColor = _camera.backgroundColor;
        }

        if (_backgroundRenderer == null) return;
        var imageName = data.Value<string>("bgImage");
        if (string.IsNullOrWhiteSpace(imageName))
        {
            _backgroundRenderer.enabled = false;
            _backgroundRenderer.sprite = null;
            return;
        }

        var path = Path.Combine(_chartDirectory, imageName);
        if (!File.Exists(path))
        {
            _backgroundRenderer.enabled = false;
            return;
        }

        var texture = GetTexture(path);
        if (texture == null) return;
        _backgroundRenderer.sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
        _backgroundRenderer.color = ParseColor(data.Value<string>("imageColor") ?? "ffffffff");
        _backgroundRenderer.enabled = true;
    }

    private void UpdateBackgroundTransform()
    {
        if (_backgroundRenderer == null || !_backgroundRenderer.enabled || _backgroundRenderer.sprite == null) return;
        var transform = _backgroundRenderer.transform;
        transform.position = new Vector3(_camera.transform.position.x, _camera.transform.position.y, 1f);
        transform.rotation = _camera.transform.rotation;
        var spriteSize = _backgroundRenderer.sprite.bounds.size;
        var screenHeight = _camera.orthographicSize * 2f;
        var screenWidth = screenHeight * _camera.aspect;
        var fit = Math.Min(screenWidth / Math.Max(0.001f, spriteSize.x), screenHeight / Math.Max(0.001f, spriteSize.y));
        transform.localScale = new Vector3(fit, fit, 1f);
    }

    private void StartEncoder()
    {
        var encoder = string.IsNullOrWhiteSpace(_job.videoEncoder) ? "libopenh264" : _job.videoEncoder;
        if (encoder != "libopenh264" && encoder != "h264_nvenc" && encoder != "h264_qsv" && encoder != "h264_amf")
            throw new InvalidOperationException("Unsupported video encoder: " + encoder);
        var presetArguments = encoder == "h264_nvenc"
            ? " -preset " + (_job.encoderPreset == "p7" ? "p7" : "p4") + " -tune hq"
            : "";
        var videoFilter = RenderWidth == _job.width && RenderHeight == _job.height
            ? "vflip"
            : string.Format(CultureInfo.InvariantCulture, "vflip,scale={0}:{1}:flags=lanczos", _job.width, _job.height);
        var arguments = string.Format(
            CultureInfo.InvariantCulture,
            "-hide_banner -loglevel warning -y -f rawvideo -pixel_format rgba -video_size {0}x{1} -framerate {2} -i pipe:0 -vf {3} -an -c:v {4}{5} -b:v {6}k -pix_fmt yuv420p \"{7}\"",
            RenderWidth,
            RenderHeight,
            _job.framesPerSecond,
            videoFilter,
            encoder,
            presetArguments,
            Math.Max(1, _job.bitrateKbps),
            GetEncodingOutputPath().Replace("\"", "\\\""));
        _ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _job.ffmpegPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(_job.outputPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            },
        };
        if (!_ffmpeg.Start()) throw new InvalidOperationException("Unable to start FFmpeg.");
    }

    private string GetEncodingOutputPath()
    {
        if (!string.IsNullOrWhiteSpace(_encodingOutputPath)) return _encodingOutputPath;
        var directory = Path.GetDirectoryName(_job.outputPath) ?? ".";
        var fileName = Path.GetFileNameWithoutExtension(_job.outputPath);
        var extension = Path.GetExtension(_job.outputPath);
        foreach (var stalePath in Directory.GetFiles(directory, $".{fileName}.*.rendering{extension}"))
            File.Delete(stalePath);
        _encodingOutputPath = Path.Combine(directory, $".{fileName}.{_job.jobId}.rendering{extension}");
        if (File.Exists(_encodingOutputPath)) File.Delete(_encodingOutputPath);
        return _encodingOutputPath;
    }

    private int RenderWidth => _job.renderWidth > 0 ? _job.renderWidth : _job.width;
    private int RenderHeight => _job.renderHeight > 0 ? _job.renderHeight : _job.height;

    private void CommitEncodedVideo()
    {
        var temporaryPath = GetEncodingOutputPath();
        if (!File.Exists(temporaryPath))
            throw new FileNotFoundException("FFmpeg did not create the temporary video.", temporaryPath);
        if (File.Exists(_job.outputPath))
            File.Replace(temporaryPath, _job.outputPath, null);
        else
            File.Move(temporaryPath, _job.outputPath);
    }

    private string WritePreview(byte[] imageBytes, int frame)
    {
        var path = Path.Combine(_job.previewDirectory, $"preview-{frame % 2}.jpg");
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, imageBytes);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
        return path;
    }

    private void WriteRenderDiagnostic(RenderTexture target, int frame, string label)
    {
        var previous = RenderTexture.active;
        var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, false);
        try
        {
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0f, 0f, target.width, target.height), 0, 0, false);
            image.Apply(false, false);
            File.WriteAllBytes(
                Path.Combine(_job.previewDirectory, $"render-{frame:D5}-{label}.png"),
                ImageConversion.EncodeToPNG(image));
        }
        finally
        {
            RenderTexture.active = previous;
            Destroy(image);
        }
    }

    private void WriteStatus()
    {
        if (string.IsNullOrWhiteSpace(_job.statusPath)) return;
        var temporary = _job.statusPath + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(_status, Formatting.Indented));
        File.Copy(temporary, _job.statusPath, true);
        File.Delete(temporary);
    }

    private void Fail(Exception exception)
    {
        Debug.LogException(exception);
        try
        {
            if (_ffmpeg is { HasExited: false })
            {
                _ffmpeg.Kill();
                _ffmpeg.WaitForExit(2000);
            }
            if (!string.IsNullOrWhiteSpace(_encodingOutputPath) && File.Exists(_encodingOutputPath))
                File.Delete(_encodingOutputPath);
        }
        catch
        {
            // Preserve the original render exception. The next render also removes stale staging files.
        }
        _status ??= new WorkerStatus();
        _status.state = "failed";
        _status.message = exception.Message;
        _status.error = exception.ToString();
        try { WriteStatus(); } catch { /* keep original error */ }
        Application.Quit(1);
    }

    private static string GetJobPath()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] == "--job") return Path.GetFullPath(arguments[index + 1]);
        }
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "render-job.json"));
    }

    private static int GetArgumentInt(string name, int fallback)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index < arguments.Length - 1; index++)
            if (arguments[index] == name && int.TryParse(arguments[index + 1], out var value)) return value;
        return fallback;
    }

    private static Vector2 ReadVector2(JToken? token, Vector2 fallback)
    {
        if (token is not JArray { Count: >= 2 } array) return fallback;
        return new Vector2(ReadFloat(array[0], fallback.x), ReadFloat(array[1], fallback.y));
    }

    private static float ReadFloat(JToken? token, float fallback) =>
        token == null || token.Type is JTokenType.Null or JTokenType.Undefined
            ? fallback
            : token.Value<float>();

    private static Vector2 ReadFloatPair(JToken? token, Vector2 fallback) => ReadVector2(token, fallback);

    private static Tuple<Vector2, Vector2> ReadVelocityPair(JToken? token)
    {
        if (token is not JArray { Count: >= 2 } array)
            return Tuple.Create(Vector2.zero, Vector2.zero);
        return Tuple.Create(ReadVector2(array[0], Vector2.zero), ReadVector2(array[1], Vector2.zero));
    }

    private static ParticleSystem.MinMaxCurve ToRandomCurve(Vector2 pair, float multiplier = 1f) =>
        new(pair.x * multiplier, pair.y * multiplier);

    private static ParticleSystem.MinMaxCurve ToLinearCurve(Vector2 pair, float multiplier = 1f) =>
        new(multiplier, AnimationCurve.Linear(0f, pair.x, 1f, pair.y));

    private static ParticleSystem.MinMaxGradient ParseMinMaxGradient(JToken? token)
    {
        if (token is JValue value && value.Type == JTokenType.String)
            return new ParticleSystem.MinMaxGradient(ParseColor(value.Value<string>() ?? "ffffff"));
        if (token is not JObject data)
            return new ParticleSystem.MinMaxGradient(Color.white);

        var mode = data.Value<string>("mode") ?? "Color";
        return mode switch
        {
            "TwoColors" => new ParticleSystem.MinMaxGradient(
                ParseColor(data.Value<string>("color1") ?? "ffffff"),
                ParseColor(data.Value<string>("color2") ?? "ffffff")),
            "Gradient" => new ParticleSystem.MinMaxGradient(ParseGradient(data["gradient1"])),
            "TwoGradients" => new ParticleSystem.MinMaxGradient(
                ParseGradient(data["gradient1"]), ParseGradient(data["gradient2"])),
            "RandomColor" => RandomColorGradient(ParseGradient(data["gradient1"])),
            _ => new ParticleSystem.MinMaxGradient(ParseColor(data.Value<string>("color1") ?? "ffffff")),
        };
    }

    private static ParticleSystem.MinMaxGradient RandomColorGradient(Gradient gradient)
    {
        var result = new ParticleSystem.MinMaxGradient(gradient) { mode = ParticleSystemGradientMode.RandomColor };
        return result;
    }

    private static Gradient ParseGradient(JToken? token)
    {
        var data = token as JObject;
        var gradient = new Gradient
        {
            mode = (data?.Value<string>("mode") ?? "Blend") == "Fixed" ? GradientMode.Fixed : GradientMode.Blend,
        };
        var colorKeys = data?["colorKeys"] is JArray colors
            ? colors.OfType<JObject>().Select(key => new GradientColorKey(
                ParseColor(key.Value<string>("color") ?? "ffffff"),
                key.Value<float?>("time") ?? 0f)).ToArray()
            : new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) };
        var alphaKeys = data?["alphaKeys"] is JArray alphas
            ? alphas.OfType<JObject>().Select(key => new GradientAlphaKey(
                key.Value<float?>("alpha") ?? 1f,
                key.Value<float?>("time") ?? 0f)).ToArray()
            : new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };
        gradient.SetKeys(colorKeys, alphaKeys);
        return gradient;
    }

    private static HashSet<string> SplitTags(string? tags) =>
        new((string.IsNullOrWhiteSpace(tags) ? "NO TAG" : tags)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static bool HasValue(JObject data, string key) =>
        data.TryGetValue(key, out var token) && token.Type is not (JTokenType.Null or JTokenType.Undefined);

    private static float EaseAmount(float amount, string? ease)
        => GameEaseBridge.Evaluate(amount, ease);

    private static float EaseOutBounce(float amount)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;
        if (amount < 1f / d1) return n1 * amount * amount;
        if (amount < 2f / d1)
        {
            amount -= 1.5f / d1;
            return n1 * amount * amount + 0.75f;
        }
        if (amount < 2.5f / d1)
        {
            amount -= 2.25f / d1;
            return n1 * amount * amount + 0.9375f;
        }
        amount -= 2.625f / d1;
        return n1 * amount * amount + 0.984375f;
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6) hex += "ff";
        return ColorUtility.TryParseHtmlString("#" + hex, out var color) ? color : Color.white;
    }

    private static void HideWindow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var handle = Process.GetCurrentProcess().MainWindowHandle;
        if (handle != IntPtr.Zero) ShowWindow(handle, 0);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [Serializable]
    private sealed class WorkerJob
    {
        public string jobId = "";
        public string chartPath = "";
        public string outputPath = "";
        public string statusPath = "";
        public string previewDirectory = "";
        public string ffmpegPath = "";
        public string gameManagedDirectory = "";
        public int width = 640;
        public int height = 360;
        public int renderWidth;
        public int renderHeight;
        public int framesPerSecond = 30;
        public int bitrateKbps = 2000;
        public float tailSeconds;
        public float durationLimitSeconds;
        public float renderStartSeconds;
        public bool includeTrack;
        public bool includePlanets;
        public bool includeFilters;
        // Backward-compatible with older diagnostic job files.
        public bool includeEffects;
        public string[]? bakedFilterKeys;
        public bool includeBackgroundDecorations = true;
        public bool includeForegroundDecorations = true;
        public int[]? retainedDecorationIndices;
        public string? videoEncoder;
        public string? encoderPreset;
        public int textureMaximumSide;
    }

    [Serializable]
    private sealed class WorkerStatus
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

    private sealed class ParticleControlEvent
    {
        public ParticleControlEvent(
            float start,
            string eventType,
            HashSet<string> tags,
            string targetMode,
            int count,
            int order)
        {
            Start = start;
            EventType = eventType;
            Tags = tags;
            TargetMode = targetMode;
            Count = count;
            Order = order;
        }

        public float Start { get; }
        public string EventType { get; }
        public HashSet<string> Tags { get; }
        public string TargetMode { get; }
        public int Count { get; }
        public int Order { get; }

        public void Apply(ParticleState particle)
        {
            if (EventType == "EmitParticle")
            {
                particle.Emit(Count, Start);
                return;
            }

            switch (TargetMode)
            {
                case "Start":
                    particle.Start(Start);
                    break;
                case "Stop":
                    particle.Stop(clear: false, Start);
                    break;
                case "Clear":
                    particle.Stop(clear: true, Start);
                    break;
            }
        }
    }

    private sealed class ParticleState
    {
        private readonly Transform _transform;
        private readonly ParticleSystem _particleSystem;
        private readonly string _relativeTo;
        private readonly Vector2 _startPosition;
        private readonly Vector2 _pivotOffset;
        private readonly Vector2 _parallax;
        private readonly Vector2 _parallaxOffset;
        private readonly float _startRotation;
        private readonly bool _lockRotation;
        private readonly bool _lockScale;
        private readonly float _initialZoomRatio;
        private bool _hasSimulation;
        private float _lastTime;

        public ParticleState(
            int sourceIndex,
            Transform transform,
            ParticleSystem particleSystem,
            ParticleSystemRenderer renderer,
            HashSet<string> tags,
            string relativeTo,
            Vector2 startPosition,
            Vector2 pivotOffset,
            Vector2 parallax,
            Vector2 parallaxOffset,
            float startRotation,
            bool lockRotation,
            bool lockScale,
            int depth,
            float initialZoomRatio)
        {
            SourceIndex = sourceIndex;
            _transform = transform;
            _particleSystem = particleSystem;
            Renderer = renderer;
            Tags = tags;
            _relativeTo = relativeTo;
            _startPosition = startPosition;
            _pivotOffset = pivotOffset;
            _parallax = parallax;
            _parallaxOffset = parallaxOffset;
            _startRotation = startRotation;
            _lockRotation = lockRotation;
            _lockScale = lockScale;
            _initialZoomRatio = initialZoomRatio;
            Depth = depth;
        }

        public int SourceIndex { get; }
        public int Depth { get; }
        public HashSet<string> Tags { get; }
        public ParticleSystem System => _particleSystem;
        public ParticleSystemRenderer Renderer { get; }

        public void Start(float time)
        {
            _lastTime = time;
            _hasSimulation = true;
            _particleSystem.Play(false);
            _particleSystem.Pause(false);
        }

        public void Stop(bool clear, float time)
        {
            _lastTime = time;
            _particleSystem.Stop(false, clear
                ? ParticleSystemStopBehavior.StopEmittingAndClear
                : ParticleSystemStopBehavior.StopEmitting);
            _particleSystem.Pause(false);
            if (clear) _hasSimulation = false;
        }

        public void Emit(int count, float time)
        {
            _lastTime = time;
            if (count <= 0) return;
            _hasSimulation = true;
            _particleSystem.Emit(count);
            _particleSystem.Pause(false);
        }

        public void AdvanceTo(float time)
        {
            if (time <= _lastTime) return;
            var delta = time - _lastTime;
            _lastTime = time;
            if (!_hasSimulation) return;
            _particleSystem.Simulate(delta, false, false, false);
            _particleSystem.Pause(false);
            if (!_particleSystem.IsAlive(false)) _hasSimulation = false;
        }

        public void ApplyVisual(Camera camera)
        {
            var cameraScaleRatio = _lockScale
                ? camera.orthographicSize / (DefaultCameraOrthoSize * _initialZoomRatio)
                : 1f;
            Vector2 worldPosition;
            if (_relativeTo is "Camera" or "CameraAspect")
            {
                var screenPosition = _startPosition + _pivotOffset;
                if (_relativeTo == "CameraAspect") screenPosition.x /= Math.Max(0.001f, camera.aspect);
                var viewport = screenPosition / 20f + new Vector2(0.5f, 0.5f);
                worldPosition = camera.ViewportToWorldPoint(new Vector3(viewport.x, viewport.y, 10f));
            }
            else
            {
                var cameraPosition = (Vector2)camera.transform.position;
                worldPosition = Vector2.Scale(cameraPosition - _startPosition, _parallax) +
                                _startPosition + _parallaxOffset + _pivotOffset;
            }

            _transform.position = new Vector3(worldPosition.x, worldPosition.y, 0f);
            _transform.rotation = Quaternion.Euler(0f, 0f,
                _startRotation + (_lockRotation ? camera.transform.eulerAngles.z : 0f));
            _particleSystem.transform.localScale = Vector3.one * cameraScaleRatio;
            Renderer.sortingLayerName = Depth >= 0 ? "Bg" : "Default";
            Renderer.sortingOrder = Math.Clamp(-Depth, short.MinValue + 1, short.MaxValue);
            _particleSystem.gameObject.layer = Depth >= 0 ? FloorLayer : ForegroundLayer;
        }
    }

    private sealed class DecorationState
    {
        public DecorationState(
            int sourceIndex,
            Transform transform,
            Transform childTransform,
            SpriteRenderer renderer,
            HashSet<string> tags,
            string relativeTo,
            Vector2 startPosition,
            Vector2 pivotOffset,
            Vector2 parallax,
            Vector2 parallaxOffset,
            float startRotation,
            Vector2 scale,
            Color color,
            float opacity,
            bool lockRotation,
            bool lockScale,
            float scaleMultiplier,
            Vector2 textureScaleMultiplier,
            int depth,
            SpriteMask? spriteMask,
            string maskingType,
            string maskingTarget,
            bool visible,
            bool useMaskingDepth,
            int maskingFrontDepth,
            int maskingBackDepth,
            float initialZoomRatio)
        {
            SourceIndex = sourceIndex;
            Transform = transform;
            ChildTransform = childTransform;
            Renderer = renderer;
            Tags = tags;
            RelativeTo = relativeTo;
            StartPosition = startPosition;
            PivotPosition = startPosition;
            PivotOffset = pivotOffset;
            Parallax = parallax;
            ParallaxOffset = parallaxOffset;
            StartRotation = startRotation;
            Scale = scale;
            Color = color;
            Opacity = opacity;
            LockRotation = lockRotation;
            LockScale = lockScale;
            ScaleMultiplier = scaleMultiplier;
            TextureScaleMultiplier = textureScaleMultiplier;
            Depth = depth;
            SpriteMask = spriteMask;
            MaskingType = maskingType;
            MaskingTarget = maskingTarget;
            Visible = visible;
            UseMaskingDepth = useMaskingDepth;
            MaskingFrontDepth = maskingFrontDepth;
            MaskingBackDepth = maskingBackDepth;
            InitialZoomRatio = initialZoomRatio;
            RefreshMaskRange();
        }
        public int SourceIndex { get; }
        public Transform Transform { get; }
        public Transform ChildTransform { get; }
        public SpriteRenderer Renderer { get; }
        public HashSet<string> Tags { get; }
        public string RelativeTo { get; set; }
        public Vector2 StartPosition { get; set; }
        public Vector2 PivotPosition { get; set; }
        public Vector2 PivotOffset { get; set; }
        public Vector2 Parallax { get; set; }
        public Vector2 ParallaxOffset { get; set; }
        public float StartRotation { get; }
        public float RotationOffset { get; set; }
        public Vector2 Scale { get; set; }
        public Color Color { get; set; }
        public float Opacity { get; set; }
        public bool LockRotation { get; }
        public bool LockScale { get; }
        public float ScaleMultiplier { get; }
        public Vector2 TextureScaleMultiplier { get; private set; }
        public int Depth { get; set; }
        public SpriteMask? SpriteMask { get; }
        public string MaskingType { get; private set; }
        public string MaskingTarget { get; private set; }
        private bool HasMatchingMask { get; set; }
        public bool Visible { get; private set; }
        public bool UseMaskingDepth { get; private set; }
        public int MaskingFrontDepth { get; private set; }
        public int MaskingBackDepth { get; private set; }
        public float InitialZoomRatio { get; }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            Renderer.enabled = visible && MaskingType != "Mask";
            if (SpriteMask != null)
                SpriteMask.enabled = visible && MaskingType == "Mask";
        }

        public void ConfigureMaskRelationship(bool hasMatchingMask)
        {
            HasMatchingMask = hasMatchingMask;
            Renderer.maskInteraction = MaskingType switch
            {
                "VisibleInsideMask" => SpriteMaskInteraction.VisibleInsideMask,
                "VisibleOutsideMask" => SpriteMaskInteraction.VisibleOutsideMask,
                _ => SpriteMaskInteraction.None,
            };
            SetVisible(Visible);
        }

        public void SetDepth(int depth)
        {
            Depth = depth;
            Renderer.gameObject.layer = depth >= 0 ? FloorLayer : ForegroundLayer;
            Renderer.sortingLayerName = depth >= 0 ? "Bg" : "Default";
            Renderer.sortingOrder = Math.Clamp(-depth, short.MinValue + 1, short.MaxValue);
            RefreshMaskRange();
        }

        public void SetMasking(string? maskingType = null, bool? useDepth = null, int? frontDepth = null, int? backDepth = null)
        {
            if (maskingType != null) MaskingType = maskingType;
            if (useDepth.HasValue) UseMaskingDepth = useDepth.Value;
            if (frontDepth.HasValue) MaskingFrontDepth = frontDepth.Value;
            if (backDepth.HasValue) MaskingBackDepth = backDepth.Value;
            ConfigureMaskRelationship(HasMatchingMask);
            SetVisible(Visible);
            RefreshMaskRange();
        }

        public void SetMaskingTarget(string target)
        {
            MaskingTarget = string.IsNullOrWhiteSpace(target) ? "NO TAG" : target;
        }

        public void SetSprite(Sprite? sprite)
        {
            Renderer.sprite = sprite;
            if (SpriteMask != null) SpriteMask.sprite = sprite;
        }

        public void SetOriginalSize(Vector2 originalSize)
        {
            var sprite = Renderer.sprite;
            if (sprite == null || originalSize.x == 0f || originalSize.y == 0f) return;
            TextureScaleMultiplier = new Vector2(
                originalSize.x / sprite.texture.width,
                originalSize.y / sprite.texture.height);
        }

        private void RefreshMaskRange()
        {
            if (SpriteMask == null) return;
            SpriteMask.isCustomRangeActive = UseMaskingDepth;
            var front = Math.Min(MaskingFrontDepth, MaskingBackDepth);
            var back = Math.Max(MaskingFrontDepth, MaskingBackDepth);
            SpriteMask.frontSortingLayerID = SortingLayer.NameToID(front >= 0 ? "Bg" : "Default");
            SpriteMask.frontSortingOrder = Math.Clamp(-front + 1, short.MinValue + 1, short.MaxValue);
            SpriteMask.backSortingLayerID = SortingLayer.NameToID(back >= 0 ? "Bg" : "Default");
            SpriteMask.backSortingOrder = Math.Clamp(-back, short.MinValue + 1, short.MaxValue);
        }

        public void ApplyVisual(Camera camera)
        {
            var cameraScaleRatio = LockScale
                ? camera.orthographicSize / (DefaultCameraOrthoSize * InitialZoomRatio)
                : 1f;
            var trueScaleMultiplier = ScaleMultiplier * TextureScaleMultiplier;
            var cameraScaleMultiplier = cameraScaleRatio * trueScaleMultiplier;
            Vector2 worldPosition;
            if (RelativeTo is "Camera" or "CameraAspect")
            {
                var screenPosition = PivotPosition + PivotOffset;
                if (RelativeTo == "CameraAspect") screenPosition.x /= Math.Max(0.001f, camera.aspect);
                var viewport = screenPosition / 20f + new Vector2(0.5f, 0.5f);
                worldPosition = camera.ViewportToWorldPoint(new Vector3(viewport.x, viewport.y, 10f));
            }
            else
            {
                var cameraPosition = (Vector2)camera.transform.position;
                var safeTextureScale = new Vector2(
                    Math.Abs(TextureScaleMultiplier.x) < 0.000001f ? 1f : TextureScaleMultiplier.x,
                    Math.Abs(TextureScaleMultiplier.y) < 0.000001f ? 1f : TextureScaleMultiplier.y);
                var scaledParallaxOffset = Vector2.Scale(
                    ParallaxOffset,
                    new Vector2(
                        cameraScaleMultiplier.x / safeTextureScale.x,
                        cameraScaleMultiplier.y / safeTextureScale.y));
                worldPosition = Vector2.Scale(cameraPosition - PivotPosition, Parallax) +
                                PivotPosition + scaledParallaxOffset;
            }

            Transform.position = new Vector3(worldPosition.x, worldPosition.y, 0f);
            Transform.rotation = Quaternion.Euler(0f, 0f,
                StartRotation + RotationOffset + (LockRotation ? camera.transform.eulerAngles.z : 0f));
            Transform.localScale = new Vector3(
                Scale.x * cameraScaleMultiplier.x,
                Scale.y * cameraScaleMultiplier.y,
                1f);
            ChildTransform.localPosition = new Vector3(
                PivotOffset.x / (Math.Abs(trueScaleMultiplier.x) < 0.000001f ? 1f : trueScaleMultiplier.x),
                PivotOffset.y / (Math.Abs(trueScaleMultiplier.y) < 0.000001f ? 1f : trueScaleMultiplier.y),
                0f);
            var renderColor = Color;
            renderColor.a *= Opacity;
            Renderer.color = renderColor;
            SetDepth(Depth);
            SetVisible(Visible);
        }
    }

    private sealed class MoveEvent
    {
        [Flags]
        private enum Property
        {
            None = 0,
            PositionX = 1 << 0,
            PositionY = 1 << 1,
            PivotX = 1 << 2,
            PivotY = 1 << 3,
            ScaleX = 1 << 4,
            ScaleY = 1 << 5,
            Rotation = 1 << 6,
            Color = 1 << 7,
            Opacity = 1 << 8,
            Parallax = 1 << 9,
            ParallaxOffsetX = 1 << 10,
            ParallaxOffsetY = 1 << 11,
        }

        private readonly HashSet<string> _tags;
        private readonly JObject _data;
        private readonly List<Snapshot> _targets = new();
        private readonly Property _properties;
        private readonly bool _replaceSprite;
        private readonly Sprite? _replacementSprite;
        private readonly bool _replacementFailed;
        private readonly Action<int> _markUnbaked;
        private readonly Action _refreshMasks;
        public MoveEvent(
            float start,
            float duration,
            HashSet<string> tags,
            JObject data,
            bool replaceSprite,
            Sprite? replacementSprite,
            bool replacementFailed,
            Action<int> markUnbaked,
            Action refreshMasks,
            int order)
        {
            Start = start;
            Duration = duration;
            _tags = tags;
            _data = data;
            _properties = GetProperties(data);
            _replaceSprite = replaceSprite;
            _replacementSprite = replacementSprite;
            _replacementFailed = replacementFailed;
            _markUnbaked = markUnbaked;
            _refreshMasks = refreshMasks;
            Order = order;
        }
        public float Start { get; }
        public int Order { get; }
        private float Duration { get; }
        public bool IsComplete => _properties == Property.None || _targets.All(static target => target.Active == Property.None);

        public void CompleteConflicts(MoveEvent incoming)
        {
            var overlap = _properties & incoming._properties;
            if (overlap == Property.None) return;
            foreach (var target in _targets)
            {
                if (!incoming._tags.Overlaps(target.State.Tags)) continue;
                var targetOverlap = target.Active & overlap;
                if (targetOverlap == Property.None) continue;
                ApplyProperties(target, targetOverlap, 1f);
                target.Active &= ~targetOverlap;
            }
        }

        public void Capture(IEnumerable<DecorationState> decorations)
        {
            var maskingChanged = false;
            foreach (var decoration in decorations)
            {
                if (!_tags.Overlaps(decoration.Tags)) continue;
                if (HasValue(_data, "relativeTo") && _data.Value<string>("relativeTo") is { } relativeTo && relativeTo != "LastPosition")
                    decoration.RelativeTo = relativeTo;
                if (HasValue(_data, "visible")) decoration.SetVisible(_data.Value<bool>("visible"));
                if (HasValue(_data, "depth")) decoration.SetDepth(_data.Value<int>("depth"));
                if (_replaceSprite)
                {
                    decoration.SetSprite(_replacementSprite);
                    if (_replacementFailed) _markUnbaked(decoration.SourceIndex);
                }
                if (HasValue(_data, "originalSize"))
                    decoration.SetOriginalSize(ReadVector2(_data["originalSize"], Vector2.zero));
                if (HasValue(_data, "maskingType") || HasValue(_data, "useMaskingDepth") ||
                    HasValue(_data, "maskingFrontDepth") || HasValue(_data, "maskingBackDepth"))
                {
                    decoration.SetMasking(
                        HasValue(_data, "maskingType") ? _data.Value<string>("maskingType") : null,
                        HasValue(_data, "useMaskingDepth") ? _data.Value<bool?>("useMaskingDepth") : null,
                        HasValue(_data, "maskingFrontDepth") ? _data.Value<int?>("maskingFrontDepth") : null,
                        HasValue(_data, "maskingBackDepth") ? _data.Value<int?>("maskingBackDepth") : null);
                    maskingChanged = HasValue(_data, "maskingType");
                }
                if (HasValue(_data, "maskingTarget"))
                {
                    decoration.SetMaskingTarget(_data.Value<string>("maskingTarget") ?? "NO TAG");
                    maskingChanged = true;
                }
                _targets.Add(new Snapshot(decoration, _data, _properties));
            }
            if (maskingChanged) _refreshMasks();
        }
        public bool Apply(float time)
        {
            var rawAmount = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            var amount = EaseAmount(rawAmount, _data.Value<string>("ease"));
            foreach (var target in _targets)
            {
                ApplyProperties(target, target.Active, amount);
                if (rawAmount >= 1f) target.Active = Property.None;
            }
            return rawAmount >= 1f || IsComplete;
        }

        private static void ApplyProperties(Snapshot target, Property properties, float amount)
        {
            var state = target.State;
            if ((properties & Property.PositionX) != 0) state.PivotPosition = new Vector2(Mathf.LerpUnclamped(target.PivotPosition.x, target.DestinationPosition.x, amount), state.PivotPosition.y);
            if ((properties & Property.PositionY) != 0) state.PivotPosition = new Vector2(state.PivotPosition.x, Mathf.LerpUnclamped(target.PivotPosition.y, target.DestinationPosition.y, amount));
            if ((properties & Property.PivotX) != 0) state.PivotOffset = new Vector2(Mathf.LerpUnclamped(target.PivotOffset.x, target.DestinationPivot.x, amount), state.PivotOffset.y);
            if ((properties & Property.PivotY) != 0) state.PivotOffset = new Vector2(state.PivotOffset.x, Mathf.LerpUnclamped(target.PivotOffset.y, target.DestinationPivot.y, amount));
            if ((properties & Property.ScaleX) != 0) state.Scale = new Vector2(Mathf.LerpUnclamped(target.Scale.x, target.DestinationScale.x, amount), state.Scale.y);
            if ((properties & Property.ScaleY) != 0) state.Scale = new Vector2(state.Scale.x, Mathf.LerpUnclamped(target.Scale.y, target.DestinationScale.y, amount));
            if ((properties & Property.Rotation) != 0) state.RotationOffset = Mathf.LerpUnclamped(target.RotationOffset, target.DestinationRotation, amount);
            if ((properties & Property.Color) != 0) state.Color = Color.LerpUnclamped(target.Color, target.DestinationColor, amount);
            if ((properties & Property.Opacity) != 0) state.Opacity = Mathf.LerpUnclamped(target.Opacity, target.DestinationOpacity, amount);
            if ((properties & Property.Parallax) != 0) state.Parallax = Vector2.LerpUnclamped(target.Parallax, target.DestinationParallax, amount);
            if ((properties & Property.ParallaxOffsetX) != 0) state.ParallaxOffset = new Vector2(Mathf.LerpUnclamped(target.ParallaxOffset.x, target.DestinationParallaxOffset.x, amount), state.ParallaxOffset.y);
            if ((properties & Property.ParallaxOffsetY) != 0) state.ParallaxOffset = new Vector2(state.ParallaxOffset.x, Mathf.LerpUnclamped(target.ParallaxOffset.y, target.DestinationParallaxOffset.y, amount));
        }

        private static Property GetProperties(JObject data)
        {
            var properties = Property.None;
            var position = ReadVector2(data["positionOffset"], new Vector2(float.NaN, float.NaN));
            if (!float.IsNaN(position.x)) properties |= Property.PositionX;
            if (!float.IsNaN(position.y)) properties |= Property.PositionY;
            var pivot = ReadVector2(data["pivotOffset"], new Vector2(float.NaN, float.NaN));
            if (!float.IsNaN(pivot.x)) properties |= Property.PivotX;
            if (!float.IsNaN(pivot.y)) properties |= Property.PivotY;
            var scale = ReadVector2(data["scale"], new Vector2(float.NaN, float.NaN));
            if (!float.IsNaN(scale.x)) properties |= Property.ScaleX;
            if (!float.IsNaN(scale.y)) properties |= Property.ScaleY;
            if (HasValue(data, "rotationOffset")) properties |= Property.Rotation;
            if (HasValue(data, "color")) properties |= Property.Color;
            if (HasValue(data, "opacity")) properties |= Property.Opacity;
            if (HasValue(data, "parallax")) properties |= Property.Parallax;
            var parallaxOffset = ReadVector2(data["parallaxOffset"], new Vector2(float.NaN, float.NaN));
            if (!float.IsNaN(parallaxOffset.x)) properties |= Property.ParallaxOffsetX;
            if (!float.IsNaN(parallaxOffset.y)) properties |= Property.ParallaxOffsetY;
            return properties;
        }

        private sealed class Snapshot
        {
            public Snapshot(DecorationState state, JObject data, Property active)
            {
                State = state;
                Active = active;
                PivotPosition = state.PivotPosition;
                PivotOffset = state.PivotOffset;
                Scale = state.Scale;
                RotationOffset = state.RotationOffset;
                Color = state.Color;
                Opacity = state.Opacity;
                Parallax = state.Parallax;
                ParallaxOffset = state.ParallaxOffset;

                var position = ReadVector2(data["positionOffset"], new Vector2(float.NaN, float.NaN)) * TileSize;
                var basePosition = (data.Value<string>("relativeTo") ?? "LastPosition") == "LastPosition"
                    ? PivotPosition
                    : state.StartPosition;
                DestinationPosition = PivotPosition;
                if (!float.IsNaN(position.x)) DestinationPosition.x = basePosition.x + position.x;
                if (!float.IsNaN(position.y)) DestinationPosition.y = basePosition.y + position.y;
                var pivot = ReadVector2(data["pivotOffset"], new Vector2(float.NaN, float.NaN)) * TileSize;
                DestinationPivot = PivotOffset;
                if (!float.IsNaN(pivot.x)) DestinationPivot.x = pivot.x;
                if (!float.IsNaN(pivot.y)) DestinationPivot.y = pivot.y;
                var scale = ReadVector2(data["scale"], new Vector2(float.NaN, float.NaN)) / 100f;
                DestinationScale = Scale;
                if (!float.IsNaN(scale.x)) DestinationScale.x = scale.x;
                if (!float.IsNaN(scale.y)) DestinationScale.y = scale.y;
                DestinationRotation = data.Value<float?>("rotationOffset") ?? RotationOffset;
                DestinationColor = data.Value<string>("color") is { } color ? ParseColor(color) : Color;
                DestinationOpacity = (data.Value<float?>("opacity") ?? (Opacity * 100f)) / 100f;
                DestinationParallax = ReadVector2(data["parallax"], Parallax * 100f) / 100f;
                DestinationParallaxOffset = ReadVector2(data["parallaxOffset"], ParallaxOffset / TileSize) * TileSize;
            }
            public DecorationState State { get; }
            public Property Active { get; set; }
            public Vector2 PivotPosition { get; }
            public Vector2 DestinationPosition;
            public Vector2 PivotOffset { get; }
            public Vector2 DestinationPivot;
            public Vector2 Scale { get; }
            public Vector2 DestinationScale;
            public float RotationOffset { get; }
            public float DestinationRotation { get; }
            public Color Color { get; }
            public Color DestinationColor { get; }
            public float Opacity { get; }
            public float DestinationOpacity { get; }
            public Vector2 Parallax { get; }
            public Vector2 DestinationParallax { get; }
            public Vector2 ParallaxOffset { get; }
            public Vector2 DestinationParallaxOffset { get; }
        }
    }

    private sealed class CameraEvent
    {
        [Flags]
        private enum Property
        {
            None = 0,
            PositionX = 1,
            PositionY = 2,
            Rotation = 4,
            Zoom = 8,
        }

        private readonly JObject _data;
        private Vector3 _position;
        private Vector3 _targetPosition;
        private float _rotation;
        private float _targetRotation;
        private float _size;
        private float _targetSize;
        private readonly int _floorIndex;
        private readonly Vector2 _floorPosition;
        private Property _active;
        private readonly Property _properties;
        private readonly bool _positionUsed;
        private readonly bool _movementTypeUsed;
        private bool _effectiveMovementTypeUsed;
        private string _effectiveMode = "Player";
        public CameraEvent(float start, float duration, JObject data, int floorIndex, Vector2 floorPosition, int order)
        {
            Start = start;
            Duration = duration;
            _data = data;
            _floorIndex = floorIndex;
            _floorPosition = floorPosition;
            Order = order;
            var position = ReadVector2(data["position"], new Vector2(float.NaN, float.NaN));
            _positionUsed = !float.IsNaN(position.x) || !float.IsNaN(position.y);
            _movementTypeUsed = HasValue(data, "relativeTo");
            if (!float.IsNaN(position.x) || _movementTypeUsed) _properties |= Property.PositionX;
            if (!float.IsNaN(position.y) || _movementTypeUsed) _properties |= Property.PositionY;
            var requestedMode = data.Value<string>("relativeTo") ?? "";
            if (HasValue(data, "rotation") || (_movementTypeUsed && requestedMode is "LastPosition" or "LastPositionNoRotation"))
                _properties |= Property.Rotation;
            if (HasValue(data, "zoom")) _properties |= Property.Zoom;
        }
        public float Start { get; }
        public int Order { get; }
        private float Duration { get; }
        public bool IsComplete => _active == Property.None;

        public void CompleteConflicts(
            Transform cameraParent,
            Camera camera,
            CameraLogicalState cameraState,
            CameraEvent incoming)
        {
            var overlap = _active & incoming._properties;
            var parentPosition = cameraParent.position;
            if ((overlap & Property.PositionX) != 0) parentPosition.x = _targetPosition.x;
            if ((overlap & Property.PositionY) != 0) parentPosition.y = _targetPosition.y;
            cameraParent.position = new Vector3(parentPosition.x, parentPosition.y, 0f);
            if ((overlap & Property.Rotation) != 0)
            {
                cameraState.Rotation = _targetRotation;
                camera.transform.localRotation = Quaternion.Euler(0f, 0f, cameraState.Rotation);
            }
            if ((overlap & Property.Zoom) != 0) camera.orthographicSize = _targetSize;
            _active &= ~overlap;
        }

        public void Capture(
            Transform cameraParent,
            Camera camera,
            CameraLogicalState cameraState,
            Vector2 followPosition)
        {
            _active = _properties;
            var requested = ReadVector2(_data["position"], new Vector2(float.NaN, float.NaN)) * TileSize;
            _effectiveMovementTypeUsed = _movementTypeUsed;
            var requestedMode = _data.Value<string>("relativeTo") ?? cameraState.LastUsedMovementType;
            if (_effectiveMovementTypeUsed && (requestedMode != "Tile" || cameraState.LastTileCamFloor == _floorIndex) &&
                _positionUsed && (float.IsNaN(requested.x) || float.IsNaN(requested.y)) &&
                requestedMode == cameraState.LastUsedMovementType && requestedMode is not ("Global" or "LastPosition" or "LastPositionNoRotation"))
                _effectiveMovementTypeUsed = false;
            if (_effectiveMovementTypeUsed)
            {
                if (float.IsNaN(requested.x)) requested.x = 0f;
                if (float.IsNaN(requested.y)) requested.y = 0f;
            }
            _effectiveMode = _effectiveMovementTypeUsed ? requestedMode : cameraState.LastUsedMovementType;

            if (_effectiveMode == "Player" && !cameraState.FollowMode)
            {
                var oldParent = cameraParent.position;
                cameraParent.position = new Vector3(oldParent.x - followPosition.x, oldParent.y - followPosition.y, 0f);
                camera.transform.position = new Vector3(oldParent.x, oldParent.y, -10f);
                cameraState.FollowMode = true;
            }
            else if (_effectiveMode is "Tile" or "Global" && cameraState.FollowMode)
            {
                var world = camera.transform.position;
                cameraParent.position = new Vector3(world.x, world.y, 0f);
                camera.transform.localPosition = new Vector3(0f, 0f, -10f);
                cameraState.FollowMode = false;
            }

            _position = cameraParent.position;
            _rotation = cameraState.Rotation;
            _size = camera.orthographicSize;
            var relative = _positionUsed
                ? requested
                : cameraState.LastEventRelativePosition - (Vector2)cameraParent.position;
            var target = _effectiveMode switch
            {
                "Player" => relative,
                "Tile" => _floorPosition + relative,
                "Global" => relative,
                _ => (Vector2)cameraParent.position + (_positionUsed ? requested : Vector2.zero),
            };
            if (float.IsNaN(target.x)) target.x = _position.x;
            if (float.IsNaN(target.y)) target.y = _position.y;
            _targetPosition = new Vector3(target.x, target.y, 0f);
            if (_effectiveMode == "Tile")
            {
                cameraState.LastEventRelativePosition = _floorPosition;
                cameraState.LastTileCamFloor = _floorIndex;
            }
            else if (_effectiveMode == "Global")
                cameraState.LastEventRelativePosition = Vector2.zero;
            if (_effectiveMovementTypeUsed) cameraState.LastUsedMovementType = requestedMode;

            var rotationBase = _effectiveMode == "LastPosition" ? cameraState.Rotation : 0f;
            _targetRotation = _data.Value<float?>("rotation") is { } requestedRotation
                ? rotationBase + requestedRotation
                : (_effectiveMovementTypeUsed && _effectiveMode is "LastPosition" or "LastPositionNoRotation"
                    ? rotationBase
                    : _rotation);
            _targetSize = _data.Value<float?>("zoom") is { } zoom
                ? DefaultCameraOrthoSize * Math.Max(0.01f, zoom / 100f)
                : _size;
        }
        public bool Apply(Transform cameraParent, Camera camera, CameraLogicalState cameraState, float time)
        {
            var rawAmount = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            var amount = EaseAmount(rawAmount, _data.Value<string>("ease"));
            var current = cameraParent.position;
            if ((_active & Property.PositionX) != 0) current.x = Mathf.LerpUnclamped(_position.x, _targetPosition.x, amount);
            if ((_active & Property.PositionY) != 0) current.y = Mathf.LerpUnclamped(_position.y, _targetPosition.y, amount);
            cameraParent.position = new Vector3(current.x, current.y, 0f);
            if ((_active & Property.Rotation) != 0)
            {
                cameraState.Rotation = Mathf.LerpUnclamped(_rotation, _targetRotation, amount);
                camera.transform.localRotation = Quaternion.Euler(0f, 0f, cameraState.Rotation);
            }
            if ((_active & Property.Zoom) != 0)
                camera.orthographicSize = Mathf.LerpUnclamped(_size, _targetSize, amount);
            if (rawAmount >= 1f) _active = Property.None;
            return rawAmount >= 1f || IsComplete;
        }
    }

    private sealed class CameraLogicalState
    {
        public float Rotation { get; set; }
        public bool FollowMode { get; set; }
        public string LastUsedMovementType { get; set; } = "Player";
        public Vector2 LastEventRelativePosition { get; set; }
        public int LastTileCamFloor { get; set; } = -1;

        public void Initialize(string initialMode, float rotation)
        {
            Rotation = rotation;
            FollowMode = initialMode == "Player";
            LastUsedMovementType = initialMode is "Player" or "Tile" or "Global" or "LastPosition" or "LastPositionNoRotation"
                ? initialMode
                : "Player";
        }
    }

    private sealed class BackgroundEvent
    {
        public BackgroundEvent(float start, JObject data, int order)
        {
            Start = start;
            Data = data;
            Order = order;
        }
        public float Start { get; }
        public JObject Data { get; }
        public int Order { get; }
    }

    private sealed class RepeatRule
    {
        public RepeatRule(string type, int count, float intervalBeats, int gapLength, bool executeOnCurrentFloor)
        {
            Type = type;
            Count = count;
            IntervalBeats = intervalBeats;
            GapLength = gapLength;
            ExecuteOnCurrentFloor = executeOnCurrentFloor;
        }
        public string Type { get; }
        public int Count { get; }
        public float IntervalBeats { get; }
        public int GapLength { get; }
        public bool ExecuteOnCurrentFloor { get; }
    }

    private readonly struct RepeatedPlacement
    {
        public RepeatedPlacement(int floor, float angleOffset)
        {
            Floor = floor;
            AngleOffset = angleOffset;
        }
        public int Floor { get; }
        public float AngleOffset { get; }
    }
}
}
