using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ADOFAIVfxRenderer.Core;

public enum GameInstallationStatus
{
    Invalid,
    CompatibleButUnverified,
    Supported,
}

public sealed record GameBuildAdapter(
    string Id,
    string DisplayName,
    string AssemblyCSharpSha256,
    string AssemblyFirstPassSha256,
    int MaximumChartVersion);

public sealed record GameCapabilities(
    bool HasLevelData,
    bool HasCameraRenderer,
    bool HasTrackRenderer,
    bool HasDecorationRenderer,
    bool HasBasicFilterRenderer,
    bool HasAdvancedFilterRenderer,
    int CameraFilterComponentCount)
{
    public bool HasRequiredRuntime =>
        HasLevelData &&
        HasCameraRenderer &&
        HasTrackRenderer &&
        HasDecorationRenderer &&
        HasBasicFilterRenderer &&
        HasAdvancedFilterRenderer &&
        CameraFilterComponentCount > 0;
}

public sealed record GameInstallation(
    GameInstallationStatus Status,
    string SelectedPath,
    string? RootDirectory,
    string? ExecutablePath,
    string? DataDirectory,
    string? ManagedDirectory,
    string? UnityVersion,
    string? AssemblyCSharpSha256,
    string? AssemblyFirstPassSha256,
    GameCapabilities Capabilities,
    GameBuildAdapter? Adapter,
    IReadOnlyList<string> Problems)
{
    public bool CanRender => Status == GameInstallationStatus.Supported && Problems.Count == 0;
}

public static partial class GameInstallationProbe
{
    private const string GameExecutableName = "A Dance of Fire and Ice.exe";
    private const string GameDirectoryName = "A Dance of Fire and Ice";

    private static readonly GameCapabilities NoCapabilities = new(
        false,
        false,
        false,
        false,
        false,
        false,
        0);

    private static readonly IReadOnlyList<GameBuildAdapter> KnownAdapters =
    [
        new(
            "unity6000-v19-0d1524da",
            "Unity 6000.3.10f1 / 谱面格式 19",
            "0D1524DABF0F8C67AA58BA1992D6ED858522B9094490EBBB00E5E8AEFF7F40CD",
            "4080ED90585059A9A2D71DD05BF18DA2F4F0DED1771C7092377EDC0A56C0E29B",
            19)
    ];

    public static GameInstallation Inspect(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Invalid(selectedPath, "请选择 A Dance of Fire and Ice 的安装目录或主程序。");
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(selectedPath.Trim().Trim('"')));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid(selectedPath, $"路径无效：{exception.Message}");
        }

        var executablePath = ResolveExecutable(normalized);
        if (executablePath is null)
        {
            return Invalid(selectedPath, $"所选位置中找不到 {GameExecutableName}。");
        }

        var root = Path.GetDirectoryName(executablePath)!;
        var dataDirectory = Path.Combine(root, "A Dance of Fire and Ice_Data");
        var managedDirectory = Path.Combine(dataDirectory, "Managed");
        var assemblyCSharp = Path.Combine(managedDirectory, "Assembly-CSharp.dll");
        var assemblyFirstPass = Path.Combine(managedDirectory, "Assembly-CSharp-firstpass.dll");
        var unityPlayer = Path.Combine(root, "UnityPlayer.dll");
        var problems = new List<string>();

        RequireDirectory(dataDirectory, "游戏数据目录", problems);
        RequireDirectory(managedDirectory, "Managed 程序集目录", problems);
        RequireFile(assemblyCSharp, "Assembly-CSharp.dll", problems);
        RequireFile(assemblyFirstPass, "Assembly-CSharp-firstpass.dll", problems);
        RequireFile(unityPlayer, "UnityPlayer.dll", problems);

        if (problems.Count > 0)
        {
            return new GameInstallation(
                GameInstallationStatus.Invalid,
                selectedPath,
                root,
                executablePath,
                dataDirectory,
                managedDirectory,
                null,
                null,
                null,
                NoCapabilities,
                null,
                problems);
        }

        try
        {
            var assemblyHash = Sha256(assemblyCSharp);
            var firstPassHash = Sha256(assemblyFirstPass);
            var mainTypes = ReadTypeNames(assemblyCSharp);
            var firstPassTypes = ReadTypeNames(assemblyFirstPass);
            var capabilities = new GameCapabilities(
                mainTypes.Contains("ADOFAI.LevelData"),
                mainTypes.Contains("ffxCameraPlus"),
                mainTypes.Contains("ffxMoveFloorPlus"),
                mainTypes.Contains("ffxMoveDecorationsPlus") && mainTypes.Contains("scrDecorationManager"),
                mainTypes.Contains("ffxSetFilterPlus") && mainTypes.Contains("scrVfxPlus"),
                mainTypes.Contains("ffxSetFilterAdvancedPlus"),
                firstPassTypes.Count(static type => type.StartsWith("CameraFilterPack", StringComparison.Ordinal)));
            var adapter = KnownAdapters.FirstOrDefault(candidate =>
                candidate.AssemblyCSharpSha256.Equals(assemblyHash, StringComparison.OrdinalIgnoreCase) &&
                candidate.AssemblyFirstPassSha256.Equals(firstPassHash, StringComparison.OrdinalIgnoreCase));

            if (!capabilities.HasRequiredRuntime)
            {
                problems.Add("程序集缺少离线渲染需要的原版事件、轨道、装饰或滤镜类型。");
            }

            var status = problems.Count > 0
                ? GameInstallationStatus.Invalid
                : adapter is null
                    ? GameInstallationStatus.CompatibleButUnverified
                    : GameInstallationStatus.Supported;

            return new GameInstallation(
                status,
                selectedPath,
                root,
                executablePath,
                dataDirectory,
                managedDirectory,
                FileVersionInfo.GetVersionInfo(unityPlayer).ProductVersion,
                assemblyHash,
                firstPassHash,
                capabilities,
                adapter,
                problems);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            problems.Add($"读取游戏程序集失败：{exception.Message}");
            return new GameInstallation(
                GameInstallationStatus.Invalid,
                selectedPath,
                root,
                executablePath,
                dataDirectory,
                managedDirectory,
                null,
                null,
                null,
                NoCapabilities,
                null,
                problems);
        }
    }

    public static IReadOnlyList<string> FindDefaultCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(candidates, Environment.GetEnvironmentVariable("ADOFAI_GAME_DIR"));
        AddIfPresent(candidates, @"C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice");
        AddIfPresent(candidates, @"C:\Program Files\Steam\steamapps\common\A Dance of Fire and Ice");

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    AddSteamLibrary(candidates, steamPath);
                    var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(libraryFile))
                    {
                        var vdf = File.ReadAllText(libraryFile);
                        foreach (Match match in SteamLibraryPathRegex().Matches(vdf))
                        {
                            AddSteamLibrary(candidates, match.Groups[1].Value.Replace("\\\\", "\\"));
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Steam 自动检测失败时仍允许用户手动选择，不把注册表问题当成安装错误。
            }
        }

        return candidates.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static GameInstallation Invalid(string selectedPath, string problem) => new(
        GameInstallationStatus.Invalid,
        selectedPath,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        NoCapabilities,
        null,
        [problem]);

    private static string? ResolveExecutable(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFileName(path).Equals(GameExecutableName, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var direct = Path.Combine(path, GameExecutableName);
        if (File.Exists(direct))
        {
            return direct;
        }

        var oneLevelDown = Path.Combine(path, GameDirectoryName, GameExecutableName);
        return File.Exists(oneLevelDown) ? oneLevelDown : null;
    }

    private static HashSet<string> ReadTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException($"{Path.GetFileName(assemblyPath)} 不包含 .NET 元数据。");
        }

        var metadata = peReader.GetMetadataReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var name = metadata.GetString(definition.Name);
            if (name == "<Module>")
            {
                continue;
            }

            var @namespace = metadata.GetString(definition.Namespace);
            result.Add(string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}");
        }

        return result;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void RequireDirectory(string path, string label, ICollection<string> problems)
    {
        if (!Directory.Exists(path))
        {
            problems.Add($"缺少{label}：{path}");
        }
    }

    private static void RequireFile(string path, string label, ICollection<string> problems)
    {
        if (!File.Exists(path))
        {
            problems.Add($"缺少 {label}：{path}");
        }
    }

    private static void AddSteamLibrary(ISet<string> candidates, string libraryRoot) =>
        AddIfPresent(candidates, Path.Combine(libraryRoot, "steamapps", "common", GameDirectoryName));

    private static void AddIfPresent(ISet<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.Combine(candidate, GameExecutableName)))
        {
            candidates.Add(Path.GetFullPath(candidate));
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s*\\\"([^\\\"]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
