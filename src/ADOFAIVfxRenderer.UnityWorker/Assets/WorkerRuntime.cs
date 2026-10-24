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
using Debug = UnityEngine.Debug;

namespace ADOFAIVfxWorker
{
public sealed class WorkerRuntime : MonoBehaviour
{
    private const float TileSize = 1.5f;
    private const float DefaultCameraOrthoSize = 5f;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
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
    private float[] _floorTimes = Array.Empty<float>();
    private float[] _floorBpms = Array.Empty<float>();
    private Vector2[] _floorPositions = Array.Empty<Vector2>();
    private string _chartDirectory = "";
    private float _pitch = 1f;
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
        BuildFloorTimeline(
            (JArray?)chart["angleData"],
            (JArray?)chart["actions"],
            bpm,
            settings.Value<int?>("countdownTicks") ?? 4);

        var cameraObject = new GameObject("Render Camera");
        _camera = cameraObject.AddComponent<Camera>();
        _camera.orthographic = true;
        _camera.orthographicSize = DefaultCameraOrthoSize * Math.Max(0.01f, (settings.Value<float?>("zoom") ?? 100f) / 100f);
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

        var actions = (JArray?)chart["actions"] ?? new JArray();
        var repeatRules = BuildRepeatRules(actions);
        foreach (var token in actions)
        {
            if (token is not JObject action)
            {
                continue;
            }

            var eventType = action.Value<string>("eventType");
            foreach (var repeatedAngleOffset in GetRepeatedAngleOffsets(action, repeatRules))
            {
                if (eventType == "MoveDecorations")
                {
                    AddMoveEvent(action, repeatedAngleOffset);
                }
                else if (eventType == "MoveCamera")
                {
                    AddCameraEvent(action, repeatedAngleOffset);
                }
                else if (eventType == "CustomBackground")
                {
                    AddBackgroundEvent(action, repeatedAngleOffset);
                }
            }
        }

        _moves.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        _cameraEvents.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        _backgroundEvents.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        ApplyTimeline(0f);
    }

    private void BuildFloorTimeline(JArray? angles, JArray? actions, float initialBpm, int countdownTicks)
    {
        var count = Math.Max(1, angles?.Count ?? 1);
        _floorTimes = new float[count];
        _floorBpms = new float[count];
        _floorPositions = new Vector2[count];
        var actionsByFloor = new Dictionary<int, List<JObject>>();
        if (actions != null)
        {
            foreach (var action in actions.OfType<JObject>())
            {
                if (action.Value<string>("eventType") is "SetSpeed" or "Pause" or "Twirl" or "MultiPlanet")
                {
                    var floor = action.Value<int?>("floor") ?? 0;
                    if (!actionsByFloor.TryGetValue(floor, out var list))
                    {
                        actionsByFloor[floor] = list = new List<JObject>();
                    }
                    list.Add(action);
                }
            }
        }

        var bpm = initialBpm;
        var twirled = false;
        var threePlanets = false;
        for (var floor = 0; floor < count; floor++)
        {
            var pauseBeats = 0f;
            if (actionsByFloor.TryGetValue(floor, out var changes))
            {
                foreach (var change in changes)
                {
                    switch (change.Value<string>("eventType"))
                    {
                    case "SetSpeed":
                        bpm = change.Value<string>("speedType") == "Multiplier"
                            ? bpm * (change.Value<float?>("bpmMultiplier") ?? 1f)
                            : change.Value<float?>("beatsPerMinute") ?? bpm;
                        break;
                    case "Pause":
                        pauseBeats += change.Value<float?>("duration") ?? 0f;
                        break;
                    case "Twirl":
                        twirled = !twirled;
                        break;
                    case "MultiPlanet":
                        threePlanets = change.Value<string>("planets") == "ThreePlanets";
                        break;
                    }
                }
            }

            _floorBpms[floor] = Math.Max(1f, bpm);
            if (floor >= count - 1)
            {
                break;
            }

            var currentAngle = angles?[floor]?.Value<float>() ?? 0f;
            var nextIndex = floor + 1;
            var nextAngle = angles?[nextIndex]?.Value<float>() ?? currentAngle;
            var nextIsMidspin = Mathf.Approximately(nextAngle, 999f);
            if (nextIsMidspin && nextIndex + 1 < count)
            {
                nextAngle = angles?[nextIndex + 1]?.Value<float>() ?? currentAngle;
            }

            var turn = Mathf.Repeat(nextAngle - currentAngle + (nextIsMidspin ? 360f : 540f), 360f);
            if (twirled) turn = 360f - turn;
            if (turn < 0.001f) turn = 360f;
            turn += pauseBeats * 180f;
            if (threePlanets && !nextIsMidspin) turn = turn > 60f ? turn - 60f : turn + 300f;
            var seconds = turn / 180f * 60f / (_floorBpms[floor] * _pitch);
            if (floor == 0)
            {
                seconds += Math.Max(0, countdownTicks - 1) * 60f / (Math.Max(1f, initialBpm) * _pitch);
            }
            _floorTimes[floor + 1] = _floorTimes[floor] + seconds;

            var positionAngle = Mathf.Approximately(currentAngle, 999f) ? 0f : currentAngle;
            var radians = positionAngle * Mathf.Deg2Rad;
            _floorPositions[floor + 1] = _floorPositions[floor] +
                new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * TileSize;
        }
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

        var texture = GetTexture(imagePath);
        if (texture == null) return;

        var gameObject = new GameObject(imageName);
        var renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
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
        renderer.enabled = data.Value<bool?>("visible") ?? true;

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
            depth);
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
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        _textures[imagePath] = texture;
        return texture;
    }

    private static Dictionary<int, Dictionary<string, RepeatRule>> BuildRepeatRules(JArray actions)
    {
        var result = new Dictionary<int, Dictionary<string, RepeatRule>>();
        foreach (var repeat in actions.OfType<JObject>().Where(static action =>
                     action.Value<string>("eventType") == "RepeatEvents"))
        {
            if ((repeat.Value<string>("repeatType") ?? "Beat") != "Beat") continue;
            var floor = repeat.Value<int?>("floor") ?? 0;
            if (!result.TryGetValue(floor, out var byTag)) result[floor] = byTag = new(StringComparer.Ordinal);
            var rule = new RepeatRule(
                Math.Max(0, repeat.Value<int?>("repetitions") ?? 0),
                Math.Max(0f, repeat.Value<float?>("interval") ?? 0f));
            foreach (var tag in SplitTags(repeat.Value<string>("tag"))) byTag[tag] = rule;
        }
        return result;
    }

    private static IEnumerable<float> GetRepeatedAngleOffsets(
        JObject action,
        Dictionary<int, Dictionary<string, RepeatRule>> rules)
    {
        var baseOffset = action.Value<float?>("angleOffset") ?? 0f;
        var floor = action.Value<int?>("floor") ?? 0;
        if (!rules.TryGetValue(floor, out var byTag) || string.IsNullOrWhiteSpace(action.Value<string>("eventTag")))
        {
            yield return baseOffset;
            yield break;
        }

        RepeatRule? matched = null;
        foreach (var tag in SplitTags(action.Value<string>("eventTag")))
        {
            if (byTag.TryGetValue(tag, out matched)) break;
        }
        if (matched == null)
        {
            yield return baseOffset;
            yield break;
        }

        for (var repetition = 0; repetition <= matched.Repetitions; repetition++)
        {
            yield return baseOffset + matched.IntervalBeats * repetition * 180f;
        }
    }

    private void AddMoveEvent(JObject data, float angleOffset)
    {
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorTimes.Length - 1);
        var bpm = _floorBpms[floor];
        var start = _floorTimes[floor] + AngleOffsetSeconds(angleOffset, bpm, _pitch);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f / (bpm * _pitch));
        _moves.Add(new MoveEvent(start, duration, SplitTags(data.Value<string>("tag")), data));
    }

    private void AddCameraEvent(JObject data, float angleOffset)
    {
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorTimes.Length - 1);
        var bpm = _floorBpms[floor];
        var start = _floorTimes[floor] + AngleOffsetSeconds(angleOffset, bpm, _pitch);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f / (bpm * _pitch));
        _cameraEvents.Add(new CameraEvent(start, duration, data, _floorPositions[floor]));
    }

    private void AddBackgroundEvent(JObject data, float angleOffset)
    {
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorTimes.Length - 1);
        var start = _floorTimes[floor] + AngleOffsetSeconds(
            angleOffset,
            _floorBpms[floor],
            _pitch);
        _backgroundEvents.Add(new BackgroundEvent(start, data));
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
                : (_floorTimes.Length == 0 ? 1f : _floorTimes[^1]) + Math.Max(0f, _job.tailSeconds);
            var totalFrames = Math.Max(1, Mathf.CeilToInt(duration * _job.framesPerSecond));
            _status.totalFrames = totalFrames;
            _status.state = "rendering";
            _status.message = "独立 Unity Worker 正在逐帧渲染……";
            WriteStatus();

            StartEncoder();
            target = new RenderTexture(_job.width, _job.height, 24, RenderTextureFormat.ARGB32);
            frameTexture = new Texture2D(_job.width, _job.height, TextureFormat.RGBA32, false, false);
            var watch = Stopwatch.StartNew();
            for (var frame = 0; frame < totalFrames; frame++)
            {
                var time = frame / (float)_job.framesPerSecond;
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
                if (frame % Math.Max(1, _job.framesPerSecond / 2) == 0 || completed == totalFrames)
                {
                    _status.previewPath = WritePreview(ImageConversion.EncodeToPNG(frameTexture), completed);
                    _status.message = $"已完成 {completed:N0} / {totalFrames:N0} 帧";
                    WriteStatus();
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
            move.Capture(_decorations);
            if (!move.Apply(move.Start)) _activeMoves.Add(move);
        }
        AdvanceMoves(time);

        while (_nextCameraEvent < _cameraEvents.Count && _cameraEvents[_nextCameraEvent].Start <= time)
        {
            AdvanceCamera(_cameraEvents[_nextCameraEvent].Start);
            var cameraEvent = _cameraEvents[_nextCameraEvent++];
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

    private string WritePreview(byte[] png, int frame)
    {
        var path = Path.Combine(_job.previewDirectory, $"preview-{frame % 2}.png");
        File.WriteAllBytes(path, png);
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

    private static float AngleOffsetSeconds(float angleOffset, float bpm, float pitch) =>
        angleOffset / 180f * 60f / (Math.Max(1f, bpm) * Math.Max(0.01f, pitch));

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
            "InCirc" => 1f - Mathf.Sqrt(1f - amount * amount),
            "OutCirc" => Mathf.Sqrt(1f - Mathf.Pow(amount - 1f, 2f)),
            "InOutCirc" => amount < 0.5f
                ? (1f - Mathf.Sqrt(1f - Mathf.Pow(2f * amount, 2f))) * 0.5f
                : (Mathf.Sqrt(1f - Mathf.Pow(-2f * amount + 2f, 2f)) + 1f) * 0.5f,
            _ => amount,
        };
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
            int depth)
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
            Transform.localScale = new Vector3(Scale.x, Scale.y, 1f);
            var renderColor = Color;
            renderColor.a *= Opacity;
            Renderer.color = renderColor;
            Renderer.sortingOrder = Math.Clamp(-Depth, short.MinValue + 1, short.MaxValue);
        }
    }

    private sealed class MoveEvent
    {
        private readonly HashSet<string> _tags;
        private readonly JObject _data;
        private readonly List<Snapshot> _targets = new();
        public MoveEvent(float start, float duration, HashSet<string> tags, JObject data)
        {
            Start = start;
            Duration = duration;
            _tags = tags;
            _data = data;
        }
        public float Start { get; }
        private float Duration { get; }
        public void Capture(IEnumerable<DecorationState> decorations)
        {
            foreach (var decoration in decorations)
            {
                if (!_tags.Overlaps(decoration.Tags)) continue;
                _targets.Add(new Snapshot(decoration));
            }
        }
        public bool Apply(float time)
        {
            var rawAmount = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            var amount = EaseAmount(rawAmount, _data.Value<string>("ease"));
            foreach (var target in _targets)
            {
                if (HasValue(_data, "positionOffset"))
                {
                    var offset = ReadVector2(_data["positionOffset"], new Vector2(float.NaN, float.NaN)) * TileSize;
                    var basePosition = (_data.Value<string>("relativeTo") ?? "LastPosition") == "LastPosition"
                        ? target.PivotPosition
                        : target.State.StartPosition;
                    var destination = target.PivotPosition;
                    if (!float.IsNaN(offset.x)) destination.x = basePosition.x + offset.x;
                    if (!float.IsNaN(offset.y)) destination.y = basePosition.y + offset.y;
                    target.State.PivotPosition = Vector2.Lerp(target.PivotPosition, destination, amount);
                }
                if (HasValue(_data, "pivotOffset"))
                {
                    var pivot = ReadVector2(_data["pivotOffset"], new Vector2(float.NaN, float.NaN)) * TileSize;
                    var destination = target.PivotOffset;
                    if (!float.IsNaN(pivot.x)) destination.x = pivot.x;
                    if (!float.IsNaN(pivot.y)) destination.y = pivot.y;
                    target.State.PivotOffset = Vector2.Lerp(target.PivotOffset, destination, amount);
                }
                if (HasValue(_data, "scale"))
                {
                    var scale = ReadVector2(_data["scale"], new Vector2(float.NaN, float.NaN)) / 100f;
                    var destination = target.Scale;
                    if (!float.IsNaN(scale.x)) destination.x = scale.x;
                    if (!float.IsNaN(scale.y)) destination.y = scale.y;
                    target.State.Scale = Vector2.Lerp(target.Scale, destination, amount);
                }
                if (HasValue(_data, "rotationOffset"))
                {
                    var rotation = _data.Value<float>("rotationOffset");
                    target.State.RotationOffset = Mathf.LerpUnclamped(target.RotationOffset, rotation, amount);
                }
                if (_data.Value<string>("color") is { } colorHex)
                    target.State.Color = Color.Lerp(target.Color, ParseColor(colorHex), amount);
                if (_data.Value<float?>("opacity") is { } opacity)
                    target.State.Opacity = Mathf.Lerp(target.Opacity, opacity / 100f, amount);
                if (HasValue(_data, "parallax"))
                {
                    var parallax = ReadVector2(_data["parallax"], target.Parallax * 100f) / 100f;
                    target.State.Parallax = Vector2.Lerp(target.Parallax, parallax, amount);
                }
                if (HasValue(_data, "parallaxOffset"))
                {
                    var parallaxOffset = ReadVector2(_data["parallaxOffset"], target.ParallaxOffset / TileSize) * TileSize;
                    target.State.ParallaxOffset = Vector2.Lerp(target.ParallaxOffset, parallaxOffset, amount);
                }
                if (HasValue(_data, "depth")) target.State.Depth = _data.Value<int>("depth");
                if (HasValue(_data, "visible")) target.State.Renderer.enabled = _data.Value<bool>("visible");
            }
            return rawAmount >= 1f;
        }
        private sealed class Snapshot
        {
            public Snapshot(DecorationState state)
            {
                State = state;
                PivotPosition = state.PivotPosition;
                PivotOffset = state.PivotOffset;
                Scale = state.Scale;
                RotationOffset = state.RotationOffset;
                Color = state.Color;
                Opacity = state.Opacity;
                Parallax = state.Parallax;
                ParallaxOffset = state.ParallaxOffset;
            }
            public DecorationState State { get; }
            public Vector2 PivotPosition { get; }
            public Vector2 PivotOffset { get; }
            public Vector2 Scale { get; }
            public float RotationOffset { get; }
            public Color Color { get; }
            public float Opacity { get; }
            public Vector2 Parallax { get; }
            public Vector2 ParallaxOffset { get; }
        }
    }

    private sealed class CameraEvent
    {
        private readonly JObject _data;
        private Vector3 _position;
        private float _rotation;
        private float _size;
        private readonly Vector2 _floorPosition;
        public CameraEvent(float start, float duration, JObject data, Vector2 floorPosition)
        {
            Start = start;
            Duration = duration;
            _data = data;
            _floorPosition = floorPosition;
        }
        public float Start { get; }
        private float Duration { get; }
        public void Capture(Camera camera) { _position = camera.transform.position; _rotation = camera.transform.eulerAngles.z; _size = camera.orthographicSize; }
        public bool Apply(Camera camera, float time)
        {
            var rawAmount = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            var amount = EaseAmount(rawAmount, _data.Value<string>("ease"));
            if (HasValue(_data, "position"))
            {
                var offset = ReadVector2(_data["position"], Vector2.zero) * TileSize;
                var target = (_data.Value<string>("relativeTo") ?? "LastPosition") switch
                {
                    "Tile" => _floorPosition + offset,
                    "Global" => offset,
                    "Player" => _floorPosition + offset,
                    _ => new Vector2(_position.x, _position.y) + offset,
                };
                camera.transform.position = Vector3.Lerp(_position, new Vector3(target.x, target.y, -10f), amount);
            }
            if (_data.Value<float?>("rotation") is { } rotation)
                camera.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(_rotation, rotation, amount));
            if (_data.Value<float?>("zoom") is { } zoom)
                camera.orthographicSize = Mathf.Lerp(_size, DefaultCameraOrthoSize * Math.Max(0.01f, zoom / 100f), amount);
            return rawAmount >= 1f;
        }
    }

    private sealed class BackgroundEvent
    {
        public BackgroundEvent(float start, JObject data)
        {
            Start = start;
            Data = data;
        }
        public float Start { get; }
        public JObject Data { get; }
    }

    private sealed class RepeatRule
    {
        public RepeatRule(int repetitions, float intervalBeats)
        {
            Repetitions = repetitions;
            IntervalBeats = intervalBeats;
        }
        public int Repetitions { get; }
        public float IntervalBeats { get; }
    }
}
}
