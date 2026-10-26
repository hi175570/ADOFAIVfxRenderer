using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ADOFAIVfxWorker
{
internal sealed class GameFilterBridge
{
    private static readonly Dictionary<string, string> BasicTypes = new(StringComparer.Ordinal)
    {
        ["Grayscale"] = "CameraFilterPackLegacy_Color_GrayScale",
        ["Sepia"] = "CameraFilterPackLegacy_Color_Sepia",
        ["Invert"] = "CameraFilterPackLegacy_Color_Invert",
        ["VHS"] = "CameraFilterPackLegacy_Real_VHS",
        ["EightiesTV"] = "CameraFilterPackLegacy_TV_80",
        ["FiftiesTV"] = "CameraFilterPackLegacy_TV_50",
        ["Arcade"] = "CameraFilterPackLegacy_TV_ARCADE",
        ["LED"] = "CameraFilterPackLegacy_TV_LED",
        ["Rain"] = "CameraFilterPackLegacy_Atmosphere_Rain",
        ["Blizzard"] = "CameraFilterPackLegacy_Blizzard",
        ["PixelSnow"] = "CameraFilterPackLegacy_Atmosphere_Snow_8bits",
        ["Compression"] = "CameraFilterPackLegacy_TV_CompressionFX",
        ["Glitch"] = "CameraFilterPackLegacy_FX_Glitch1",
        ["Pixelate"] = "CameraFilterPackLegacy_Pixel_Pixelisation",
        ["Waves"] = "CameraFilterPackLegacy_Distortion_Wave_Horizontal",
        ["Static"] = "CameraFilterPackLegacy_Noise_TV",
        ["Grain"] = "CameraFilterPackLegacy_Film_Grain",
        ["MotionBlur"] = "UnityStandardAssets.ImageEffects.CameraMotionBlur",
        ["Blur"] = "CameraFilterPackLegacy_Blur_Blurry",
        ["BlurFocus"] = "CameraFilterPackLegacy_Blur_Focus",
        ["GaussianBlur"] = "CameraFilterPackLegacy_Blur_GaussianBlur",
        ["Fisheye"] = "CameraFilterPackLegacy_Distortion_FishEye",
        ["Aberration"] = "CameraFilterPackLegacy_Color_Chromatic_Aberration",
        ["Drawing"] = "CameraFilterPackLegacy_Drawing_Paper",
        ["Neon"] = "CameraFilterPackLegacy_Edge_Neon",
        ["HexagonBlack"] = "CameraFilterPackLegacy_FX_Hexagon_Black",
        ["Posterize"] = "CameraFilterPackLegacy_TV_Posterize",
        ["Sharpen"] = "CameraFilterPackLegacy_Sharpen_Sharpen",
        ["Contrast"] = "CameraFilterPackLegacy_Color_Contrast",
        ["EdgeBlackLine"] = "CameraFilterPackLegacy_Edge_BlackLine",
        ["OilPaint"] = "CameraFilterPackLegacy_Pixelisation_OilPaint",
        ["SuperDot"] = "CameraFilterPackLegacy_FX_superDot",
        ["WaterDrop"] = "CameraFilterPackLegacy_AAA_WaterDrop",
        ["LightWater"] = "CameraFilterPackLegacy_Light_Water2",
        ["Handheld"] = "CameraFilterPackLegacy_FX_8bits_gb",
        ["NightVision"] = "CameraFilterPackLegacy_Oculus_NightVision1",
        ["Funk"] = "CameraFilterPackLegacy_FX_Funk",
        ["Weird3D"] = "CameraFilterPackLegacy_TV_Video3D",
    };

    // Serialized component order from the installed game's level4/Camera
    // GameObject. Unity invokes OnRenderImage in this order. Advanced filters
    // are added later by ffxSetFilterAdvancedPlus and therefore follow every
    // built-in camera filter.
    private static readonly Dictionary<string, int> BasicRenderOrder = new(StringComparer.Ordinal)
    {
        ["VHS"] = 4, ["Funk"] = 5, ["LED"] = 6, ["Static"] = 7,
        ["Fisheye"] = 8, ["FiftiesTV"] = 9, ["EightiesTV"] = 10,
        ["Drawing"] = 12, ["Arcade"] = 13, ["Compression"] = 14,
        ["Glitch"] = 15, ["Waves"] = 16, ["NightVision"] = 17,
        ["Pixelate"] = 18, ["Sepia"] = 19, ["Grayscale"] = 20,
        ["Rain"] = 21, ["Invert"] = 22, ["Weird3D"] = 23,
        ["Blizzard"] = 24, ["Neon"] = 25, ["Handheld"] = 26,
        ["PixelSnow"] = 27, ["MotionBlur"] = 28, ["Grain"] = 29,
        ["Aberration"] = 31, ["HexagonBlack"] = 36, ["Posterize"] = 37,
        ["Blur"] = 38, ["BlurFocus"] = 39, ["Sharpen"] = 40,
        ["Contrast"] = 41, ["EdgeBlackLine"] = 43, ["OilPaint"] = 44,
        ["WaterDrop"] = 45, ["LightWater"] = 46, ["SuperDot"] = 47,
        ["GaussianBlur"] = 48,
    };

    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly List<Binding> _order = new();
    private readonly GameObject _host;
    private readonly GameObject _serviceHost;
    private readonly GameObject? _pipelineHost;
    private readonly Dictionary<string, InstalledGameFilterPass> _nativePasses = new(StringComparer.Ordinal);
    private readonly Assembly? _firstPass;
    private readonly Assembly? _gameAssembly;
    private Component? _basicFilterInvoker;
    private Component? _basicVfxState;
    private MethodInfo? _basicSetFilter;
    private Type? _basicFilterEnum;
    private Type? _editorUtilsType;
    private bool _reportedMissingBasicInvoker;
    private bool _reportedMissingUnitConverter;
    private readonly bool _useNativeCameraPipeline;

    public GameFilterBridge(string managedDirectory, GameObject? cameraHost = null)
    {
        _useNativeCameraPipeline = cameraHost != null;
        _pipelineHost = cameraHost;
        var cameraPrefab = Resources.Load<GameObject>("adofai_camera");
        if (cameraPrefab != null)
        {
            var prefabWasActive = cameraPrefab.activeSelf;
            cameraPrefab.SetActive(false);
            _host = UnityEngine.Object.Instantiate(cameraPrefab);
            cameraPrefab.SetActive(prefabWasActive);
            _host.name = "ADOFAI Installed Camera Filter Components";
        }
        else
        {
            _host = new GameObject("ADOFAI Game Filter Components");
            _host.SetActive(false);
        }
        UnityEngine.Object.DontDestroyOnLoad(_host);
        _serviceHost = new GameObject("ADOFAI Installed Filter Event Services");
        _serviceHost.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_serviceHost);
        if (!Directory.Exists(managedDirectory)) return;
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => ResolveAssembly(managedDirectory, args.Name);
        _firstPass = LoadAssembly(Path.Combine(managedDirectory, "Assembly-CSharp-firstpass.dll"));
        _gameAssembly = LoadAssembly(Path.Combine(managedDirectory, "Assembly-CSharp.dll"));
        InitializeGameContracts();
        if (_useNativeCameraPipeline) CreateNativeBasicPasses();
    }

    public bool IsAvailable => _firstPass != null;

    public void Register(string key, string eventType, JObject data)
    {
        if (!IsAvailable || _bindings.ContainsKey(key)) return;
        string? className = null;
        if (eventType == "SetFilter")
        {
            BasicTypes.TryGetValue(data.Value<string>("filter") ?? "", out className);
        }
        else if (eventType == "SetFilterAdvanced")
        {
            className = data.Value<string>("filter");
        }
        else if (eventType == "Bloom")
        {
            className = "VideoBloom";
        }
        if (string.IsNullOrWhiteSpace(className)) return;
        var type = _firstPass!.GetType(className) ?? _gameAssembly?.GetType(className);
        if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type))
        {
            Debug.LogWarning($"Installed game filter type is unavailable: {className}");
            return;
        }
        var filterName = data.Value<string>("filter") ?? "";
        var templateComponent = _host.GetComponent(type) as MonoBehaviour;
        var cameFromInstalledPrefab = templateComponent != null;
        templateComponent ??= (MonoBehaviour)_host.AddComponent(type);
        templateComponent.enabled = false;
        var serializedOrder = Array.IndexOf(_host.GetComponents<Component>(), templateComponent);
        var renderOrder = cameFromInstalledPrefab && serializedOrder >= 0
            ? serializedOrder
            : 1000 + _order.Count;
        var component = templateComponent;
        if (_useNativeCameraPipeline && _pipelineHost != null)
        {
            component = (MonoBehaviour)_pipelineHost.AddComponent(type);
            component.enabled = false;
            CopyInstanceFields(templateComponent, component);
        }
        var binding = new Binding(key, eventType, filterName, component, renderOrder);
        binding.Shader = FindAssignedShader(templateComponent) ?? LoadInstalledShader(className);
        if (!AttachInstalledShader(binding))
        {
            Debug.LogWarning($"Installed game shader is unavailable: {className}");
            return;
        }
        AttachInstalledTextures(binding);
        if (_useNativeCameraPipeline) PrimeNativeMaterials(binding);
        if (eventType == "SetFilter" && !RegisterBasicFilter(binding))
        {
            Debug.LogError($"Installed ffxSetFilterPlus could not bind {filterName}; the filter will not be approximated.");
            UnityEngine.Object.Destroy(component);
            return;
        }
        if (className == "VideoBloom")
            type.GetMethod("CheckResources", BindingFlags.Instance | BindingFlags.Public)?.Invoke(component, null);
        if (_useNativeCameraPipeline && _pipelineHost != null)
        {
            if (!_nativePasses.TryGetValue(key, out var pass))
            {
                pass = _pipelineHost.AddComponent<InstalledGameFilterPass>();
                pass.enabled = false;
                _nativePasses[key] = pass;
            }
            binding.NativePass = pass;
            pass.RenderAction = (source, destination) =>
                binding.OnRenderImage.Invoke(binding.Component, new object[] { source, destination });
        }
        _bindings[key] = binding;
        _order.Add(binding);
        Debug.Log($"Bound installed game filter {key} -> {binding.Shader?.name}");
    }

    public object? ConvertAdvancedValue(string key, string propertyName, JToken chartValue)
    {
        if (!_bindings.TryGetValue(key, out var binding)) return null;
        var fieldName = StripFilterPrefix(propertyName);
        var field = binding.Component.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
        if (field == null) return null;
        if (field.FieldType == typeof(float) || field.FieldType == typeof(int))
        {
            if (chartValue.Type is not (JTokenType.Integer or JTokenType.Float)) return null;
            var multiplier = GetGameUnitMultiplier(field.FieldType, field.Name);
            if (multiplier == null) return null;
            var converted = chartValue.Value<float>() / multiplier.Value;
            return field.FieldType == typeof(int) ? Convert.ToInt32(converted) : converted;
        }
        if (field.FieldType == typeof(Color))
        {
            var text = chartValue.Value<string>();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var normalized = text.Trim().TrimStart('#');
            if (normalized.Length == 6) normalized += "ff";
            return ColorUtility.TryParseHtmlString("#" + normalized, out var color) ? color : null;
        }
        if (field.FieldType == typeof(Vector2) && chartValue is JArray { Count: >= 2 } vector)
            return new Vector2(vector[0]!.Value<float>(), vector[1]!.Value<float>());
        return null;
    }

    public void Synchronize(IEnumerable<GameFilterStateView> states, float time)
    {
        foreach (var view in states)
        {
            if (!_bindings.TryGetValue(view.Key, out var binding)) continue;
            binding.Enabled = view.Enabled;
            if (!view.Enabled)
            {
                binding.Component.enabled = false;
                if (binding.NativePass != null) binding.NativePass.enabled = false;
                continue;
            }
            AttachInstalledShader(binding);
            if (binding.EventType == "SetFilter")
            {
                var intensity = view.GetFloat("Intensity");
                if (!ApplyBasic(binding, intensity)) binding.Enabled = false;
            }
            else if (binding.EventType == "Bloom")
            {
                SetNumericField(binding.Component, "MasterAmount", view.GetFloat("Intensity"));
                SetNumericField(binding.Component, "Threshold", view.GetFloat("Threshold"));
                if (view.Values.TryGetValue("Color", out var color))
                    SetTypedField(binding.Component, "Tint", color);
                binding.Component.enabled = true;
            }
            else
            {
                if (!binding.Initialized)
                {
                    InitializeAdvancedFields(binding.Component);
                    binding.Initialized = true;
                }
                foreach (var pair in view.Values)
                    SetTypedField(binding.Component, StripFilterPrefix(pair.Key), pair.Value);
                binding.Component.enabled = true;
            }
            if (_useNativeCameraPipeline) binding.Component.enabled = false;
            // The batchmode worker composes camera planes explicitly because
            // Camera.Render discards loaded color even with Depth/Nothing clear
            // flags. Keep native passes disabled and invoke the same installed
            // OnRenderImage methods from Render instead.
            if (binding.NativePass != null) binding.NativePass.enabled = false;
        }
    }

    public void SetBloomColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color) || !_bindings.TryGetValue("builtin:Bloom", out var binding)) return;
        var normalized = color.Trim().TrimStart('#');
        if (normalized.Length == 6) normalized += "ff";
        if (!ColorUtility.TryParseHtmlString("#" + normalized, out var parsed)) return;
        binding.Component.GetType().GetField("Tint", BindingFlags.Instance | BindingFlags.Public)?.SetValue(binding.Component, parsed);
    }

    public void Render(RenderTexture source, RenderTexture destination)
    {
        var active = _order.Where(static item => item.Enabled)
            .OrderBy(static item => item.RenderOrder)
            .ToArray();
        if (active.Length == 0)
        {
            Graphics.Blit(source, destination);
            return;
        }

        RenderTexture current = source;
        RenderTexture? previousTemporary = null;
        for (var index = 0; index < active.Length; index++)
        {
            var binding = active[index];
            var next = index == active.Length - 1
                ? destination
                : RenderTexture.GetTemporary(source.descriptor);
            binding.OnRenderImage.Invoke(binding.Component, new object[] { current, next });
            if (previousTemporary != null) RenderTexture.ReleaseTemporary(previousTemporary);
            previousTemporary = next == destination ? null : next;
            current = next;
        }
        if (previousTemporary != null) RenderTexture.ReleaseTemporary(previousTemporary);
    }

    private bool AttachInstalledShader(Binding binding)
    {
        var shaderField = binding.Component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(static field => field.FieldType == typeof(Shader));
        if (shaderField == null) return false;
        if (binding.Shader == null || !binding.Shader.isSupported) return false;
        shaderField.SetValue(binding.Component, binding.Shader);
        return true;
    }

    private static Shader? FindAssignedShader(Component component)
    {
        foreach (var field in component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(Shader) && field.GetValue(component) is Shader shader && shader != null)
                return shader;
        }
        return null;
    }

    internal static Shader? LoadInstalledShader(string className)
    {
        if (className == "VideoBloom")
            return Resources.Load<Shader>("adofai_videobloom");
        var coreResource = className switch
        {
            "PolarScreen" => "adofai_polarscreen",
            "ScreenTile" => "adofai_screentile",
            "ScreenScroll" => "adofai_screenscroll",
            _ => null,
        };
        if (coreResource != null) return Resources.Load<Shader>(coreResource);
        if (!className.StartsWith("CameraFilterPackLegacy_", StringComparison.Ordinal)) return null;
        var resourceName = className.Replace("CameraFilterPackLegacy_", "CameraFilterPack_", StringComparison.Ordinal)
            .ToLowerInvariant();
        var shaders = Resources.LoadAll<Shader>(resourceName);
        var wantsLegacy = className.StartsWith("CameraFilterPackLegacy_", StringComparison.Ordinal);
        return shaders.FirstOrDefault(item => item.name.Contains("Legacy/", StringComparison.Ordinal) == wantsLegacy)
               ?? shaders.FirstOrDefault();
    }

    private static void AttachInstalledTextures(Binding binding)
    {
        if (!binding.Component.GetType().Name.EndsWith("Blizzard", StringComparison.Ordinal)) return;
        var texture = Resources.Load<Texture2D>("camerafilterpack_blizzard1");
        if (texture == null) return;
        foreach (var field in binding.Component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (field.FieldType == typeof(Texture2D)) field.SetValue(binding.Component, texture);
    }

    private static void CopyInstanceFields(object source, object destination)
    {
        for (var current = source.GetType(); current != null && current != typeof(MonoBehaviour); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsInitOnly || field.IsLiteral) continue;
                try { field.SetValue(destination, field.GetValue(source)); }
                catch { /* Unity-owned transient fields are initialized by the native component itself. */ }
            }
        }
    }

    private static void PrimeNativeMaterials(Binding binding)
    {
        if (binding.Shader == null) return;
        for (var current = binding.Component.GetType(); current != null && current != typeof(MonoBehaviour); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsInitOnly || field.FieldType != typeof(Material)) continue;
                if (field.GetValue(binding.Component) != null) continue;
                try
                {
                    field.SetValue(binding.Component, new Material(binding.Shader)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                    });
                }
                catch { /* Some image effects allocate their materials lazily. */ }
            }
        }
    }

    private void InitializeGameContracts()
    {
        if (_gameAssembly == null) return;
        try
        {
            var vfxType = _gameAssembly.GetType("scrVfxPlus", throwOnError: true)!;
            var eventType = _gameAssembly.GetType("ffxSetFilterPlus", throwOnError: true)!;
            _basicFilterEnum = _gameAssembly.GetType("Filter", throwOnError: true)!;
            _editorUtilsType = _gameAssembly.GetType("RDEditorUtils", throwOnError: true)!;
            _basicVfxState = _serviceHost.AddComponent(vfxType);
            _basicFilterInvoker = _serviceHost.AddComponent(eventType);
            var vfxField = FindField(eventType, "vfx")
                ?? throw new MissingFieldException(eventType.FullName, "vfx");
            vfxField.SetValue(_basicFilterInvoker, _basicVfxState);
            _basicSetFilter = eventType.GetMethod("SetFilter", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(eventType.FullName, "SetFilter");
        }
        catch (Exception exception)
        {
            Debug.LogError("Unable to bind the installed game's ffxSetFilterPlus implementation: " + exception);
            _basicVfxState = null;
            _basicFilterInvoker = null;
            _basicSetFilter = null;
            _basicFilterEnum = null;
        }
    }

    private void CreateNativeBasicPasses()
    {
        if (_pipelineHost == null) return;
        var serializedComponents = _host.GetComponents<Component>();
        var slots = new List<(string Key, int Order)>();
        foreach (var pair in BasicTypes)
        {
            var type = _firstPass?.GetType(pair.Value) ?? _gameAssembly?.GetType(pair.Value);
            if (type == null) continue;
            var component = _host.GetComponent(type);
            var order = component == null ? -1 : Array.IndexOf(serializedComponents, component);
            if (order >= 0) slots.Add(($"basic:{pair.Key}", order));
        }
        var bloomType = _gameAssembly?.GetType("VideoBloom");
        var bloom = bloomType == null ? null : _host.GetComponent(bloomType);
        var bloomOrder = bloom == null ? -1 : Array.IndexOf(serializedComponents, bloom);
        if (bloomOrder >= 0) slots.Add(("builtin:Bloom", bloomOrder));
        foreach (var slot in slots.OrderBy(static item => item.Order))
        {
            var pass = _pipelineHost.AddComponent<InstalledGameFilterPass>();
            pass.enabled = false;
            _nativePasses[slot.Key] = pass;
        }
    }

    private bool RegisterBasicFilter(Binding binding)
    {
        if (_basicVfxState == null || _basicFilterEnum == null) return false;
        try
        {
            var filter = Enum.Parse(_basicFilterEnum, binding.FilterName, ignoreCase: false);
            var dictionaryField = _basicVfxState.GetType().GetField("filterToComp", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingFieldException(_basicVfxState.GetType().FullName, "filterToComp");
            var dictionary = dictionaryField.GetValue(_basicVfxState) as IDictionary
                ?? throw new InvalidOperationException("scrVfxPlus.filterToComp is unavailable.");
            dictionary[filter] = binding.Component;
            binding.GameFilter = filter;
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"Unable to register installed basic filter {binding.FilterName}: {exception}");
            return false;
        }
    }

    private bool ApplyBasic(Binding binding, float intensity)
    {
        if (_basicFilterInvoker == null || _basicSetFilter == null || binding.GameFilter == null)
        {
            if (!_reportedMissingBasicInvoker)
            {
                _reportedMissingBasicInvoker = true;
                Debug.LogError("Installed ffxSetFilterPlus is unavailable; basic filters will not use approximate parameter formulas.");
            }
            return false;
        }
        try
        {
            _basicSetFilter.Invoke(_basicFilterInvoker, new[] { binding.GameFilter, true, intensity });
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"Installed ffxSetFilterPlus failed for {binding.FilterName}: {exception}");
            return false;
        }
    }

    private float? GetGameUnitMultiplier(Type fieldType, string fieldName)
    {
        if (_editorUtilsType == null)
        {
            if (!_reportedMissingUnitConverter)
            {
                _reportedMissingUnitConverter = true;
                Debug.LogError("Installed RDEditorUtils unit conversion is unavailable; advanced filter values will not be guessed.");
            }
            return null;
        }
        try
        {
            var typeToProperty = _editorUtilsType.GetMethod("TypeToPropertyType", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(_editorUtilsType.FullName, "TypeToPropertyType");
            var fieldToUnit = _editorUtilsType.GetMethod("FilterFieldToUnit", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(_editorUtilsType.FullName, "FilterFieldToUnit");
            var unitMultiplier = _editorUtilsType.GetMethod("UnitMultiplier", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(_editorUtilsType.FullName, "UnitMultiplier");
            var propertyType = typeToProperty.Invoke(null, new object[] { fieldType });
            var unit = fieldToUnit.Invoke(null, new[] { propertyType!, fieldName });
            return Convert.ToSingle(unitMultiplier.Invoke(null, new[] { unit! }));
        }
        catch (Exception exception)
        {
            Debug.LogError($"Installed advanced-filter unit conversion failed for {fieldName}: {exception}");
            return null;
        }
    }

    private static void InitializeAdvancedFields(Component component)
    {
        foreach (var field in component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (field.FieldType == typeof(float)) field.SetValue(component, 0f);
            else if (field.FieldType == typeof(int)) field.SetValue(component, 0);
            else if (field.FieldType == typeof(Color)) field.SetValue(component, Color.white);
            else if (field.FieldType == typeof(Vector2)) field.SetValue(component, Vector2.zero);
        }
    }

    private static void SetTypedField(Component component, string name, object value)
    {
        var field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null || value == null) return;
        if (field.FieldType.IsInstanceOfType(value)) field.SetValue(component, value);
        else if (field.FieldType == typeof(float)) field.SetValue(component, Convert.ToSingle(value));
        else if (field.FieldType == typeof(int)) field.SetValue(component, Convert.ToInt32(value));
    }

    private static void SetNumericField(Component component, string name, float value)
    {
        var field = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) return;
        if (field.FieldType == typeof(float)) field.SetValue(component, value);
        else if (field.FieldType == typeof(int)) field.SetValue(component, Mathf.RoundToInt(value));
    }

    private static string StripFilterPrefix(string name) =>
        name.StartsWith("filter_", StringComparison.Ordinal) ? name.Substring("filter_".Length) : name;

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }

    private static Assembly? LoadAssembly(string path)
    {
        try { return File.Exists(path) ? Assembly.LoadFrom(path) : null; }
        catch { return null; }
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

    private sealed class Binding
    {
        public Binding(string key, string eventType, string filterName, MonoBehaviour component, int renderOrder)
        {
            Key = key;
            EventType = eventType;
            FilterName = filterName;
            Component = component;
            RenderOrder = renderOrder;
            OnRenderImage = component.GetType().GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(component.GetType().FullName, "OnRenderImage");
        }
        public string Key { get; }
        public string EventType { get; }
        public string FilterName { get; }
        public MonoBehaviour Component { get; }
        public int RenderOrder { get; }
        public MethodInfo OnRenderImage { get; }
        public Shader? Shader { get; set; }
        public bool Enabled { get; set; }
        public bool Initialized { get; set; }
        public object? GameFilter { get; set; }
        public InstalledGameFilterPass? NativePass { get; set; }
    }
}

internal sealed class InstalledGameFilterPass : MonoBehaviour
{
    public Action<RenderTexture, RenderTexture>? RenderAction { get; set; }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (RenderAction == null) Graphics.Blit(source, destination);
        else RenderAction(source, destination);
    }
}

internal sealed class GameFilterStateView
{
    public GameFilterStateView(string key, bool enabled, IReadOnlyDictionary<string, object> values)
    {
        Key = key;
        Enabled = enabled;
        Values = values;
    }
    public string Key { get; }
    public bool Enabled { get; }
    public IReadOnlyDictionary<string, object> Values { get; }
    public float GetFloat(string key) => Values.TryGetValue(key, out var value) ? Convert.ToSingle(value) : 0f;
}
}
