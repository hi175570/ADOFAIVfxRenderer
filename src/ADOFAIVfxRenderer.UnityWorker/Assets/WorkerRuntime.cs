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
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DecorationState> _decorations = new();
    private readonly List<MoveEvent> _moves = new();
    private readonly List<MoveEvent> _activeMoves = new();
    private readonly List<CameraEvent> _cameraEvents = new();
    private readonly List<CameraEvent> _activeCameraEvents = new();
    private WorkerJob _job = null!;
    private WorkerStatus _status = null!;
    private Camera _camera = null!;
    private Process? _ffmpeg;
    private int _nextMove;
    private int _nextCameraEvent;
    private float[] _floorTimes = Array.Empty<float>();
    private Vector2[] _floorPositions = Array.Empty<Vector2>();
    private string _chartDirectory = "";

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
        BuildFloorTimeline((JArray?)chart["angleData"], (JArray?)chart["actions"], bpm);

        var cameraObject = new GameObject("Render Camera");
        _camera = cameraObject.AddComponent<Camera>();
        _camera.orthographic = true;
        _camera.orthographicSize = 5f * 100f / Math.Max(1f, settings.Value<float?>("zoom") ?? 100f);
        _camera.transform.position = new Vector3(0f, 0f, -10f);
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = ParseColor(settings.Value<string>("backgroundColor") ?? "000000ff");
        _camera.aspect = _job.width / (float)_job.height;

        foreach (var token in (JArray?)chart["decorations"] ?? new JArray())
        {
            if (token is JObject decoration && decoration.Value<string>("eventType") == "AddDecoration")
            {
                AddDecoration(decoration);
            }
        }

        foreach (var token in (JArray?)chart["actions"] ?? new JArray())
        {
            if (token is not JObject action)
            {
                continue;
            }

            var eventType = action.Value<string>("eventType");
            if (eventType == "MoveDecorations")
            {
                AddMoveEvent(action, bpm);
            }
            else if (eventType == "MoveCamera")
            {
                AddCameraEvent(action, bpm);
            }
            else if (eventType == "CustomBackground" && action.Value<string>("color") is { } color)
            {
                _camera.backgroundColor = ParseColor(color);
            }
        }

        _moves.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        _cameraEvents.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        ApplyTimeline(0f);
    }

    private void BuildFloorTimeline(JArray? angles, JArray? actions, float initialBpm)
    {
        var count = Math.Max(1, angles?.Count ?? 1);
        _floorTimes = new float[count];
        _floorPositions = new Vector2[count];
        var bpmByFloor = new Dictionary<int, List<JObject>>();
        if (actions != null)
        {
            foreach (var action in actions.OfType<JObject>())
            {
                if (action.Value<string>("eventType") is "SetSpeed" or "Pause")
                {
                    var floor = action.Value<int?>("floor") ?? 0;
                    if (!bpmByFloor.TryGetValue(floor, out var list))
                    {
                        bpmByFloor[floor] = list = new List<JObject>();
                    }
                    list.Add(action);
                }
            }
        }

        var bpm = initialBpm;
        var previousAngle = 0f;
        for (var floor = 1; floor < count; floor++)
        {
            if (bpmByFloor.TryGetValue(floor - 1, out var changes))
            {
                foreach (var change in changes)
                {
                    if (change.Value<string>("eventType") == "SetSpeed")
                    {
                        bpm = change.Value<string>("speedType") == "Multiplier"
                            ? bpm * (change.Value<float?>("bpmMultiplier") ?? 1f)
                            : change.Value<float?>("beatsPerMinute") ?? bpm;
                    }
                    else
                    {
                        _floorTimes[floor - 1] += (change.Value<float?>("duration") ?? 0f) * 60f / bpm;
                    }
                }
            }

            var angle = angles?[floor - 1]?.Value<float>() ?? 0f;
            if (Mathf.Approximately(angle, 999f))
            {
                angle = previousAngle;
            }
            var turn = Mathf.Repeat(180f - (angle - previousAngle), 360f);
            if (turn < 0.001f) turn = 360f;
            _floorTimes[floor] = _floorTimes[floor - 1] + turn / 180f * 60f / Math.Max(1f, bpm);
            var radians = angle * Mathf.Deg2Rad;
            _floorPositions[floor] = _floorPositions[floor - 1] + new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
            previousAngle = angle;
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

        if (!_textures.TryGetValue(imagePath, out var texture))
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(imagePath), markNonReadable: true))
            {
                Destroy(texture);
                return;
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            _textures[imagePath] = texture;
        }

        var gameObject = new GameObject(imageName);
        var renderer = gameObject.AddComponent<SpriteRenderer>();
        renderer.sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorPositions.Length - 1);
        var localPosition = ReadVector2(data["position"], Vector2.zero);
        var relativeTo = data.Value<string>("relativeTo") ?? "Tile";
        var basePosition = relativeTo is "Tile" or "LastPosition" ? _floorPositions[floor] : Vector2.zero;
        gameObject.transform.position = basePosition + localPosition;
        gameObject.transform.rotation = Quaternion.Euler(0f, 0f, -(data.Value<float?>("rotation") ?? 0f));
        var scale = ReadVector2(data["scale"], new Vector2(100f, 100f)) / 100f;
        gameObject.transform.localScale = new Vector3(scale.x, scale.y, 1f);
        var color = ParseColor(data.Value<string>("color") ?? "ffffffff");
        color.a *= (data.Value<float?>("opacity") ?? 100f) / 100f;
        renderer.color = color;
        var depth = data.Value<int?>("depth") ?? 0;
        renderer.sortingOrder = Math.Clamp(-depth * 10, short.MinValue, short.MaxValue);

        var tags = SplitTags(data.Value<string>("tag"));
        _decorations.Add(new DecorationState(gameObject.transform, renderer, tags, basePosition));
    }

    private void AddMoveEvent(JObject data, float bpm)
    {
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorTimes.Length - 1);
        var start = _floorTimes[floor] + AngleOffsetSeconds(data.Value<float?>("angleOffset") ?? 0f, bpm);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f / bpm);
        _moves.Add(new MoveEvent(start, duration, SplitTags(data.Value<string>("tag")), data));
    }

    private void AddCameraEvent(JObject data, float bpm)
    {
        var floor = Math.Clamp(data.Value<int?>("floor") ?? 0, 0, _floorTimes.Length - 1);
        var start = _floorTimes[floor] + AngleOffsetSeconds(data.Value<float?>("angleOffset") ?? 0f, bpm);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f / bpm);
        _cameraEvents.Add(new CameraEvent(start, duration, data));
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
            var move = _moves[_nextMove++];
            move.Capture(_decorations);
            _activeMoves.Add(move);
        }
        for (var index = _activeMoves.Count - 1; index >= 0; index--)
        {
            if (_activeMoves[index].Apply(time)) _activeMoves.RemoveAt(index);
        }

        while (_nextCameraEvent < _cameraEvents.Count && _cameraEvents[_nextCameraEvent].Start <= time)
        {
            var cameraEvent = _cameraEvents[_nextCameraEvent++];
            cameraEvent.Capture(_camera);
            _activeCameraEvents.Add(cameraEvent);
        }
        for (var index = _activeCameraEvents.Count - 1; index >= 0; index--)
        {
            if (_activeCameraEvents[index].Apply(_camera, time)) _activeCameraEvents.RemoveAt(index);
        }
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

    private static float AngleOffsetSeconds(float angleOffset, float bpm) => angleOffset / 180f * 60f / Math.Max(1f, bpm);

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
        new((tags ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

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
        public DecorationState(Transform transform, SpriteRenderer renderer, HashSet<string> tags, Vector2 basePosition)
        {
            Transform = transform;
            Renderer = renderer;
            Tags = tags;
            BasePosition = basePosition;
        }
        public Transform Transform { get; }
        public SpriteRenderer Renderer { get; }
        public HashSet<string> Tags { get; }
        public Vector2 BasePosition { get; }
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
                if (_tags.Count > 0 && !_tags.Overlaps(decoration.Tags)) continue;
                _targets.Add(new Snapshot(decoration, decoration.Transform.localPosition, decoration.Transform.localScale,
                    decoration.Transform.localEulerAngles.z, decoration.Renderer.color));
            }
        }
        public bool Apply(float time)
        {
            var amount = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            foreach (var target in _targets)
            {
                var position = ReadVector2(_data["positionOffset"] ?? _data["position"], new Vector2(float.NaN, float.NaN));
                if (!float.IsNaN(position.x)) target.State.Transform.localPosition = Vector2.Lerp(target.Position, position, amount);
                var scale = ReadVector2(_data["scale"], new Vector2(float.NaN, float.NaN));
                if (!float.IsNaN(scale.x)) target.State.Transform.localScale = Vector3.Lerp(target.Scale, new Vector3(scale.x / 100f, scale.y / 100f, 1f), amount);
                if (_data["rotationOffset"]?.Type is not (JTokenType.Null or JTokenType.None))
                {
                    var rotation = _data.Value<float?>("rotationOffset") ?? _data.Value<float?>("rotation") ?? target.Rotation;
                    target.State.Transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(target.Rotation, -rotation, amount));
                }
                var color = target.Color;
                if (_data.Value<string>("color") is { } colorHex) color = Color.Lerp(target.Color, ParseColor(colorHex), amount);
                if (_data.Value<float?>("opacity") is { } opacity) color.a = Mathf.Lerp(target.Color.a, opacity / 100f, amount);
                target.State.Renderer.color = color;
            }
            return amount >= 1f;
        }
        private sealed class Snapshot
        {
            public Snapshot(DecorationState state, Vector3 position, Vector3 scale, float rotation, Color color)
            {
                State = state;
                Position = position;
                Scale = scale;
                Rotation = rotation;
                Color = color;
            }
            public DecorationState State { get; }
            public Vector3 Position { get; }
            public Vector3 Scale { get; }
            public float Rotation { get; }
            public Color Color { get; }
        }
    }

    private sealed class CameraEvent
    {
        private readonly JObject _data;
        private Vector3 _position;
        private float _rotation;
        private float _size;
        public CameraEvent(float start, float duration, JObject data) { Start = start; Duration = duration; _data = data; }
        public float Start { get; }
        private float Duration { get; }
        public void Capture(Camera camera) { _position = camera.transform.position; _rotation = camera.transform.eulerAngles.z; _size = camera.orthographicSize; }
        public bool Apply(Camera camera, float time)
        {
            var amount = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            var position = ReadVector2(_data["position"], new Vector2(_position.x, _position.y));
            camera.transform.position = Vector3.Lerp(_position, new Vector3(position.x, position.y, -10f), amount);
            if (_data.Value<float?>("rotation") is { } rotation) camera.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(_rotation, -rotation, amount));
            if (_data.Value<float?>("zoom") is { } zoom) camera.orthographicSize = Mathf.Lerp(_size, 5f * 100f / Math.Max(1f, zoom), amount);
            return amount >= 1f;
        }
    }
}
}
