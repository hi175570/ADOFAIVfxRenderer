using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ADOFAIVfxTimeline;
using Debug = UnityEngine.Debug;

namespace ADOFAIVfxWorker
{
public sealed class WorkerRuntime : MonoBehaviour
{
    private const float TileSize = 1.5f;
    private const float DefaultCameraOrthoSize = 5f;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Material> _blendMaterials = new(StringComparer.Ordinal);
    private Material? _spriteMaterial;
    private readonly List<DecorationState> _decorations = new();
    private readonly List<MoveEvent> _moves = new();
    private readonly List<MoveEvent> _activeMoves = new();
    private readonly List<CameraEvent> _cameraEvents = new();
    private readonly List<CameraEvent> _activeCameraEvents = new();
    private readonly List<BackgroundEvent> _backgroundEvents = new();
    private WorkerJob _job = null!;
    private WorkerStatus _status = null!;
    private Camera _camera = null!;
    private Process? _ffmpeg;
    private int _nextMove;
    private int _nextCameraEvent;
    private int _nextBackgroundEvent;
    private int _eventSequence;
    private float[] _floorTimes = Array.Empty<float>();
    private float[] _floorBpms = Array.Empty<float>();
    private Vector2[] _floorPositions = Array.Empty<Vector2>();
    private AdoTimelineResult _timeline = null!;
    private string _chartDirectory = "";
    private float _pitch = 1f;
    private float _initialZoomRatio = 1f;
    private SpriteRenderer? _backgroundRenderer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
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
        var chart = JObject.Parse(File.ReadAllText(_job.chartPath));
        _chartDirectory = Path.GetDirectoryName(_job.chartPath)!;
        var settings = (JObject?)chart["settings"] ?? new JObject();
        var bpm = settings.Value<float?>("bpm") ?? 100f;
        _pitch = Math.Max(0.01f, (settings.Value<float?>("pitch") ?? 100f) / 100f);
        var actions = (JArray?)chart["actions"] ?? new JArray();
        BuildFloorTimeline(chart, actions, bpm);
        _status.chartDurationSeconds = (float)_timeline.DurationSeconds;

        var cameraObject = new GameObject("Render Camera");
        _camera = cameraObject.AddComponent<Camera>();
        _camera.orthographic = true;
        _initialZoomRatio = Math.Max(0.01f, (settings.Value<float?>("zoom") ?? 100f) / 100f);
        _camera.orthographicSize = DefaultCameraOrthoSize * _initialZoomRatio;
        var initialCameraOffset = ReadVector2(settings["position"], Vector2.zero) * TileSize;
        var initialCameraBase = (settings.Value<string>("relativeTo") ?? "Player") == "Tile"
            ? _floorPositions[0]
            : Vector2.zero;
        _camera.transform.position = new Vector3(initialCameraBase.x + initialCameraOffset.x, initialCameraBase.y + initialCameraOffset.y, -10f);
        _camera.transform.rotation = Quaternion.Euler(0f, 0f, settings.Value<float?>("rotation") ?? 0f);
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = ParseColor(settings.Value<string>("backgroundColor") ?? "000000ff");
        _camera.aspect = _job.width / (float)_job.height;

        var backgroundObject = new GameObject("Custom Background");
        _backgroundRenderer = backgroundObject.AddComponent<SpriteRenderer>();
        _backgroundRenderer.sortingOrder = short.MinValue;
        _backgroundRenderer.enabled = false;

        foreach (var token in (JArray?)chart["decorations"] ?? new JArray())
        {
            if (token is JObject decoration && decoration.Value<string>("eventType") == "AddDecoration")
            {
                AddDecoration(decoration);
            }
        }

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
            }
        }

        _moves.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Order.CompareTo(right.Order));
        _cameraEvents.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Order.CompareTo(right.Order));
        _backgroundEvents.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Order.CompareTo(right.Order));
        ApplyTimeline(0f);
    }

    private void BuildFloorTimeline(JObject chart, JArray actions, float initialBpm)
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
        _floorPositions = new Vector2[angles.Count + 1];
        var entryAngle = -Mathf.PI * 0.5f;
        for (var floor = 0; floor < angles.Count; floor++)
        {
            var radians = Mathf.Approximately((float)angles[floor], 999f)
                ? entryAngle
                : (90f - (float)angles[floor]) * Mathf.Deg2Rad;
            _floorPositions[floor + 1] = _floorPositions[floor] +
                new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * TileSize;
            entryAngle = Mathf.Repeat(radians + Mathf.PI, Mathf.PI * 2f);
        }
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

    private void AddDecoration(JObject data)
    {
        var imageName = data.Value<string>("decorationImage");
        if (string.IsNullOrWhiteSpace(imageName))
        {
            return;
        }

        var imagePath = Path.Combine(_chartDirectory, imageName);
        if (!File.Exists(imagePath))
        {
            return;
        }

        var sprite = GetSprite(imagePath);
        if (sprite == null) return;

        var gameObject = new GameObject(imageName);
        var renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        var maskingType = data.Value<string>("maskingType") ?? "None";
        var spriteMask = gameObject.AddComponent<SpriteMask>();
        spriteMask.sprite = renderer.sprite;
        spriteMask.alphaCutoff = 0.001f;
        spriteMask.enabled = false;
        renderer.maskInteraction = maskingType switch
        {
            "VisibleInsideMask" => SpriteMaskInteraction.VisibleInsideMask,
            "VisibleOutsideMask" => SpriteMaskInteraction.VisibleOutsideMask,
            _ => SpriteMaskInteraction.None,
        };
        ApplyMaterial(
            renderer,
            data.Value<string>("blendMode") ?? "None",
            ReadVector2(data["tile"], Vector2.one));
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorPositions.Length - 1);
        var localPosition = ReadVector2(data["position"], Vector2.zero);
        var relativeTo = data.Value<string>("relativeTo") ?? "Tile";
        var startPosition = relativeTo switch
        {
            "Tile" => _floorPositions[floor] + localPosition * TileSize,
            "RedPlanet" or "BluePlanet" or "GreenPlanet" => _floorPositions[floor] + localPosition * TileSize,
            "Camera" or "CameraAspect" => localPosition,
            _ => localPosition * TileSize,
        };
        var rotation = data.Value<float?>("rotation") ?? 0f;
        var scale = ReadVector2(data["scale"], new Vector2(100f, 100f)) / 100f;
        var color = ParseColor(data.Value<string>("color") ?? "ffffffff");
        var opacity = (data.Value<float?>("opacity") ?? 100f) / 100f;
        var depth = data.Value<int?>("depth") ?? 0;
        if ((depth >= 0 && !_job.includeBackgroundDecorations) ||
            (depth < 0 && !_job.includeForegroundDecorations))
        {
            return;
        }
        renderer.sortingOrder = Math.Clamp(-depth, short.MinValue + 1, short.MaxValue);
        var visible = data.Value<bool?>("visible") ?? true;

        var tags = SplitTags(data.Value<string>("tag"));
        var parallax = ReadVector2(data["parallax"], Vector2.zero) / 100f;
        var pivotOffset = ReadVector2(data["pivotOffset"], Vector2.zero) *
                          (relativeTo is "Camera" or "CameraAspect" ? 1f : TileSize);
        var parallaxOffset = ReadVector2(data["parallaxOffset"], Vector2.zero) * TileSize;
        var state = new DecorationState(
            gameObject.transform,
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
            depth,
            spriteMask,
            maskingType,
            visible,
            data.Value<bool?>("useMaskingDepth") ?? false,
            data.Value<int?>("maskingFrontDepth") ?? depth,
            data.Value<int?>("maskingBackDepth") ?? depth,
            _initialZoomRatio);
        state.ApplyVisual(_camera);
        _decorations.Add(state);
    }

    private Texture2D? GetTexture(string imagePath)
    {
        if (_textures.TryGetValue(imagePath, out var existing)) return existing;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(imagePath), markNonReadable: true))
        {
            Destroy(texture);
            return null;
        }
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Bilinear;
        _textures[imagePath] = texture;
        return texture;
    }

    private Sprite? GetSprite(string imagePath)
    {
        if (_sprites.TryGetValue(imagePath, out var existing)) return existing;
        var texture = GetTexture(imagePath);
        if (texture == null) return null;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
        _sprites[imagePath] = sprite;
        return sprite;
    }

    private void ApplyMaterial(SpriteRenderer renderer, string blendMode, Vector2 tile)
    {
        Material material;
        if (blendMode is "None" or "Normal" or "")
        {
            if (_spriteMaterial == null)
            {
                var spriteShader = Resources.Load<Shader>("ADOFAISprite");
                if (spriteShader == null) throw new InvalidOperationException("Missing ADOFAI/VFX Sprite shader resource.");
                _spriteMaterial = new Material(spriteShader) { name = "ADOFAI VFX Sprite" };
            }
            material = _spriteMaterial;
        }
        else if (!_blendMaterials.TryGetValue(blendMode, out material))
        {
            var shader = Resources.Load<Shader>("ADOFAIBlend");
            if (shader == null) throw new InvalidOperationException("Missing ADOFAI/VFX Blend shader resource.");
            material = new Material(shader) { name = $"ADOFAI {blendMode} Blend" };
            material.SetFloat("_BlendMode", blendMode switch
            {
                "Difference" => 0f,
                "Overlay" => 1f,
                "SoftLight" => 2f,
                "LinearDodge" => 3f,
                _ => throw new NotSupportedException($"Unsupported decoration blend mode: {blendMode}"),
            });
            _blendMaterials[blendMode] = material;
        }
        renderer.sharedMaterial = material;
        var properties = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(properties);
        properties.SetVector("_Tile", new Vector4(Math.Max(0.000001f, tile.x), Math.Max(0.000001f, tile.y), 0f, 0f));
        renderer.SetPropertyBlock(properties);
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
        if (replaceSprite && data.Value<string>("decorationImage") is { Length: > 0 } imageName)
        {
            var imagePath = Path.Combine(_chartDirectory, imageName);
            if (File.Exists(imagePath)) sprite = GetSprite(imagePath);
        }
        _moves.Add(new MoveEvent(start, duration, SplitTags(data.Value<string>("tag")), data, replaceSprite, sprite, _eventSequence++));
    }

    private void AddCameraEvent(JObject data, int repeatedFloor, float angleOffset)
    {
        var floor = Math.Clamp(repeatedFloor, 0, _floorTimes.Length - 1);
        var bpm = (float)_timeline.BpmAtFloorAngleOffset(floor, angleOffset);
        var start = (float)_timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f / (bpm * _pitch));
        _cameraEvents.Add(new CameraEvent(start, duration, data, _floorPositions[floor], _eventSequence++));
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

            StartEncoder();
            target = new RenderTexture(_job.width, _job.height, 24, RenderTextureFormat.ARGB32);
            frameTexture = new Texture2D(_job.width, _job.height, TextureFormat.RGBA32, false, false);
            var watch = Stopwatch.StartNew();
            var previewWatch = Stopwatch.StartNew();
            for (var frame = 0; frame < totalFrames; frame++)
            {
                var time = _job.renderStartSeconds + frame / (float)_job.framesPerSecond;
                ApplyTimeline(time);
                _camera.targetTexture = target;
                _camera.Render();
                _camera.targetTexture = null;

                var previous = RenderTexture.active;
                RenderTexture.active = target;
                frameTexture.ReadPixels(new Rect(0f, 0f, _job.width, _job.height), 0, 0, false);
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
                if (previewWatch.Elapsed.TotalSeconds >= 0.1d || completed == totalFrames)
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

            _status.state = "complete";
            _status.percent = 100f;
            _status.message = "独立 Unity Worker 渲染完成。";
            WriteStatus();
            yield return null;
            Application.Quit(0);
        }
        finally
        {
            if (target != null) target.Release();
            if (frameTexture != null) Destroy(frameTexture);
        }
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
            AdvanceCamera(_cameraEvents[_nextCameraEvent].Start);
            var cameraEvent = _cameraEvents[_nextCameraEvent++];
            for (var index = _activeCameraEvents.Count - 1; index >= 0; index--)
            {
                _activeCameraEvents[index].CompleteConflicts(_camera, cameraEvent);
                if (_activeCameraEvents[index].IsComplete) _activeCameraEvents.RemoveAt(index);
            }
            cameraEvent.Capture(_camera);
            if (!cameraEvent.Apply(_camera, cameraEvent.Start)) _activeCameraEvents.Add(cameraEvent);
        }
        AdvanceCamera(time);

        while (_nextBackgroundEvent < _backgroundEvents.Count && _backgroundEvents[_nextBackgroundEvent].Start <= time)
        {
            ApplyBackground(_backgroundEvents[_nextBackgroundEvent++].Data);
        }

        foreach (var decoration in _decorations) decoration.ApplyVisual(_camera);
        UpdateBackgroundTransform();
    }

    private void AdvanceMoves(float time)
    {
        for (var index = _activeMoves.Count - 1; index >= 0; index--)
        {
            if (_activeMoves[index].Apply(time)) _activeMoves.RemoveAt(index);
        }
    }

    private void AdvanceCamera(float time)
    {
        for (var index = _activeCameraEvents.Count - 1; index >= 0; index--)
        {
            if (_activeCameraEvents[index].Apply(_camera, time)) _activeCameraEvents.RemoveAt(index);
        }
    }

    private void ApplyBackground(JObject data)
    {
        if (data.Value<string>("color") is { } color)
        {
            _camera.backgroundColor = ParseColor(color);
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
        var arguments = string.Format(
            CultureInfo.InvariantCulture,
            "-hide_banner -loglevel warning -y -f rawvideo -pixel_format rgba -video_size {0}x{1} -framerate {2} -i pipe:0 -vf vflip -an -c:v libopenh264 -b:v {3}k -pix_fmt yuv420p \"{4}\"",
            _job.width,
            _job.height,
            _job.framesPerSecond,
            Math.Max(1, _job.bitrateKbps),
            _job.outputPath.Replace("\"", "\\\""));
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

    private string WritePreview(byte[] imageBytes, int frame)
    {
        var path = Path.Combine(_job.previewDirectory, $"preview-{frame % 2}.jpg");
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, imageBytes);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
        return path;
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

    private static Vector2 ReadVector2(JToken? token, Vector2 fallback)
    {
        if (token is not JArray { Count: >= 2 } array) return fallback;
        return new Vector2(ReadFloat(array[0], fallback.x), ReadFloat(array[1], fallback.y));
    }

    private static float ReadFloat(JToken? token, float fallback) =>
        token == null || token.Type is JTokenType.Null or JTokenType.Undefined
            ? fallback
            : token.Value<float>();

    private static HashSet<string> SplitTags(string? tags) =>
        new((string.IsNullOrWhiteSpace(tags) ? "NO TAG" : tags)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static bool HasValue(JObject data, string key) =>
        data.TryGetValue(key, out var token) && token.Type is not (JTokenType.Null or JTokenType.Undefined);

    private static float EaseAmount(float amount, string? ease)
    {
        amount = Mathf.Clamp01(amount);
        const float back = 1.70158f;
        const float backInOut = back * 1.525f;
        var elastic = 2f * Mathf.PI / 3f;
        var elasticInOut = 2f * Mathf.PI / 4.5f;
        return ease switch
        {
            "InSine" => 1f - Mathf.Cos(amount * Mathf.PI * 0.5f),
            "OutSine" => Mathf.Sin(amount * Mathf.PI * 0.5f),
            "InOutSine" => -(Mathf.Cos(Mathf.PI * amount) - 1f) * 0.5f,
            "InQuad" => amount * amount,
            "OutQuad" => 1f - (1f - amount) * (1f - amount),
            "InOutQuad" => amount < 0.5f ? 2f * amount * amount : 1f - Mathf.Pow(-2f * amount + 2f, 2f) * 0.5f,
            "InCubic" => amount * amount * amount,
            "OutCubic" => 1f - Mathf.Pow(1f - amount, 3f),
            "InOutCubic" => amount < 0.5f ? 4f * amount * amount * amount : 1f - Mathf.Pow(-2f * amount + 2f, 3f) * 0.5f,
            "InQuart" => Mathf.Pow(amount, 4f),
            "OutQuart" => 1f - Mathf.Pow(1f - amount, 4f),
            "InOutQuart" => amount < 0.5f ? 8f * Mathf.Pow(amount, 4f) : 1f - Mathf.Pow(-2f * amount + 2f, 4f) * 0.5f,
            "InQuint" => Mathf.Pow(amount, 5f),
            "OutQuint" => 1f - Mathf.Pow(1f - amount, 5f),
            "InOutQuint" => amount < 0.5f ? 16f * Mathf.Pow(amount, 5f) : 1f - Mathf.Pow(-2f * amount + 2f, 5f) * 0.5f,
            "InExpo" => amount <= 0f ? 0f : Mathf.Pow(2f, 10f * amount - 10f),
            "OutExpo" => amount >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * amount),
            "InOutExpo" => amount <= 0f ? 0f : amount >= 1f ? 1f : amount < 0.5f
                ? Mathf.Pow(2f, 20f * amount - 10f) * 0.5f
                : (2f - Mathf.Pow(2f, -20f * amount + 10f)) * 0.5f,
            "InCirc" => 1f - Mathf.Sqrt(1f - amount * amount),
            "OutCirc" => Mathf.Sqrt(1f - Mathf.Pow(amount - 1f, 2f)),
            "InOutCirc" => amount < 0.5f
                ? (1f - Mathf.Sqrt(1f - Mathf.Pow(2f * amount, 2f))) * 0.5f
                : (Mathf.Sqrt(1f - Mathf.Pow(-2f * amount + 2f, 2f)) + 1f) * 0.5f,
            "InBack" => (back + 1f) * amount * amount * amount - back * amount * amount,
            "OutBack" => 1f + (back + 1f) * Mathf.Pow(amount - 1f, 3f) + back * Mathf.Pow(amount - 1f, 2f),
            "InOutBack" => amount < 0.5f
                ? Mathf.Pow(2f * amount, 2f) * ((backInOut + 1f) * 2f * amount - backInOut) * 0.5f
                : (Mathf.Pow(2f * amount - 2f, 2f) * ((backInOut + 1f) * (amount * 2f - 2f) + backInOut) + 2f) * 0.5f,
            "InElastic" => amount <= 0f ? 0f : amount >= 1f ? 1f
                : -Mathf.Pow(2f, 10f * amount - 10f) * Mathf.Sin((amount * 10f - 10.75f) * elastic),
            "OutElastic" => amount <= 0f ? 0f : amount >= 1f ? 1f
                : Mathf.Pow(2f, -10f * amount) * Mathf.Sin((amount * 10f - 0.75f) * elastic) + 1f,
            "InOutElastic" => amount <= 0f ? 0f : amount >= 1f ? 1f : amount < 0.5f
                ? -(Mathf.Pow(2f, 20f * amount - 10f) * Mathf.Sin((20f * amount - 11.125f) * elasticInOut)) * 0.5f
                : Mathf.Pow(2f, -20f * amount + 10f) * Mathf.Sin((20f * amount - 11.125f) * elasticInOut) * 0.5f + 1f,
            "InBounce" => 1f - EaseOutBounce(1f - amount),
            "OutBounce" => EaseOutBounce(amount),
            "InOutBounce" => amount < 0.5f
                ? (1f - EaseOutBounce(1f - 2f * amount)) * 0.5f
                : (1f + EaseOutBounce(2f * amount - 1f)) * 0.5f,
            _ => amount,
        };
    }

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
        public int width = 640;
        public int height = 360;
        public int framesPerSecond = 30;
        public int bitrateKbps = 2000;
        public float tailSeconds;
        public float durationLimitSeconds;
        public float renderStartSeconds;
        public bool includeTrack;
        public bool includePlanets;
        public bool includeEffects;
        public bool includeBackgroundDecorations = true;
        public bool includeForegroundDecorations = true;
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
        public int currentFrame;
        public int totalFrames;
        public float percent;
        public float renderedFramesPerSecond;
        public float secondsPerFrame;
        public float chartDurationSeconds;
    }

    private sealed class DecorationState
    {
        public DecorationState(
            Transform transform,
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
            int depth,
            SpriteMask? spriteMask,
            string maskingType,
            bool visible,
            bool useMaskingDepth,
            int maskingFrontDepth,
            int maskingBackDepth,
            float initialZoomRatio)
        {
            Transform = transform;
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
            Depth = depth;
            SpriteMask = spriteMask;
            MaskingType = maskingType;
            Visible = visible;
            UseMaskingDepth = useMaskingDepth;
            MaskingFrontDepth = maskingFrontDepth;
            MaskingBackDepth = maskingBackDepth;
            InitialZoomRatio = initialZoomRatio;
        }
        public Transform Transform { get; }
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
        public int Depth { get; set; }
        public SpriteMask? SpriteMask { get; }
        public string MaskingType { get; private set; }
        public bool Visible { get; private set; }
        public bool UseMaskingDepth { get; private set; }
        public int MaskingFrontDepth { get; private set; }
        public int MaskingBackDepth { get; private set; }
        public float InitialZoomRatio { get; }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            Renderer.enabled = visible && MaskingType != "Mask";
            if (SpriteMask != null) SpriteMask.enabled = visible && MaskingType == "Mask";
        }

        public void SetDepth(int depth)
        {
            Depth = depth;
            Renderer.sortingOrder = Math.Clamp(-depth, short.MinValue + 1, short.MaxValue);
            RefreshMaskRange();
        }

        public void SetMasking(string? maskingType = null, bool? useDepth = null, int? frontDepth = null, int? backDepth = null)
        {
            if (maskingType != null) MaskingType = maskingType;
            if (useDepth.HasValue) UseMaskingDepth = useDepth.Value;
            if (frontDepth.HasValue) MaskingFrontDepth = frontDepth.Value;
            if (backDepth.HasValue) MaskingBackDepth = backDepth.Value;
            Renderer.maskInteraction = MaskingType switch
            {
                "VisibleInsideMask" => SpriteMaskInteraction.VisibleInsideMask,
                "VisibleOutsideMask" => SpriteMaskInteraction.VisibleOutsideMask,
                _ => SpriteMaskInteraction.None,
            };
            SetVisible(Visible);
            RefreshMaskRange();
        }

        public void SetSprite(Sprite? sprite)
        {
            Renderer.sprite = sprite;
            if (SpriteMask != null) SpriteMask.sprite = sprite;
        }

        private void RefreshMaskRange()
        {
            if (SpriteMask == null) return;
            SpriteMask.isCustomRangeActive = UseMaskingDepth;
            var front = Math.Min(MaskingFrontDepth, MaskingBackDepth);
            var back = Math.Max(MaskingFrontDepth, MaskingBackDepth);
            SpriteMask.frontSortingOrder = Math.Clamp(-front + 1, short.MinValue + 1, short.MaxValue);
            SpriteMask.backSortingOrder = Math.Clamp(-back, short.MinValue + 1, short.MaxValue);
        }

        public void ApplyVisual(Camera camera)
        {
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
                worldPosition = Vector2.Scale(cameraPosition - PivotPosition, Parallax) + PivotPosition + ParallaxOffset;
                var angle = StartRotation + RotationOffset + (LockRotation ? camera.transform.eulerAngles.z : 0f);
                worldPosition += (Vector2)(Quaternion.Euler(0f, 0f, angle) * PivotOffset);
            }

            Transform.position = new Vector3(worldPosition.x, worldPosition.y, 0f);
            Transform.rotation = Quaternion.Euler(0f, 0f,
                StartRotation + RotationOffset + (LockRotation ? camera.transform.eulerAngles.z : 0f));
            var cameraScale = LockScale
                ? camera.orthographicSize / (DefaultCameraOrthoSize * InitialZoomRatio)
                : 1f;
            Transform.localScale = new Vector3(Scale.x * cameraScale, Scale.y * cameraScale, 1f);
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
        public MoveEvent(float start, float duration, HashSet<string> tags, JObject data, bool replaceSprite, Sprite? replacementSprite, int order)
        {
            Start = start;
            Duration = duration;
            _tags = tags;
            _data = data;
            _properties = GetProperties(data);
            _replaceSprite = replaceSprite;
            _replacementSprite = replacementSprite;
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
            foreach (var decoration in decorations)
            {
                if (!_tags.Overlaps(decoration.Tags)) continue;
                if (HasValue(_data, "relativeTo") && _data.Value<string>("relativeTo") is { } relativeTo && relativeTo != "LastPosition")
                    decoration.RelativeTo = relativeTo;
                if (HasValue(_data, "visible")) decoration.SetVisible(_data.Value<bool>("visible"));
                if (HasValue(_data, "depth")) decoration.SetDepth(_data.Value<int>("depth"));
                if (_replaceSprite) decoration.SetSprite(_replacementSprite);
                if (HasValue(_data, "maskingType") || HasValue(_data, "useMaskingDepth") ||
                    HasValue(_data, "maskingFrontDepth") || HasValue(_data, "maskingBackDepth"))
                {
                    decoration.SetMasking(
                        HasValue(_data, "maskingType") ? _data.Value<string>("maskingType") : null,
                        HasValue(_data, "useMaskingDepth") ? _data.Value<bool?>("useMaskingDepth") : null,
                        HasValue(_data, "maskingFrontDepth") ? _data.Value<int?>("maskingFrontDepth") : null,
                        HasValue(_data, "maskingBackDepth") ? _data.Value<int?>("maskingBackDepth") : null);
                }
                _targets.Add(new Snapshot(decoration, _data, _properties));
            }
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
        private readonly Vector2 _floorPosition;
        private Property _active;
        private readonly Property _properties;
        public CameraEvent(float start, float duration, JObject data, Vector2 floorPosition, int order)
        {
            Start = start;
            Duration = duration;
            _data = data;
            _floorPosition = floorPosition;
            Order = order;
            var position = ReadVector2(data["position"], new Vector2(float.NaN, float.NaN));
            var relativeUsed = HasValue(data, "relativeTo");
            if (!float.IsNaN(position.x) || relativeUsed) _properties |= Property.PositionX;
            if (!float.IsNaN(position.y) || relativeUsed) _properties |= Property.PositionY;
            if (HasValue(data, "rotation") || (relativeUsed && (data.Value<string>("relativeTo") ?? "") == "LastPosition"))
                _properties |= Property.Rotation;
            if (HasValue(data, "zoom")) _properties |= Property.Zoom;
        }
        public float Start { get; }
        public int Order { get; }
        private float Duration { get; }
        public bool IsComplete => _active == Property.None;

        public void CompleteConflicts(Camera camera, CameraEvent incoming)
        {
            var overlap = _active & incoming._properties;
            if ((overlap & Property.PositionX) != 0) camera.transform.position = new Vector3(_targetPosition.x, camera.transform.position.y, -10f);
            if ((overlap & Property.PositionY) != 0) camera.transform.position = new Vector3(camera.transform.position.x, _targetPosition.y, -10f);
            if ((overlap & Property.Rotation) != 0) camera.transform.rotation = Quaternion.Euler(0f, 0f, _targetRotation);
            if ((overlap & Property.Zoom) != 0) camera.orthographicSize = _targetSize;
            _active &= ~overlap;
        }

        public void Capture(Camera camera)
        {
            _active = _properties;
            _position = camera.transform.position;
            _rotation = camera.transform.eulerAngles.z;
            _size = camera.orthographicSize;
            var requested = ReadVector2(_data["position"], new Vector2(float.NaN, float.NaN)) * TileSize;
            if (float.IsNaN(requested.x)) requested.x = 0f;
            if (float.IsNaN(requested.y)) requested.y = 0f;
            var relativeTo = _data.Value<string>("relativeTo") ?? "LastPosition";
            var target = relativeTo switch
            {
                "Tile" => _floorPosition + requested,
                "Global" => requested,
                "Player" => _floorPosition + requested,
                _ => new Vector2(_position.x, _position.y) + requested,
            };
            _targetPosition = new Vector3(target.x, target.y, -10f);
            _targetRotation = _data.Value<float?>("rotation") ?? _rotation;
            if (relativeTo == "LastPosition" && !HasValue(_data, "rotation")) _targetRotation = _rotation;
            _targetSize = _data.Value<float?>("zoom") is { } zoom
                ? DefaultCameraOrthoSize * Math.Max(0.01f, zoom / 100f)
                : _size;
        }
        public bool Apply(Camera camera, float time)
        {
            var rawAmount = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            var amount = EaseAmount(rawAmount, _data.Value<string>("ease"));
            var current = camera.transform.position;
            if ((_active & Property.PositionX) != 0) current.x = Mathf.LerpUnclamped(_position.x, _targetPosition.x, amount);
            if ((_active & Property.PositionY) != 0) current.y = Mathf.LerpUnclamped(_position.y, _targetPosition.y, amount);
            camera.transform.position = new Vector3(current.x, current.y, -10f);
            if ((_active & Property.Rotation) != 0)
                camera.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpUnclamped(_rotation, _targetRotation, amount));
            if ((_active & Property.Zoom) != 0)
                camera.orthographicSize = Mathf.LerpUnclamped(_size, _targetSize, amount);
            if (rawAmount >= 1f) _active = Property.None;
            return rawAmount >= 1f || IsComplete;
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
