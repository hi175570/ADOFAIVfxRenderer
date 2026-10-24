using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAIVfxTimeline;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ADOFAIVfxWorker
{

[RequireComponent(typeof(Camera))]
public sealed class OfflineEffectController : MonoBehaviour
{
    private readonly List<EffectEvent> _events = new();
    private readonly Dictionary<string, EffectState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EffectTween> _active = new(StringComparer.Ordinal);
    private Material? _material;
    private HashSet<string>? _allowedKeys;
    private int _nextEvent;
    private float _time;

    public static bool Supports(string? eventType) => eventType is
        "SetFilter" or "SetFilterAdvanced" or "Bloom" or "ScreenTile" or "ScreenScroll";

    public void Configure(IEnumerable<string>? allowedKeys)
    {
        var keys = allowedKeys?.Where(static key => !string.IsNullOrWhiteSpace(key)).ToArray() ?? Array.Empty<string>();
        _allowedKeys = keys.Length == 0 ? null : new HashSet<string>(keys, StringComparer.Ordinal);
    }

    public void AddEvent(
        JObject data,
        int repeatedFloor,
        float angleOffset,
        AdoTimelineResult timeline,
        float pitch,
        int order)
    {
        var floor = Math.Clamp(repeatedFloor, 0, timeline.FloorTimesSeconds.Length - 1);
        var start = (float)timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        var bpm = (float)timeline.BpmAtFloorAngleOffset(floor, angleOffset);
        var duration = Math.Max(0f, (data.Value<float?>("duration") ?? 0f) * 60f /
                                    (Math.Max(0.000001f, bpm) * Math.Max(0.000001f, pitch)));
        var type = data.Value<string>("eventType") ?? "";
        var key = type switch
        {
            "SetFilter" => $"basic:{data.Value<string>("filter") ?? ""}",
            "SetFilterAdvanced" => $"advanced:{data.Value<string>("plane") ?? "Foreground"}:{data.Value<string>("filter") ?? ""}:{data.Value<string>("targetType") ?? "Camera"}:{data.Value<string>("targetTag") ?? ""}",
            "Bloom" => "builtin:Bloom",
            "ScreenTile" => "builtin:ScreenTile",
            "ScreenScroll" => "builtin:ScreenScroll",
            _ => "",
        };
        if (key.Length == 0 || (_allowedKeys != null && !_allowedKeys.Contains(key))) return;
        _events.Add(new EffectEvent(start, duration, order, key, type, data));
    }

    public void Prepare()
    {
        _events.Sort(static (left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : left.Order.CompareTo(right.Order));
        var shader = Resources.Load<Shader>("ADOFAIOfflineEffects");
        if (shader == null) throw new InvalidOperationException("Missing ADOFAIOfflineEffects shader resource.");
        _material = new Material(shader) { name = "ADOFAI Offline Effect Stack" };
    }

    public void Apply(float time)
    {
        while (_nextEvent < _events.Count && _events[_nextEvent].Start <= time)
        {
            AdvanceTweens(_events[_nextEvent].Start);
            StartEvent(_events[_nextEvent++]);
        }
        AdvanceTweens(time);
        _time = time;
        UpdateMaterial();
    }

    private void StartEvent(EffectEvent effectEvent)
    {
        if (_active.Remove(effectEvent.Key, out var existing)) existing.Apply(1f);

        var state = GetState(effectEvent.Key);
        var enabled = effectEvent.Data.Value<bool?>("enabled") ?? true;
        if (!enabled)
        {
            state.Enabled = false;
            return;
        }

        if (effectEvent.Data.Value<bool?>("disableOthers") == true)
        {
            var prefix = effectEvent.Type == "SetFilter" ? "basic:" : "advanced:";
            foreach (var pair in _states.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                if (pair.Key == effectEvent.Key) continue;
                pair.Value.Enabled = false;
                _active.Remove(pair.Key);
            }
        }

        var targets = ReadTargets(effectEvent);
        var starts = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var target in targets)
            starts[target.Key] = state.Values.TryGetValue(target.Key, out var current) && state.Enabled ? current : 0f;
        state.Enabled = true;

        var tween = new EffectTween(effectEvent, state, starts, targets);
        if (effectEvent.Duration <= 0f)
        {
            tween.Apply(1f);
        }
        else
        {
            tween.Apply(0f);
            _active[effectEvent.Key] = tween;
        }
    }

    private static Dictionary<string, float> ReadTargets(EffectEvent effectEvent)
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        switch (effectEvent.Type)
        {
            case "SetFilter":
                result["Intensity"] = (effectEvent.Data.Value<float?>("intensity") ?? 0f) / 100f;
                break;
            case "SetFilterAdvanced":
                if (effectEvent.Data["filterProperties"] is JObject properties)
                {
                    foreach (var property in properties.Properties())
                    {
                        if (property.Value.Type is JTokenType.Integer or JTokenType.Float)
                            result[property.Name] = property.Value.Value<float>() / 100f;
                    }
                }
                break;
            case "Bloom":
                result["Intensity"] = (effectEvent.Data.Value<float?>("intensity") ?? 0f) / 100f;
                result["Threshold"] = (effectEvent.Data.Value<float?>("threshold") ?? 0f) / 100f;
                break;
            case "ScreenTile":
            {
                var tile = ReadVector2(effectEvent.Data["tile"], Vector2.one);
                result["X"] = tile.x;
                result["Y"] = tile.y;
                break;
            }
            case "ScreenScroll":
            {
                var scroll = ReadVector2(effectEvent.Data["scroll"], Vector2.zero);
                result["X"] = scroll.x;
                result["Y"] = scroll.y;
                break;
            }
        }
        return result;
    }

    private void AdvanceTweens(float time)
    {
        foreach (var pair in _active.ToArray())
        {
            var tween = pair.Value;
            var raw = tween.Event.Duration <= 0f
                ? 1f
                : Mathf.Clamp01((time - tween.Event.Start) / tween.Event.Duration);
            tween.Apply(EaseAmount(raw, tween.Event.Data.Value<string>("ease")));
            if (raw >= 1f) _active.Remove(pair.Key);
        }
    }

    private EffectState GetState(string key)
    {
        if (!_states.TryGetValue(key, out var state)) _states[key] = state = new EffectState();
        return state;
    }

    private float Basic(string name) => Value($"basic:{name}", "Intensity");
    private float Advanced(string name, string property)
    {
        var foreground = $"advanced:Foreground:{name}:";
        var background = $"advanced:Background:{name}:";
        return _states.Where(pair => pair.Key.StartsWith(foreground, StringComparison.Ordinal) ||
                                     pair.Key.StartsWith(background, StringComparison.Ordinal))
            .Sum(pair => pair.Value.Enabled && pair.Value.Values.TryGetValue(property, out var value) ? value : 0f);
    }

    private float Value(string key, string property)
    {
        return _states.TryGetValue(key, out var state) && state.Enabled && state.Values.TryGetValue(property, out var value)
            ? value
            : 0f;
    }

    private void UpdateMaterial()
    {
        if (_material == null) return;
        _material.SetFloat("_TimeValue", _time);
        _material.SetFloat("_Grayscale", Basic("Grayscale") + Advanced("CameraFilterPack_Color_GrayScale", "filter__Fade"));
        _material.SetFloat("_Sepia", Basic("Sepia"));
        _material.SetFloat("_Invert", Basic("Invert"));
        _material.SetFloat("_Contrast", Basic("Contrast"));
        _material.SetFloat("_Aberration", Basic("Aberration"));
        _material.SetFloat("_Waves", Basic("Waves"));
        _material.SetFloat("_Fisheye", Basic("Fisheye"));
        _material.SetFloat("_LightWater", Basic("LightWater"));
        _material.SetFloat("_Glitch", Basic("Glitch") + Advanced("CameraFilterPack_FX_Glitch2", "filter_Intensity") + Advanced("CameraFilterPack_FX_Glitch3", "filter_Intensity"));
        _material.SetFloat("_Pixelate", Math.Max(Basic("Pixelate"), Advanced("CameraFilterPack_TV_LED", "filter_Size") * 0.02f));
        _material.SetFloat("_Led", Basic("LED") + Advanced("CameraFilterPack_TV_LED", "filter_Fade"));
        _material.SetFloat("_Posterize", Basic("Posterize") + Advanced("CameraFilterPack_Colors_NewPosterize", "filter_Colors") * 0.05f);
        _material.SetFloat("_Grain", Basic("Grain") + Basic("Static") + Advanced("CameraFilterPack_Film_Grain", "filter_Intensity"));
        _material.SetFloat("_Neon", Basic("Neon"));
        _material.SetFloat("_OilPaint", Basic("OilPaint") + Advanced("CameraFilterPack_Pixelisation_OilPaint", "filter_Value"));
        _material.SetFloat("_Drawing", Basic("Drawing"));
        _material.SetFloat("_Vhs", Basic("VHS") + Basic("Compression") + Advanced("CameraFilterPack_TV_Old_Movie_2", "filter_Amount"));
        _material.SetFloat("_Rain", Basic("Rain") + Advanced("CameraFilterPack_Atmosphere_Rain_Pro", "filter_Intensity"));
        _material.SetFloat("_Sharpen", Basic("Sharpen"));
        _material.SetFloat("_MotionBlur", Basic("MotionBlur"));
        _material.SetFloat("_Earthquake", Advanced("CameraFilterPack_FX_EarthQuake", "filter_X") + Advanced("CameraFilterPack_FX_EarthQuake", "filter_Y"));
        _material.SetFloat("_RadialBlur", Advanced("CameraFilterPack_Blur_Radial_Fast", "filter_Intensity") + Advanced("CameraFilterPack_Blur_Radial", "filter_Intensity") + Advanced("CameraFilterPack_Blur_Movie", "filter_Radius") * 0.05f);
        _material.SetFloat("_BlurHole", Advanced("CameraFilterPack_Blur_BlurHole", "filter_Size"));
        _material.SetFloat("_WideHorizontal", Advanced("CameraFilterPack_TV_WideScreenHorizontal", "filter_Size"));
        _material.SetFloat("_WideVertical", Advanced("CameraFilterPack_TV_WideScreenVertical", "filter_Size"));
        _material.SetFloat("_Dark", Advanced("CameraFilterPack_Colors_DarkColor", "filter_Alpha"));
        _material.SetVector("_Yuv", new Vector4(
            Advanced("CameraFilterPack_Color_YUV", "filter__Y"),
            Advanced("CameraFilterPack_Color_YUV", "filter__U"),
            Advanced("CameraFilterPack_Color_YUV", "filter__V"), 0f));
        _material.SetFloat("_Hue", Advanced("CameraFilterPack_Colors_HUE_Rotate", "filter_Speed"));
        _material.SetFloat("_Ascii", Advanced("CameraFilterPack_FX_Ascii", "filter_Fade"));
        _material.SetFloat("_Matrix", Advanced("CameraFilterPack_FX_DigitalMatrix", "filter_Size"));
        _material.SetFloat("_Tiles", Advanced("CameraFilterPack_TV_Tiles", "filter_Size"));
        _material.SetFloat("_Tunnel", Basic("Tunnel") + Advanced("CameraFilterPack_Vision_Tunnel", "filter_Intensity"));

        var screenTile = GetState("builtin:ScreenTile");
        _material.SetVector("_ScreenTile", screenTile.Enabled
            ? new Vector4(Get(screenTile, "X", 1f), Get(screenTile, "Y", 1f), 0f, 0f)
            : Vector4.one);
        var screenScroll = GetState("builtin:ScreenScroll");
        _material.SetVector("_ScreenScroll", screenScroll.Enabled
            ? new Vector4(Get(screenScroll, "X"), Get(screenScroll, "Y"), 0f, 0f)
            : Vector4.zero);
        _material.SetFloat("_HallOfMirrors", 0f);

        var blur = Math.Max(Basic("GaussianBlur"), Math.Max(Basic("Blur"), Basic("BlurFocus")));
        blur = Math.Max(blur, Advanced("CameraFilterPack_Blur_Blurry", "filter_Amount"));
        blur = Math.Max(blur, Advanced("CameraFilterPack_Blur_Focus", "filter__Size"));
        blur = Math.Max(blur, Advanced("CameraFilterPack_Blur_Tilt_Shift", "filter_Amount"));
        _material.SetFloat("_BlurSize", Mathf.Clamp(Math.Abs(blur), 0f, 10f));

        var bloom = GetState("builtin:Bloom");
        var glow = Advanced("CameraFilterPack_Glow_Glow", "filter_Amount") +
                   Advanced("CameraFilterPack_Glow_Glow_Color", "filter_Amount") +
                   Advanced("CameraFilterPack_Blur_Bloom", "filter_Amount");
        _material.SetFloat("_BloomIntensity", Mathf.Clamp((bloom.Enabled ? Get(bloom, "Intensity") : 0f) + glow * 0.25f, 0f, 20f));
        _material.SetFloat("_BloomThreshold", bloom.Enabled ? Mathf.Clamp01(Get(bloom, "Threshold")) : 0.5f);
    }

    private static float Get(EffectState state, string key, float fallback = 0f) =>
        state.Values.TryGetValue(key, out var value) ? value : fallback;

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (_material == null)
        {
            Graphics.Blit(source, destination);
            return;
        }

        _material.SetVector("_Resolution", new Vector4(source.width, source.height, 1f / source.width, 1f / source.height));
        var processed = RenderTexture.GetTemporary(source.descriptor);
        Graphics.Blit(source, processed, _material, 0);

        var blurSize = _material.GetFloat("_BlurSize");
        if (blurSize > 0.0001f)
        {
            var scratch = RenderTexture.GetTemporary(source.descriptor);
            Graphics.Blit(processed, scratch, _material, 1);
            Graphics.Blit(scratch, processed, _material, 2);
            RenderTexture.ReleaseTemporary(scratch);
        }

        var bloomIntensity = _material.GetFloat("_BloomIntensity");
        if (bloomIntensity > 0.0001f)
        {
            var bloom = RenderTexture.GetTemporary(source.width / 2, source.height / 2, 0, source.format);
            var scratch = RenderTexture.GetTemporary(bloom.descriptor);
            Graphics.Blit(processed, bloom, _material, 3);
            Graphics.Blit(bloom, scratch, _material, 1);
            Graphics.Blit(scratch, bloom, _material, 2);
            _material.SetTexture("_BloomTex", bloom);
            Graphics.Blit(processed, destination, _material, 4);
            RenderTexture.ReleaseTemporary(scratch);
            RenderTexture.ReleaseTemporary(bloom);
        }
        else
        {
            Graphics.Blit(processed, destination);
        }
        RenderTexture.ReleaseTemporary(processed);
    }

    private static Vector2 ReadVector2(JToken? token, Vector2 fallback)
    {
        if (token is not JArray { Count: >= 2 } array) return fallback;
        return new Vector2(array[0]?.Value<float?>() ?? fallback.x, array[1]?.Value<float?>() ?? fallback.y);
    }

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
            "OutCirc" => Mathf.Sqrt(1f - (amount - 1f) * (amount - 1f)),
            "InOutCirc" => amount < 0.5f
                ? (1f - Mathf.Sqrt(1f - Mathf.Pow(2f * amount, 2f))) * 0.5f
                : (Mathf.Sqrt(1f - Mathf.Pow(-2f * amount + 2f, 2f)) + 1f) * 0.5f,
            _ => amount,
        };
    }

    private sealed class EffectState
    {
        public bool Enabled;
        public Dictionary<string, float> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed class EffectEvent
    {
        public EffectEvent(float start, float duration, int order, string key, string type, JObject data)
        {
            Start = start;
            Duration = duration;
            Order = order;
            Key = key;
            Type = type;
            Data = data;
        }

        public float Start { get; }
        public float Duration { get; }
        public int Order { get; }
        public string Key { get; }
        public string Type { get; }
        public JObject Data { get; }
    }

    private sealed class EffectTween
    {
        private readonly EffectState _state;
        private readonly IReadOnlyDictionary<string, float> _starts;
        private readonly IReadOnlyDictionary<string, float> _targets;

        public EffectTween(EffectEvent effectEvent, EffectState state, IReadOnlyDictionary<string, float> starts, IReadOnlyDictionary<string, float> targets)
        {
            Event = effectEvent;
            _state = state;
            _starts = starts;
            _targets = targets;
        }

        public EffectEvent Event { get; }

        public void Apply(float amount)
        {
            foreach (var target in _targets)
                _state.Values[target.Key] = Mathf.LerpUnclamped(_starts[target.Key], target.Value, amount);
        }
    }
}
}
