using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace ADOFAIVfxWorker
{
/// <summary>
/// Low-memory decoration texture loader used by the installed-game Video Pro path.
/// The patched game loader creates one-pixel placeholders during level construction;
/// this class swaps real images in only while their decorations need to be rendered and
/// evicts inactive images with an LRU bounded by the user-selected texture budget.
/// </summary>
public sealed class DynamicTextureStreaming : IDisposable
{
    private static readonly Dictionary<string, Placeholder> Placeholders =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> PlaceholderSprites = new();
    private static readonly Dictionary<int, string> PlaceholderTextures = new();
    private static readonly HashSet<string> StreamedDecorationKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static int ConfiguredPlaceholderMaximumSide;
    private static long PinnedResidentBytes;

    private readonly string _chartDirectory;
    private readonly long _budgetBytes;
    private readonly int _placeholderMaximumSide;
    private readonly object _manager;
    private readonly IList _decorations;
    private readonly Type _visualType;
    private readonly Type _particleType;
    private readonly MethodInfo _visualSetCustomSprite;
    private readonly MethodInfo _visualSetTextureScale;
    private readonly MethodInfo _visualUpdateShader;
    private readonly MethodInfo _visualIsMask;
    private readonly FieldInfo _visualMaskingType;
    private readonly MethodInfo _particleSetSprite;
    private readonly FieldInfo? _maskCacheRefresh;
    private readonly FieldInfo _textureScaleMultiplier;
    private readonly FieldInfo _visualRenderer;
    private readonly FieldInfo _visualMeshRenderer;
    private readonly FieldInfo _visualMeshRendererEnabled;
    private readonly PropertyInfo _particleRenderer;
    private readonly FieldInfo _particleSystem;
    private readonly Type _customSpriteType;
    private readonly ConstructorInfo _customSpriteConstructor;
    private readonly Dictionary<int, Binding> _bindings = new();
    private readonly Dictionary<string, ResidentTexture> _resident =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingKeys = new(StringComparer.OrdinalIgnoreCase);
    private int _frame;
    private long _residentBytes;
    private long _peakResidentBytes;
    private int _loadCount;
    private int _evictionCount;
    private bool _maskCacheDirty;
    private long _lastBudgetPressureBytes = -1;
    private int _lastBudgetPressureFrame = int.MinValue;

    private DynamicTextureStreaming(
        Assembly gameAssembly,
        string chartPath,
        long budgetBytes,
        int placeholderMaximumSide)
    {
        _chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath))
                          ?? throw new InvalidDataException("Chart path has no parent directory.");
        _budgetBytes = Math.Max(16L * 1024L * 1024L, budgetBytes - PinnedResidentBytes);
        _placeholderMaximumSide = placeholderMaximumSide;
        _visualType = gameAssembly.GetType("scrVisualDecoration", true)!;
        _particleType = gameAssembly.GetType("scrParticleDecoration", true)!;
        var managerType = gameAssembly.GetType("scrDecorationManager", true)!;
        _manager = managerType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
                   ?? throw new InvalidOperationException("Original decoration manager is unavailable.");
        _decorations = managerType.GetField("allDecorations", BindingFlags.Instance | BindingFlags.Public)
                           ?.GetValue(_manager) as IList
                       ?? throw new InvalidOperationException("Original decoration list is unavailable.");
        _visualSetTextureScale = _visualType.GetMethod("SetTextureScaleMultiplier", new[] { typeof(Vector2) })
                                 ?? throw new MissingMethodException("scrVisualDecoration.SetTextureScaleMultiplier(Vector2)");
        _visualUpdateShader = _visualType.GetMethod("UpdateShader", new[] { typeof(bool) })
                              ?? throw new MissingMethodException("scrVisualDecoration.UpdateShader(bool)");
        _visualIsMask = _visualType.GetMethod("isMask", BindingFlags.Instance | BindingFlags.Public)
                        ?? throw new MissingMethodException("scrVisualDecoration.isMask()");
        _visualMaskingType = _visualType.GetField("maskingType", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new MissingFieldException("scrVisualDecoration.maskingType");
        _visualRenderer = _visualType.GetField("spriteRenderer", BindingFlags.Instance | BindingFlags.Public)
                          ?? throw new MissingFieldException("scrVisualDecoration.spriteRenderer");
        _visualMeshRenderer = _visualType.GetField("meshRenderer", BindingFlags.Instance | BindingFlags.Public)
                              ?? throw new MissingFieldException("scrVisualDecoration.meshRenderer");
        _visualMeshRendererEnabled = _visualType.GetField(
                                         "meshRendererEnabled",
                                         BindingFlags.Instance | BindingFlags.NonPublic)
                                     ?? throw new MissingFieldException("scrVisualDecoration.meshRendererEnabled");
        _particleRenderer = _particleType.GetProperty("particleRenderer", BindingFlags.Instance | BindingFlags.Public)
                            ?? throw new MissingFieldException("scrParticleDecoration.particleRenderer");
        _particleSystem = _particleType.GetField("particleSystem", BindingFlags.Instance | BindingFlags.Public)
                          ?? throw new MissingFieldException("scrParticleDecoration.particleSystem");
        _textureScaleMultiplier = gameAssembly.GetType("scrDecoration", true)!
                                      .GetField("textureScaleMultiplier", BindingFlags.Instance | BindingFlags.Public)
                                  ?? throw new MissingFieldException("scrDecoration.textureScaleMultiplier");
        var textureManagerType = gameAssembly.GetType("TextureManager", true)!;
        _customSpriteType = textureManagerType.GetNestedType("CustomSprite", BindingFlags.Public)
                            ?? throw new MissingMemberException("TextureManager.CustomSprite");
        _customSpriteConstructor = _customSpriteType.GetConstructors()
            .Single(constructor => constructor.GetParameters().Length == 6);
        _visualSetCustomSprite = _visualType.GetMethod("SetSprite", new[] { _customSpriteType, typeof(bool) })
                                 ?? throw new MissingMethodException("scrVisualDecoration.SetSprite(CustomSprite, bool)");
        _particleSetSprite = _particleType.GetMethod("SetSprite", new[] { _customSpriteType })
                             ?? throw new MissingMethodException("scrParticleDecoration.SetSprite(CustomSprite)");
        _maskCacheRefresh = gameAssembly.GetType("SpriteAlphaMaskUtils")?.GetField(
            "doRefreshMaskCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Debug.Log(
            $"TEXTURE_STREAM_INIT decorations={_decorations.Count} placeholders={Placeholders.Count} " +
            $"streamBudget={_budgetBytes} pinnedBytes={PinnedResidentBytes} totalBudget={budgetBytes} " +
            $"placeholderMaximumSide={_placeholderMaximumSide}");
    }

    /// <summary>
    /// Called from the low-memory patched Assembly-CSharp while the original scene is
    /// constructing decorations. It deliberately performs no source image decode.
    /// </summary>
    public static object? GetOrAddPlaceholder(object manager, string key, string filePath)
    {
        if (manager == null || string.IsNullOrWhiteSpace(key)) return null;
        var managerType = manager.GetType();
        var dictionary = managerType.GetField("customSprites", BindingFlags.Instance | BindingFlags.Public)
                             ?.GetValue(manager) as IDictionary
                         ?? throw new InvalidOperationException("TextureManager.customSprites is unavailable.");
        if (dictionary.Contains(key)) return dictionary[key];
        if (!StreamedDecorationKeys.Contains(key))
            return GetOrAddRealSprite(managerType, dictionary, key, filePath);

        var sourceDimensions = ReadImageDimensions(filePath);
        var placeholderDimensions = LimitPlaceholderDimensions(
            sourceDimensions,
            ConfiguredPlaceholderMaximumSide);
        var texture = new Texture2D(
            placeholderDimensions.x,
            placeholderDimensions.y,
            TextureFormat.Alpha8,
            false,
            false)
        {
            name = $"ADOFAI stream placeholder: {Path.GetFileName(key)}",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        var customSpriteType = managerType.GetNestedType("CustomSprite", BindingFlags.Public)
                               ?? throw new MissingMemberException("TextureManager.CustomSprite");
        var constructor = customSpriteType.GetConstructors()
            .Single(item => item.GetParameters().Length == 6);
        var parameters = constructor.GetParameters();
        var spriteMeshType = Enum.ToObject(parameters[5].ParameterType, 0);
        var modified = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
        var customSprite = constructor.Invoke(new object[]
        {
            texture, modified, false, false, 100f, spriteMeshType,
        });
        dictionary.Add(key, customSprite);
        var sprite = customSpriteType.GetField("sprite", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(customSprite) as Sprite;
        var placeholder = new Placeholder(key, customSprite, texture, sprite);
        Placeholders[key] = placeholder;
        PlaceholderTextures[texture.GetInstanceID()] = key;
        if (sprite != null) PlaceholderSprites[sprite.GetInstanceID()] = key;
        return customSprite;
    }

    public static void Configure(string chartPath, int placeholderMaximumSide, long budgetBytes)
    {
        ConfiguredPlaceholderMaximumSide = placeholderMaximumSide;
        PinnedResidentBytes = 0;
        StreamedDecorationKeys.Clear();
        if (budgetBytes <= 0)
        {
            Debug.Log($"TEXTURE_STREAM_CONFIG disabled chart={chartPath}");
            return;
        }
        var root = JObject.Parse(NormalizeLegacyJson(File.ReadAllText(chartPath)));
        var decorations = root["decorations"] as JArray;
        var actions = root["actions"] as JArray;
        CollectDecorationKeys(decorations);
        CollectDecorationKeys(actions);
        var initializationSensitiveKeys = CollectInitialMaskKeys(decorations);
        StreamedDecorationKeys.ExceptWith(initializationSensitiveKeys);
        var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath)) ?? "";
        foreach (var key in initializationSensitiveKeys)
        {
            var path = Path.IsPathRooted(key) ? key : Path.Combine(chartDirectory, key);
            var dimensions = ReadImageDimensions(path);
            PinnedResidentBytes += (long)Math.Max(1, dimensions.x) * Math.Max(1, dimensions.y) * 4L;
        }
        Debug.Log(
            $"TEXTURE_STREAM_CONFIG keys={StreamedDecorationKeys.Count} pinned={initializationSensitiveKeys.Count} " +
            $"pinnedBytes={PinnedResidentBytes} placeholderMaximumSide={placeholderMaximumSide} chart={chartPath}");
    }

    private static HashSet<string> CollectInitialMaskKeys(JArray? decorations)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (decorations == null) return keys;
        foreach (var decoration in decorations.OfType<JObject>())
        {
            if ((decoration.Value<int?>("floor") ?? 0) > 1) continue;
            var maskingType = decoration.Value<string>("maskingType") ?? "None";
            if (maskingType is "None" or "") continue;
            AddImageKey(keys, decoration.Value<string>("decorationImage"));
        }
        return keys;
    }

    private static IEnumerable<string> SplitTags(string? value) =>
        (value ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

    private static void AddImageKey(ISet<string> keys, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase)) return;
        keys.Add(key);
    }

    private static Vector2Int LimitPlaceholderDimensions(Vector2Int source, int placeholderMaximumSide)
    {
        if (source.x <= 0 || source.y <= 0) return Vector2Int.one;
        if (placeholderMaximumSide <= 0 || Math.Max(source.x, source.y) <= placeholderMaximumSide)
            return source;
        var scale = placeholderMaximumSide / (float)Math.Max(source.x, source.y);
        return new Vector2Int(
            Math.Max(1, Mathf.RoundToInt(source.x * scale)),
            Math.Max(1, Mathf.RoundToInt(source.y * scale)));
    }

    private static Vector2Int ReadImageDimensions(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Vector2Int.one;
        using var stream = File.OpenRead(path);
        var header = new byte[24];
        if (stream.Read(header, 0, header.Length) < header.Length) return Vector2Int.one;
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
        {
            return new Vector2Int(
                ReadBigEndianInt32(header, 16),
                ReadBigEndianInt32(header, 20));
        }
        if (header[0] != 0xFF || header[1] != 0xD8) return Vector2Int.one;
        stream.Position = 2;
        while (stream.Position + 9 < stream.Length)
        {
            if (stream.ReadByte() != 0xFF) continue;
            var marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte();
            if (marker is 0xD8 or 0xD9) continue;
            var lengthHigh = stream.ReadByte();
            var lengthLow = stream.ReadByte();
            if (lengthHigh < 0 || lengthLow < 0) break;
            var segmentLength = (lengthHigh << 8) | lengthLow;
            if (segmentLength < 2) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                stream.ReadByte();
                var height = (stream.ReadByte() << 8) | stream.ReadByte();
                var width = (stream.ReadByte() << 8) | stream.ReadByte();
                return new Vector2Int(width, height);
            }
            stream.Position += segmentLength - 2;
        }
        return Vector2Int.one;
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static void CollectDecorationKeys(JArray? entries)
    {
        if (entries == null) return;
        foreach (var entry in entries.OfType<JObject>())
        {
            var type = entry.Value<string>("eventType") ?? "";
            if (type is not ("AddDecoration" or "AddParticle" or "MoveDecorations")) continue;
            var key = entry.Value<string>("decorationImage") ?? "";
            if (key.Length == 0 || key.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase)) continue;
            StreamedDecorationKeys.Add(key);
        }
    }

    private static string NormalizeLegacyJson(string json)
    {
        var result = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                result.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"')
            {
                inString = true;
                result.Append(character);
                continue;
            }
            if (character == ',')
            {
                var next = index + 1;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                if (next < json.Length && json[next] == ',') continue;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static object? GetOrAddRealSprite(
        Type managerType,
        IDictionary dictionary,
        string key,
        string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        var loadTexture = managerType.GetMethod(
                              "LoadTexture",
                              BindingFlags.Static | BindingFlags.Public)
                          ?? throw new MissingMethodException("TextureManager.LoadTexture");
        var arguments = new object?[] { filePath, null, -1 };
        var texture = loadTexture.Invoke(null, arguments) as Texture2D;
        if (texture == null) return null;
        var customSpriteType = managerType.GetNestedType("CustomSprite", BindingFlags.Public)
                               ?? throw new MissingMemberException("TextureManager.CustomSprite");
        var constructor = customSpriteType.GetConstructors()
            .Single(item => item.GetParameters().Length == 6);
        var parameters = constructor.GetParameters();
        var customSprite = constructor.Invoke(new object[]
        {
            texture,
            File.GetLastWriteTimeUtc(filePath),
            false,
            false,
            100f,
            Enum.ToObject(parameters[5].ParameterType, 0),
        });
        dictionary.Add(key, customSprite);
        return customSprite;
    }

    public static DynamicTextureStreaming? Create(
        Assembly gameAssembly,
        string chartPath,
        long budgetBytes,
        int placeholderMaximumSide)
    {
        if (budgetBytes <= 0 || Placeholders.Count == 0) return null;
        return new DynamicTextureStreaming(
            gameAssembly,
            chartPath,
            budgetBytes,
            placeholderMaximumSide);
    }

    public void Update()
    {
        _frame++;
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _decorations)
        {
            if (item is not Component decoration || decoration == null) continue;
            var binding = GetBinding(decoration);
            RefreshDesiredImage(binding);
            if (!NeedsTexture(binding))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(binding.DesiredKey)) required.Add(binding.DesiredKey);
        }

        // Retire the previous scene before decoding the next scene. Keeping a grace
        // window here makes transition frames temporarily hold both full-resolution
        // sets and can exceed the user's texture budget even when either scene fits.
        EvictInactive(required, graceFrames: 0);
        foreach (var key in required) EnsureResident(key);
        EvictToBudget(required);

        foreach (var item in _decorations)
        {
            if (item is not Component decoration || decoration == null) continue;
            var binding = GetBinding(decoration);
            if (!NeedsTexture(binding) || string.IsNullOrWhiteSpace(binding.DesiredKey)) continue;
            if (!_resident.TryGetValue(binding.DesiredKey, out var resident)) continue;
            resident.LastUsedFrame = _frame;
            ApplyResident(binding, resident);
        }
        RefreshMaskDependencies();

        if (_frame == 1 || _frame % 600 == 0)
        {
            Debug.Log(
                $"TEXTURE_STREAM_STATE frame={_frame} resident={_resident.Count} bytes={_residentBytes} " +
                $"peak={_peakResidentBytes} required={required.Count} loads={_loadCount} evictions={_evictionCount}");
        }
        if (Environment.GetCommandLineArgs().Contains("--diagnose-memory") &&
            (_frame == 1 || (_frame <= 600 && _frame % 300 == 0)))
            LogVisibleBindings();
    }

    public void CaptureVisibility()
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Kind != BindingKind.Visual) continue;
            var mesh = _visualMeshRenderer.GetValue(binding.Decoration) as MeshRenderer;
            binding.WasVisibleLastRender = mesh != null && mesh.gameObject.activeInHierarchy && mesh.isVisible;
        }
    }

    private void LogVisibleBindings()
    {
        foreach (var binding in _bindings.Values
                     .Where(NeedsTexture)
                     .OrderByDescending(static binding => binding.Decoration.transform.lossyScale.sqrMagnitude)
                     .Take(200))
        {
            if (binding.Kind != BindingKind.Visual)
            {
                Debug.Log($"TEXTURE_STREAM_VISIBLE frame={_frame} kind=particle key={binding.DesiredKey} applied={binding.AppliedKey}");
                continue;
            }
            var renderer = _visualRenderer.GetValue(binding.Decoration) as SpriteRenderer;
            var mesh = _visualMeshRenderer.GetValue(binding.Decoration) as MeshRenderer;
            Debug.Log(
                $"TEXTURE_STREAM_VISIBLE frame={_frame} kind=visual name={binding.Decoration.name} " +
                $"key={binding.DesiredKey} applied={binding.AppliedKey} sprite={renderer?.sprite?.name} " +
                $"spriteEnabled={renderer?.enabled} meshEnabled={mesh?.enabled} meshVisible={mesh?.isVisible} " +
                $"meshActive={mesh?.gameObject.activeSelf} masking={_visualMaskingType.GetValue(binding.Decoration)} " +
                $"meshTexture={mesh?.sharedMaterial?.mainTexture?.name} scale={binding.Decoration.transform.lossyScale} " +
                $"position={binding.Decoration.transform.position}");
        }
    }

    private Binding GetBinding(Component decoration)
    {
        var id = decoration.GetInstanceID();
        if (_bindings.TryGetValue(id, out var binding)) return binding;
        var kind = _visualType.IsInstanceOfType(decoration)
            ? BindingKind.Visual
            : _particleType.IsInstanceOfType(decoration)
                ? BindingKind.Particle
                : BindingKind.Other;
        binding = new Binding(decoration, kind);
        _bindings[id] = binding;
        return binding;
    }

    private void RefreshDesiredImage(Binding binding)
    {
        if (binding.Kind == BindingKind.Visual)
        {
            var renderer = _visualRenderer.GetValue(binding.Decoration) as SpriteRenderer;
            var sprite = renderer?.sprite;
            if (sprite == null || !PlaceholderSprites.TryGetValue(sprite.GetInstanceID(), out var key)) return;
            if (!binding.PlaceholderAssignedByStreamer)
            {
                binding.DesiredKey = key;
                var placeholderScale = (Vector2)(_textureScaleMultiplier.GetValue(binding.Decoration) ?? Vector2.one);
                var retainedPreviousScale = binding.AppliedScale is { } appliedScale &&
                                            Approximately(placeholderScale, appliedScale);
                binding.ExplicitOriginalSize = !retainedPreviousScale &&
                                               placeholderScale.x > 0f && placeholderScale.y > 0f &&
                                               !Approximately(placeholderScale, Vector2.one)
                    ? placeholderScale
                    : null;
            }
            binding.PlaceholderAssignedByStreamer = false;
            binding.AppliedKey = null;
            return;
        }
        if (binding.Kind != BindingKind.Particle) return;
        var particleRenderer = _particleRenderer.GetValue(binding.Decoration) as ParticleSystemRenderer;
        var texture = particleRenderer?.sharedMaterial?.mainTexture;
        if (texture == null || !PlaceholderTextures.TryGetValue(texture.GetInstanceID(), out var particleKey)) return;
        binding.DesiredKey = particleKey;
        binding.AppliedKey = null;
    }

    private bool NeedsTexture(Binding binding)
    {
        if (binding.Kind == BindingKind.Other || string.IsNullOrWhiteSpace(binding.DesiredKey)) return false;
        if (!binding.Decoration.gameObject.activeInHierarchy) return false;
        if (binding.Kind == BindingKind.Particle)
        {
            var system = _particleSystem.GetValue(binding.Decoration) as ParticleSystem;
            var renderer = _particleRenderer.GetValue(binding.Decoration) as ParticleSystemRenderer;
            return renderer != null && renderer.enabled && system != null &&
                   (system.isPlaying || system.isEmitting || system.particleCount > 0);
        }
        var getVisible = _visualType.GetMethod("GetVisible", BindingFlags.Instance | BindingFlags.Public);
        return getVisible?.Invoke(binding.Decoration, null) as bool? == true;
    }

    private void EnsureResident(string key)
    {
        if (_resident.TryGetValue(key, out var existing))
        {
            existing.LastUsedFrame = _frame;
            return;
        }
        var path = Path.IsPathRooted(key)
            ? key
            : Path.GetFullPath(Path.Combine(_chartDirectory, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path))
        {
            if (_missingKeys.Add(key))
                Debug.LogWarning($"TEXTURE_STREAM_MISSING key={key} path={path}");
            return;
        }
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
        {
            name = Path.GetFileName(path),
        };
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), markNonReadable: false))
        {
            Object.Destroy(texture);
            if (_missingKeys.Add(key))
                Debug.LogWarning($"TEXTURE_STREAM_DECODE_FAILED key={key} path={path}");
            return;
        }
        var sourceSize = new Vector2Int(texture.width, texture.height);
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Bilinear;
        if (texture.isReadable) texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        var parameters = _customSpriteConstructor.GetParameters();
        var customSprite = _customSpriteConstructor.Invoke(new object[]
        {
            texture,
            File.GetLastWriteTimeUtc(path),
            false,
            false,
            100f,
            Enum.ToObject(parameters[5].ParameterType, 0),
        });
        var sprite = _customSpriteType.GetField("sprite", BindingFlags.Instance | BindingFlags.Public)
                         ?.GetValue(customSprite) as Sprite
                     ?? throw new InvalidOperationException("Dynamic CustomSprite did not create a Sprite.");
        var bytes = Math.Max((long)texture.width * texture.height * 4L, Profiler.GetRuntimeMemorySizeLong(texture));
        var resident = new ResidentTexture(key, texture, sprite, customSprite, sourceSize, bytes, _frame);
        _resident[key] = resident;
        _residentBytes += bytes;
        _peakResidentBytes = Math.Max(_peakResidentBytes, _residentBytes);
        _loadCount++;
    }

    private void EvictToBudget(ISet<string> required)
    {
        if (_residentBytes <= _budgetBytes) return;
        foreach (var candidate in _resident.Values
                     .Where(item => !required.Contains(item.Key))
                     .OrderBy(item => item.LastUsedFrame)
                     .ToArray())
        {
            Evict(candidate);
            if (_residentBytes <= _budgetBytes) return;
        }
        if (_residentBytes > _budgetBytes)
        {
            if (_residentBytes != _lastBudgetPressureBytes || _frame - _lastBudgetPressureFrame >= 600)
            {
                _lastBudgetPressureBytes = _residentBytes;
                _lastBudgetPressureFrame = _frame;
                Debug.LogWarning(
                    $"TEXTURE_STREAM_BUDGET_PRESSURE bytes={_residentBytes} budget={_budgetBytes} " +
                    $"required={required.Count}; all resident textures are required by this frame.");
            }
        }
    }

    private void EvictInactive(ISet<string> required, int graceFrames)
    {
        foreach (var candidate in _resident.Values
                     .Where(item => !required.Contains(item.Key) && _frame - item.LastUsedFrame > graceFrames)
                     .OrderBy(item => item.LastUsedFrame)
                     .ToArray())
            Evict(candidate);
    }

    private void Evict(ResidentTexture resident)
    {
        if (!_resident.Remove(resident.Key)) return;
        foreach (var binding in _bindings.Values)
        {
            if (!string.Equals(binding.AppliedKey, resident.Key, StringComparison.OrdinalIgnoreCase)) continue;
            ApplyPlaceholder(binding, resident.Key);
        }
        _residentBytes -= resident.Bytes;
        Object.Destroy(resident.Sprite);
        Object.Destroy(resident.Texture);
        _evictionCount++;
    }

    private void ApplyResident(Binding binding, ResidentTexture resident)
    {
        if (string.Equals(binding.AppliedKey, resident.Key, StringComparison.OrdinalIgnoreCase))
        {
            if (binding.Kind == BindingKind.Visual)
            {
                var mesh = _visualMeshRenderer.GetValue(binding.Decoration) as MeshRenderer;
                var isMask = _visualIsMask.Invoke(binding.Decoration, null) as bool? == true;
                if (binding.WasVisibleLastRender)
                {
                    // Mirror the game's intended isVisible optimization using the result of
                    // our manual offline camera render, which happens after LogicUpdate.
                    _visualUpdateShader.Invoke(binding.Decoration, new object[] { false });
                }
                else if (mesh != null && !mesh.gameObject.activeSelf && !isMask)
                {
                    // GetVisible() has already declared this decoration live for the
                    // current chart time. A transparent placeholder may nevertheless
                    // have left both the GameObject and the private enable flag false.
                    // Rebuild from the resident sprite; true mask sources are the only
                    // visuals that must remain non-rendering by design.
                    _visualMeshRendererEnabled.SetValue(binding.Decoration, false);
                    _visualUpdateShader.Invoke(binding.Decoration, new object[] { false });
                }
            }
            return;
        }
        if (binding.Kind == BindingKind.Visual)
        {
            _visualSetCustomSprite.Invoke(binding.Decoration, new[] { resident.CustomSprite, false });
            if (_visualIsMask.Invoke(binding.Decoration, null) as bool? == true)
                _maskCacheDirty = true;
            if (binding.ExplicitOriginalSize is { } originalSize)
                _visualSetTextureScale.Invoke(binding.Decoration, new object[] { originalSize });
            else
                _textureScaleMultiplier.SetValue(
                    binding.Decoration,
                    new Vector2(
                        resident.SourceSize.x / (float)Math.Max(1, resident.Texture.width),
                        resident.SourceSize.y / (float)Math.Max(1, resident.Texture.height)));
            // The placeholder may already have caused UpdateShader to create a 1x1
            // filtered RenderTexture. The game's dimension-mismatch branch releases that
            // texture but keeps the non-null reference, so EnableShader will not allocate
            // the correctly-sized replacement. Clear it before the one forced rebuild.
            var meshRenderer = _visualMeshRenderer.GetValue(binding.Decoration) as MeshRenderer;
            var staleTarget = meshRenderer?.material.mainTexture as RenderTexture;
            if (staleTarget != null &&
                (staleTarget.width != resident.Texture.width || staleTarget.height != resident.Texture.height))
            {
                meshRenderer!.material.mainTexture = null;
                staleTarget.Release();
                Object.Destroy(staleTarget);
            }
            // UpdateShader normally skips an already-enabled mesh when Unity reports it
            // was not visible in the previous frame. A one-pixel transparent placeholder
            // is necessarily reported as invisible, so force the original decoration to
            // rebuild its filtered mesh from the newly streamed texture exactly once.
            _visualMeshRendererEnabled.SetValue(binding.Decoration, false);
            _visualUpdateShader.Invoke(binding.Decoration, new object[] { false });
        }
        else if (binding.Kind == BindingKind.Particle)
        {
            _particleSetSprite.Invoke(binding.Decoration, new[] { resident.CustomSprite });
        }
        binding.AppliedKey = resident.Key;
        binding.AppliedScale = (Vector2)(_textureScaleMultiplier.GetValue(binding.Decoration) ?? Vector2.one);
    }

    private void RefreshMaskDependencies()
    {
        if (!_maskCacheDirty) return;
        _maskCacheRefresh?.SetValue(null, true);
        foreach (var binding in _bindings.Values)
        {
            if (binding.Kind != BindingKind.Visual || !NeedsTexture(binding)) continue;
            var maskingType = _visualMaskingType.GetValue(binding.Decoration)?.ToString() ?? "None";
            if (maskingType is not ("VisibleInsideMask" or "VisibleOutsideMask")) continue;
            _visualMeshRendererEnabled.SetValue(binding.Decoration, false);
            _visualUpdateShader.Invoke(binding.Decoration, new object[] { false });
        }
        _maskCacheDirty = false;
    }

    private void ApplyPlaceholder(Binding binding, string key)
    {
        if (!Placeholders.TryGetValue(key, out var placeholder)) return;
        if (binding.Kind == BindingKind.Visual)
        {
            _visualSetCustomSprite.Invoke(binding.Decoration, new[] { placeholder.CustomSprite, false });
            binding.PlaceholderAssignedByStreamer = true;
        }
        else if (binding.Kind == BindingKind.Particle)
        {
            _particleSetSprite.Invoke(binding.Decoration, new[] { placeholder.CustomSprite });
        }
        binding.AppliedKey = null;
    }

    private static bool Approximately(Vector2 left, Vector2 right) =>
        Mathf.Approximately(left.x, right.x) && Mathf.Approximately(left.y, right.y);

    public void Dispose()
    {
        foreach (var resident in _resident.Values.ToArray()) Evict(resident);
        Debug.Log(
            $"TEXTURE_STREAM_DONE peak={_peakResidentBytes} loads={_loadCount} evictions={_evictionCount}");
    }

    private enum BindingKind
    {
        Other,
        Visual,
        Particle,
    }

    private sealed class Binding
    {
        public Binding(Component decoration, BindingKind kind)
        {
            Decoration = decoration;
            Kind = kind;
        }

        public Component Decoration { get; }
        public BindingKind Kind { get; }
        public string DesiredKey { get; set; } = "";
        public string? AppliedKey { get; set; }
        public Vector2? ExplicitOriginalSize { get; set; }
        public Vector2? AppliedScale { get; set; }
        public bool PlaceholderAssignedByStreamer { get; set; }
        public bool WasVisibleLastRender { get; set; }
    }

    private sealed class ResidentTexture
    {
        public ResidentTexture(
            string key,
            Texture2D texture,
            Sprite sprite,
            object customSprite,
            Vector2Int sourceSize,
            long bytes,
            int lastUsedFrame)
        {
            Key = key;
            Texture = texture;
            Sprite = sprite;
            CustomSprite = customSprite;
            SourceSize = sourceSize;
            Bytes = bytes;
            LastUsedFrame = lastUsedFrame;
        }

        public string Key { get; }
        public Texture2D Texture { get; }
        public Sprite Sprite { get; }
        public object CustomSprite { get; }
        public Vector2Int SourceSize { get; }
        public long Bytes { get; }
        public int LastUsedFrame { get; set; }
    }

    private sealed class Placeholder
    {
        public Placeholder(string key, object customSprite, Texture2D texture, Sprite? sprite)
        {
            Key = key;
            CustomSprite = customSprite;
            Texture = texture;
            Sprite = sprite;
        }

        public string Key { get; }
        public object CustomSprite { get; }
        public Texture2D Texture { get; }
        public Sprite? Sprite { get; }
    }
}
}
