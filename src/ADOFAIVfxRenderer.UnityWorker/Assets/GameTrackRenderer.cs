using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ADOFAIVfxTimeline;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ADOFAIVfxWorker
{
internal sealed class GameTrackRenderer
{
    private const float TileSize = 1.5f;
    private static object? _gameController;
    private static object? _gameConstants;
    private readonly List<TrackState> _floors = new();
    private readonly List<TrackEvent> _events = new();
    private readonly List<TrackEvent> _active = new();
    private readonly MethodInfo _updateAllRequired;
    private int _nextEvent;

    public static GameTrackRenderer? Create(
        string managedDirectory,
        IReadOnlyList<Vector2> floorPositions,
        IReadOnlyList<float> floorTimes,
        IReadOnlyList<float> floorBpms,
        float pitch,
        JObject settings,
        JArray actions)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => ResolveAssembly(managedDirectory, args.Name);
            var assembly = Assembly.LoadFrom(Path.Combine(managedDirectory, "Assembly-CSharp.dll"));
            var floorMeshType = assembly.GetType("FloorMesh", throwOnError: true)!;
            var floorMeshRendererType = assembly.GetType("FloorMeshRenderer", throwOnError: true)!;
            var scrFloorType = assembly.GetType("scrFloor", throwOnError: true)!;
            EnsureGameController(assembly);
            var floorPrefab = Resources.Load<GameObject>("adofai_floormeshlong");
            if (floorPrefab == null) return null;
            return new GameTrackRenderer(
                floorMeshType,
                floorMeshRendererType,
                scrFloorType,
                floorPrefab,
                floorPositions,
                floorTimes,
                floorBpms,
                pitch,
                settings,
                actions);
        }
        catch (Exception exception)
        {
            Debug.LogError("Unable to load the installed game's FloorMesh renderer: " + exception);
            return null;
        }
    }

    private GameTrackRenderer(
        Type floorMeshType,
        Type floorMeshRendererType,
        Type scrFloorType,
        GameObject floorPrefab,
        IReadOnlyList<Vector2> floorPositions,
        IReadOnlyList<float> floorTimes,
        IReadOnlyList<float> floorBpms,
        float pitch,
        JObject settings,
        JArray actions)
    {
        _updateAllRequired = floorMeshType.GetMethod("UpdateAllRequired", BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(floorMeshType.FullName, "UpdateAllRequired");
        var trackColor = ParseColor(settings.Value<string>("trackColor") ?? "ffffffff");
        var layouts = BuildInitialLayouts(floorPositions, settings, actions);
        var animation = BuildAnimationLayouts(floorTimes, floorBpms, pitch, settings, actions);
        for (var index = 0; index < floorPositions.Count; index++)
        {
            var prefabWasActive = floorPrefab.activeSelf;
            floorPrefab.SetActive(false);
            var floorObject = UnityEngine.Object.Instantiate(floorPrefab);
            floorPrefab.SetActive(prefabWasActive);
            floorObject.name = $"Floor {index}";
            foreach (var behaviour in floorObject.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var typeName = behaviour.GetType().Name;
                if (typeName is not ("FloorMesh" or "FloorMeshRenderer"))
                    UnityEngine.Object.DestroyImmediate(behaviour);
            }
            floorObject.layer = 9;
            floorObject.transform.position = new Vector3(layouts[index].Position.x, layouts[index].Position.y, 0f);
            floorObject.transform.rotation = Quaternion.Euler(0f, 0f, layouts[index].Rotation);
            floorObject.transform.localScale = new Vector3(layouts[index].Scale, layouts[index].Scale, 1f);
            var renderer = floorObject.GetComponent<MeshRenderer>();
            var collider = floorObject.GetComponent<PolygonCollider2D>();
            collider.enabled = false;
            var material = new Material(renderer.sharedMaterial);
            if (material.HasProperty("_Color")) material.SetColor("_Color", trackColor);
            if (material.HasProperty("_Alpha")) material.SetFloat("_Alpha", trackColor.a);
            renderer.sharedMaterial = material;
            renderer.sortingOrder = 5;

            var floorMesh = floorObject.GetComponent(floorMeshType)
                ?? throw new InvalidOperationException("Installed floorMeshLong prefab did not resolve its FloorMesh MonoScript.");
            var floorMeshRenderer = floorObject.GetComponent(floorMeshRendererType)
                ?? throw new InvalidOperationException("Installed floorMeshLong prefab did not resolve its FloorMeshRenderer MonoScript.");
            floorMeshType.GetField("polygonCollider", BindingFlags.Instance | BindingFlags.Public)?.SetValue(floorMesh, collider);
            // FloorMesh.Awake caches the prefab's MeshFilter/MeshRenderer.
            // Activate only after unrelated gameplay/editor behaviours have
            // been removed, then update the exact serialized game component.
            floorObject.SetActive(true);
            var entry = Direction(floorPositions, index, previous: true);
            var exit = Direction(floorPositions, index, previous: false);
            SetProperty(floorMesh, "_angle0", entry * Mathf.Rad2Deg);
            SetProperty(floorMesh, "_angle1", exit * Mathf.Rad2Deg);
            floorMeshType.GetMethod("UpdateMesh", BindingFlags.Instance | BindingFlags.Public)?.Invoke(floorMesh, null);
            var state = new TrackState(
                floorObject.transform,
                renderer,
                material,
                floorMesh,
                floorMeshType,
                floorMeshRenderer,
                scrFloorType,
                layouts[index].Position,
                layouts[index].Opacity,
                animation[index]);
            state.SetOpacity(layouts[index].Opacity);
            _floors.Add(state);
        }
    }

    private static void EnsureGameController(Assembly assembly)
    {
        var startupType = assembly.GetType("ADOStartup", throwOnError: true)!;
        var cacheShaderProperties = startupType.GetMethod(
            "CacheShaderPropertyIDs",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(startupType.FullName, "CacheShaderPropertyIDs");
        cacheShaderProperties.Invoke(null, null);

        var controllerType = assembly.GetType("scrController", throwOnError: true)!;
        var instanceField = controllerType.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        if (instanceField?.GetValue(null) is UnityEngine.Object existing && existing != null) return;
        var host = new GameObject("ADOFAI Installed Game Services");
        host.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(host);
        var controller = host.AddComponent(controllerType);
        instanceField?.SetValue(null, controller);
        _gameController = controller;
        var constantsType = assembly.GetType("RDConstants", throwOnError: true)!;
        var constants = constantsType.GetProperty("data", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        if (constants == null)
            throw new InvalidOperationException("Installed RDConstants resource could not be resolved by the Worker.");
        _gameConstants = constants;
    }

    private static TrackLayout[] BuildInitialLayouts(
        IReadOnlyList<Vector2> floorPositions,
        JObject settings,
        JArray actions)
    {
        var byFloor = actions.OfType<JObject>()
            .Where(static action => action.Value<string>("eventType") == "PositionTrack")
            .GroupBy(static action => action.Value<int?>("floor") ?? 0)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var result = new TrackLayout[floorPositions.Count];
        var persistentOffset = Vector2.zero;
        var persistentScale = 1f;
        var persistentOpacity = 1f;
        var persistentRotation = 0f;
        for (var floor = 0; floor < floorPositions.Count; floor++)
        {
            var offset = persistentOffset;
            var scale = persistentScale;
            var opacity = persistentOpacity;
            var rotation = persistentRotation;
            if (byFloor.TryGetValue(floor, out var floorEvents))
            {
                foreach (var action in floorEvents)
                {
                    var justThis = action.Value<bool?>("justThisTile") ?? false;
                    if (HasValue(action, "positionOffset"))
                    {
                        offset += ReadVector2(action["positionOffset"], Vector2.zero) * TileSize;
                        if (action["relativeTo"] is JArray relative)
                        {
                            var targetFloor = ResolveTile(relative, floor, floorPositions.Count);
                            if (targetFloor != floor)
                            {
                                var targetPosition = targetFloor < floor
                                    ? result[targetFloor].Position
                                    : floorPositions[targetFloor];
                                offset += targetPosition - (floorPositions[floor] + persistentOffset);
                            }
                        }
                        if (!justThis) persistentOffset = offset;
                    }
                    if (HasValue(action, "scale"))
                    {
                        scale = (action.Value<float?>("scale") ?? 100f) / 100f;
                        if (!justThis) persistentScale = scale;
                    }
                    if (HasValue(action, "opacity"))
                    {
                        opacity = (action.Value<float?>("opacity") ?? 100f) / 100f;
                        if (!justThis) persistentOpacity = opacity;
                    }
                    if (HasValue(action, "rotation"))
                    {
                        rotation = action.Value<float?>("rotation") ?? 0f;
                        if (!justThis) persistentRotation = rotation;
                    }
                }
            }
            result[floor] = new TrackLayout(floorPositions[floor] + offset, scale, opacity, rotation);
        }
        return result;
    }

    private static AnimationLayout[] BuildAnimationLayouts(
        IReadOnlyList<float> floorTimes,
        IReadOnlyList<float> floorBpms,
        float pitch,
        JObject settings,
        JArray actions)
    {
        var byFloor = actions.OfType<JObject>()
            .Where(static action => action.Value<string>("eventType") == "AnimateTrack")
            .GroupBy(static action => action.Value<int?>("floor") ?? 0)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var appear = settings.Value<string>("trackAnimation") ?? "None";
        var disappear = settings.Value<string>("trackDisappearAnimation") ?? "None";
        var beatsAhead = settings.Value<float?>("beatsAhead") ?? 3f;
        var beatsBehind = settings.Value<float?>("beatsBehind") ?? 4f;
        var result = new AnimationLayout[floorTimes.Count];
        for (var floor = 0; floor < floorTimes.Count; floor++)
        {
            if (byFloor.TryGetValue(floor, out var floorEvents))
            {
                foreach (var action in floorEvents)
                {
                    if (HasValue(action, "trackAnimation")) appear = action.Value<string>("trackAnimation") ?? appear;
                    if (HasValue(action, "beatsAhead")) beatsAhead = action.Value<float?>("beatsAhead") ?? beatsAhead;
                    if (HasValue(action, "trackDisappearAnimation")) disappear = action.Value<string>("trackDisappearAnimation") ?? disappear;
                    if (HasValue(action, "beatsBehind")) beatsBehind = action.Value<float?>("beatsBehind") ?? beatsBehind;
                }
            }
            var beatSeconds = 60f / Math.Max(0.000001f, floorBpms[floor] * pitch);
            var appearMultiplier = appear is "Drop" or "Rise" ? 2f : 1f;
            var appearStart = Math.Max(0f, floorTimes[floor] - beatsAhead * appearMultiplier * beatSeconds);
            var appearDuration = appear is "Drop" or "Rise"
                ? beatSeconds * beatsAhead
                : Math.Min(beatSeconds * 0.5f, 0.5f);
            var disappearStart = floor + 1 < floorTimes.Count
                ? floorTimes[floor + 1] + beatsBehind * beatSeconds
                : float.PositiveInfinity;
            result[floor] = new AnimationLayout(
                appear,
                appearStart,
                Math.Max(0f, appearDuration),
                disappear,
                disappearStart,
                Math.Min(beatSeconds * 0.5f, 0.5f));
        }
        return result;
    }

    private static bool HasValue(JObject data, string name) =>
        data.TryGetValue(name, out var token) && token.Type is not (JTokenType.Null or JTokenType.Undefined);

    private static Vector2 ReadVector2(JToken? token, Vector2 fallback)
    {
        if (token is not JArray array) return fallback;
        return new Vector2(
            array.Count > 0 && array[0]?.Type is not (JTokenType.Null or JTokenType.Undefined)
                ? array[0]!.Value<float>()
                : fallback.x,
            array.Count > 1 && array[1]?.Type is not (JTokenType.Null or JTokenType.Undefined)
                ? array[1]!.Value<float>()
                : fallback.y);
    }

    public static bool Supports(string? eventType) => eventType is "MoveTrack" or "RecolorTrack";

    public void AddEvent(
        JObject data,
        int repeatedFloor,
        float angleOffset,
        AdoTimelineResult timeline,
        float pitch,
        int order)
    {
        if (!Supports(data.Value<string>("eventType"))) return;
        var floor = Math.Clamp(repeatedFloor, 0, _floors.Count - 1);
        var bpm = (float)timeline.BpmAtFloorAngleOffset(floor, angleOffset);
        var start = (float)timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f /
                                    (Math.Max(0.000001f, bpm) * Math.Max(0.000001f, pitch)));
        var first = ResolveTile(data["startTile"], floor, _floors.Count);
        var last = ResolveTile(data["endTile"], floor, _floors.Count);
        if (last < first) (first, last) = (last, first);
        _events.Add(new TrackEvent(start, duration, order, first, last, data));
    }

    public void Prepare()
    {
        _events.Sort(static (left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : left.Order.CompareTo(right.Order));
    }

    public void Apply(float time)
    {
        while (_nextEvent < _events.Count && _events[_nextEvent].Start <= time)
        {
            Advance(_events[_nextEvent].Start);
            var next = _events[_nextEvent++];
            for (var index = _active.Count - 1; index >= 0; index--)
            {
                _active[index].CompleteConflicts(next, _floors);
                if (_active[index].IsComplete) _active.RemoveAt(index);
            }
            next.Capture(_floors);
            if (!next.Apply(next.Start, _floors)) _active.Add(next);
        }
        Advance(time);
        foreach (var floor in _floors) floor.ApplyAnimation(time);
        _updateAllRequired.Invoke(null, null);
    }

    private void Advance(float time)
    {
        for (var index = _active.Count - 1; index >= 0; index--)
            if (_active[index].Apply(time, _floors)) _active.RemoveAt(index);
    }

    private static int ResolveTile(JToken? token, int eventFloor, int floorCount)
    {
        if (token is not JArray { Count: >= 2 } array) return eventFloor;
        var offset = array[0]?.Value<int?>() ?? 0;
        var relative = array[1]?.Value<string>() ?? "ThisTile";
        var value = relative switch
        {
            "Start" => offset,
            "End" => floorCount - 1 + offset,
            _ => eventFloor + offset,
        };
        return Math.Clamp(value, 0, floorCount - 1);
    }

    private static float Direction(IReadOnlyList<Vector2> positions, int index, bool previous)
    {
        Vector2 delta;
        if (previous)
        {
            delta = index > 0 ? positions[index - 1] - positions[index] : positions[Math.Min(1, positions.Count - 1)] - positions[0];
        }
        else
        {
            delta = index + 1 < positions.Count ? positions[index + 1] - positions[index] : positions[index] - positions[Math.Max(0, index - 1)];
        }
        return Mathf.Atan2(delta.y, delta.x);
    }

    private static void SetProperty(object target, string name, object value)
    {
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.SetValue(target, value);
    }

    private static Color ParseColor(string value)
    {
        var normalized = value.Trim().TrimStart('#');
        if (normalized.Length == 6) normalized += "ff";
        return ColorUtility.TryParseHtmlString("#" + normalized, out var color) ? color : Color.white;
    }

    private static float EaseAmount(float amount, string? ease)
        => GameEaseBridge.Evaluate(amount, ease);

    private readonly struct TrackLayout
    {
        public TrackLayout(Vector2 position, float scale, float opacity, float rotation)
        {
            Position = position;
            Scale = scale;
            Opacity = opacity;
            Rotation = rotation;
        }
        public Vector2 Position { get; }
        public float Scale { get; }
        public float Opacity { get; }
        public float Rotation { get; }
    }

    private readonly struct AnimationLayout
    {
        public AnimationLayout(
            string appearType,
            float appearStart,
            float appearDuration,
            string disappearType,
            float disappearStart,
            float disappearDuration)
        {
            AppearType = appearType;
            AppearStart = appearStart;
            AppearDuration = appearDuration;
            DisappearType = disappearType;
            DisappearStart = disappearStart;
            DisappearDuration = disappearDuration;
        }
        public string AppearType { get; }
        public float AppearStart { get; }
        public float AppearDuration { get; }
        public string DisappearType { get; }
        public float DisappearStart { get; }
        public float DisappearDuration { get; }
    }

    private sealed class TrackState
    {
        private readonly AnimationLayout _animation;
        private float _animationOpacity = 1f;
        private readonly Component _floorMeshRenderer;
        private readonly Type _scrFloorType;

        public TrackState(
            Transform transform,
            MeshRenderer renderer,
            Material material,
            object floorMesh,
            Type floorMeshType,
            Component floorMeshRenderer,
            Type scrFloorType,
            Vector2 basePosition,
            float opacity,
            AnimationLayout animation)
        {
            Transform = transform;
            Renderer = renderer;
            Material = material;
            FloorMesh = floorMesh;
            FloorMeshType = floorMeshType;
            _floorMeshRenderer = floorMeshRenderer;
            _scrFloorType = scrFloorType;
            var floorRendererBase = floorMeshRenderer.GetType().BaseType;
            floorRendererBase?.GetField("renderer", BindingFlags.Instance | BindingFlags.Public)
                ?.SetValue(floorMeshRenderer, renderer);
            floorRendererBase?.GetField("material", BindingFlags.Instance | BindingFlags.Public)
                ?.SetValue(floorMeshRenderer, material);
            BasePosition = basePosition;
            BaseRotation = transform.eulerAngles.z;
            Opacity = opacity;
            _animation = animation;
        }
        public Transform Transform { get; }
        public MeshRenderer Renderer { get; }
        public Material Material { get; private set; }
        public object FloorMesh { get; }
        public Type FloorMeshType { get; }
        public Vector2 BasePosition { get; }
        public float BaseRotation { get; }
        public float Opacity { get; set; } = 1f;

        public void SetOpacity(float value)
        {
            Opacity = value;
            RefreshOpacity();
        }

        public void SetColor(Color value)
        {
            if (Material.HasProperty("_Color")) Material.SetColor("_Color", value);
        }

        public Color GetColor() => Material.HasProperty("_Color") ? Material.GetColor("_Color") : Color.white;

        public void SetStyle(string style)
        {
            var gameObject = Transform.gameObject;
            var wasActive = gameObject.activeSelf;
            gameObject.SetActive(false);
            Component? floor = null;
            try
            {
                floor = gameObject.AddComponent(_scrFloorType);
                _scrFloorType.GetField("floorRenderer", BindingFlags.Instance | BindingFlags.Public)
                    ?.SetValue(floor, _floorMeshRenderer);
                _scrFloorType.GetField("controller", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(floor, _gameController);
                _scrFloorType.GetField("gc", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(floor, _gameConstants);
                var styleType = _scrFloorType.Assembly.GetType("TrackStyle", throwOnError: true)!;
                var styleValue = Enum.Parse(styleType, style);
                var method = _scrFloorType.GetMethod("SetTrackStyle", BindingFlags.Instance | BindingFlags.Public)
                    ?? throw new MissingMethodException(_scrFloorType.FullName, "SetTrackStyle");
                method.Invoke(floor, new object?[] { styleValue, false, null });
                Material = Renderer.material;
                RefreshOpacity();
            }
            finally
            {
                if (floor != null) UnityEngine.Object.DestroyImmediate(floor);
                gameObject.SetActive(wasActive);
            }
        }

        public void SetMaterial(Material template)
        {
            var color = Material.HasProperty("_Color") ? Material.GetColor("_Color") : Color.white;
            var replacement = new Material(template);
            Material = replacement;
            Renderer.sharedMaterial = replacement;
            SetColor(color);
            RefreshOpacity();
        }

        public void ApplyAnimation(float time)
        {
            var appear = 1f;
            if (_animation.AppearType != "None" && time < _animation.AppearStart)
                appear = 0f;
            else if (_animation.AppearType == "Fade" && _animation.AppearDuration > 0f &&
                     time < _animation.AppearStart + _animation.AppearDuration)
                appear = Mathf.Sin(Mathf.Clamp01((time - _animation.AppearStart) / _animation.AppearDuration) * Mathf.PI * 0.5f);

            var disappear = 1f;
            if (_animation.DisappearType != "None" && time >= _animation.DisappearStart)
            {
                if (_animation.DisappearType == "Fade" && _animation.DisappearDuration > 0f)
                    disappear = 1f - Mathf.Clamp01((time - _animation.DisappearStart) / _animation.DisappearDuration);
                else
                    disappear = 0f;
            }
            _animationOpacity = Math.Min(appear, disappear);
            RefreshOpacity();
        }

        private void RefreshOpacity()
        {
            if (Material.HasProperty("_Alpha")) Material.SetFloat("_Alpha", Opacity * _animationOpacity);
        }
    }

    [Flags]
    private enum TrackProperty
    {
        None = 0,
        PositionX = 1,
        PositionY = 2,
        Rotation = 4,
        ScaleX = 8,
        ScaleY = 16,
        Opacity = 32,
        Color = 64,
    }

    private sealed class TrackEvent
    {
        private readonly int _first;
        private readonly int _last;
        private readonly int _gap;
        private readonly string _ease;
        private readonly TrackProperty _properties;
        private readonly Vector2 _positionOffset;
        private readonly float _rotationOffset;
        private readonly Vector2 _scale;
        private readonly float _opacity;
        private readonly bool _isRecolor;
        private readonly Color _color;
        private readonly Color _secondaryColor;
        private readonly string _colorType;
        private readonly string? _style;
        private readonly Dictionary<int, Snapshot> _starts = new();
        private readonly Dictionary<int, TrackProperty> _activeProperties = new();

        public TrackEvent(
            float start,
            float duration,
            int order,
            int first,
            int last,
            JObject data)
        {
            Start = start;
            Duration = duration;
            Order = order;
            _first = first;
            _last = last;
            _gap = Math.Max(0, data.Value<int?>("gapLength") ?? 0);
            _ease = data.Value<string>("ease") ?? "Linear";
            _isRecolor = data.Value<string>("eventType") == "RecolorTrack";
            if (_isRecolor)
            {
                if (HasValue(data, "trackColor"))
                {
                    _properties |= TrackProperty.Color;
                    _color = ParseColor(data.Value<string>("trackColor") ?? "ffffffff");
                }
                _secondaryColor = ParseColor(data.Value<string>("secondaryTrackColor") ?? "ffffffff");
                _colorType = data.Value<string>("trackColorType") ?? "Single";
                _style = HasValue(data, "trackStyle") ? data.Value<string>("trackStyle") : null;
                return;
            }
            if (data["positionOffset"] is JArray position)
            {
                var x = position.Count > 0 && position[0]?.Type is not (JTokenType.Null or JTokenType.Undefined);
                var y = position.Count > 1 && position[1]?.Type is not (JTokenType.Null or JTokenType.Undefined);
                if (x) _properties |= TrackProperty.PositionX;
                if (y) _properties |= TrackProperty.PositionY;
                _positionOffset = new Vector2(position[0]?.Value<float?>() ?? 0f, position[1]?.Value<float?>() ?? 0f) * TileSize;
            }
            if (data["rotationOffset"]?.Type is not (null or JTokenType.Null or JTokenType.Undefined))
            {
                _properties |= TrackProperty.Rotation;
                _rotationOffset = data.Value<float?>("rotationOffset") ?? 0f;
            }
            if (data["scale"] is JArray scale)
            {
                var x = scale.Count > 0 && scale[0]?.Type is not (JTokenType.Null or JTokenType.Undefined);
                var y = scale.Count > 1 && scale[1]?.Type is not (JTokenType.Null or JTokenType.Undefined);
                if (x) _properties |= TrackProperty.ScaleX;
                if (y) _properties |= TrackProperty.ScaleY;
                _scale = new Vector2(scale[0]?.Value<float?>() ?? 100f, scale[1]?.Value<float?>() ?? 100f) / 100f;
            }
            if (data["opacity"]?.Type is not (null or JTokenType.Null or JTokenType.Undefined))
            {
                _properties |= TrackProperty.Opacity;
                _opacity = (data.Value<float?>("opacity") ?? 100f) / 100f;
            }
        }

        public float Start { get; }
        public float Duration { get; }
        public int Order { get; }
        public bool IsComplete => _activeProperties.Count == 0 ||
                                  _activeProperties.Values.All(static value => value == TrackProperty.None);

        public void Capture(IReadOnlyList<TrackState> floors)
        {
            for (var index = _first; index <= _last; index += 1 + _gap)
            {
                var state = floors[index];
                if (_isRecolor && !string.IsNullOrWhiteSpace(_style)) state.SetStyle(_style!);
                _starts[index] = new Snapshot(
                    state.Transform.position,
                    state.Transform.eulerAngles.z,
                    state.Transform.localScale,
                    state.Opacity,
                    state.GetColor());
                _activeProperties[index] = _properties;
            }
        }

        public bool Apply(float time, IReadOnlyList<TrackState> floors)
        {
            var raw = Duration <= 0f ? 1f : Mathf.Clamp01((time - Start) / Duration);
            var amount = EaseAmount(raw, _ease);
            foreach (var pair in _starts)
            {
                var state = floors[pair.Key];
                var from = pair.Value;
                var active = _activeProperties[pair.Key];
                ApplyProperties(pair.Key, state, from, active, amount);
                if (raw >= 1f) _activeProperties[pair.Key] = TrackProperty.None;
            }
            return raw >= 1f || IsComplete;
        }

        private void ApplyProperties(int floorIndex, TrackState state, Snapshot from, TrackProperty properties, float amount)
        {
                var targetPosition = (Vector3)(state.BasePosition + _positionOffset);
                var current = state.Transform.position;
                if (properties.HasFlag(TrackProperty.PositionX)) current.x = Mathf.LerpUnclamped(from.Position.x, targetPosition.x, amount);
                if (properties.HasFlag(TrackProperty.PositionY)) current.y = Mathf.LerpUnclamped(from.Position.y, targetPosition.y, amount);
                state.Transform.position = current;
                if (properties.HasFlag(TrackProperty.Rotation))
                    state.Transform.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpUnclamped(from.Rotation, state.BaseRotation + _rotationOffset, amount));
                var currentScale = state.Transform.localScale;
                if (properties.HasFlag(TrackProperty.ScaleX)) currentScale.x = Mathf.LerpUnclamped(from.Scale.x, _scale.x, amount);
                if (properties.HasFlag(TrackProperty.ScaleY)) currentScale.y = Mathf.LerpUnclamped(from.Scale.y, _scale.y, amount);
                state.Transform.localScale = currentScale;
                if (properties.HasFlag(TrackProperty.Opacity))
                    state.SetOpacity(Mathf.LerpUnclamped(from.Opacity, _opacity, amount));
                if (properties.HasFlag(TrackProperty.Color))
                {
                    var targetColor = _colorType == "Stripes" && (floorIndex - _first) % 2 != 0
                        ? _secondaryColor
                        : _color;
                    state.SetColor(Color.LerpUnclamped(from.Color, targetColor, amount));
                }
        }

        public void CompleteConflicts(TrackEvent next, IReadOnlyList<TrackState> floors)
        {
            var overlapProperties = _properties & next._properties;
            if (overlapProperties == TrackProperty.None || _last < next._first || next._last < _first) return;
            foreach (var pair in _starts)
            {
                var floorIndex = pair.Key;
                if (floorIndex < next._first || floorIndex > next._last ||
                    (floorIndex - next._first) % (1 + next._gap) != 0) continue;
                var activeOverlap = _activeProperties[floorIndex] & overlapProperties;
                if (activeOverlap == TrackProperty.None) continue;
                ApplyProperties(floorIndex, floors[floorIndex], pair.Value, activeOverlap, 1f);
                _activeProperties[floorIndex] &= ~activeOverlap;
            }
        }

        private readonly struct Snapshot
        {
            public Snapshot(Vector3 position, float rotation, Vector3 scale, float opacity, Color color)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
                Opacity = opacity;
                Color = color;
            }
            public Vector3 Position { get; }
            public float Rotation { get; }
            public Vector3 Scale { get; }
            public float Opacity { get; }
            public Color Color { get; }
        }
    }

    private static Assembly? ResolveAssembly(string directory, string displayName)
    {
        try
        {
            var name = new AssemblyName(displayName).Name;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var path = Path.Combine(directory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        catch { return null; }
    }
}
}
