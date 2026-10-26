using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ADOFAIVfxWorker
{
internal static class GameChartConverter
{
    private static readonly object Gate = new();
    private static Assembly? _gameAssembly;
    private static Type? _levelDataType;
    private static MethodInfo? _loadLevel;
    private static MethodInfo? _encode;
    private static GameObject? _gameContextHost;

    public static string LoadCurrentFormat(string chartPath, string managedDirectory)
    {
        EnsureInitialized(managedDirectory);
        var levelData = Activator.CreateInstance(
            _levelDataType!,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            args: new object[] { true },
            culture: null)
            ?? throw new InvalidOperationException("The installed game's LevelData constructor returned null.");
        var arguments = new object?[] { Path.GetFullPath(chartPath), null };
        var loaded = (bool)(_loadLevel!.Invoke(levelData, arguments) ?? false);
        if (!loaded)
        {
            var result = arguments[1]?.ToString() ?? "Unknown";
            throw new InvalidDataException($"The installed game rejected the chart while converting it: {result}.");
        }
        return (string)(_encode!.Invoke(levelData, null)
                        ?? throw new InvalidOperationException("The installed game's LevelData.Encode returned null."));
    }

    private static void EnsureInitialized(string managedDirectory)
    {
        lock (Gate)
        {
            if (_gameAssembly != null) return;
            managedDirectory = Path.GetFullPath(managedDirectory);
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => ResolveAssembly(managedDirectory, args.Name);
            _gameAssembly = Assembly.LoadFrom(Path.Combine(managedDirectory, "Assembly-CSharp.dll"));
            var startupType = _gameAssembly.GetType("ADOStartup", throwOnError: true)!;
            var setupLevelEvents = startupType.GetMethod(
                "SetupLevelEventsInfo",
                BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(startupType.FullName, "SetupLevelEventsInfo");
            setupLevelEvents.Invoke(null, null);
            InitializeDlcMetadata(_gameAssembly);
            EnableInstalledGameProEvents(_gameAssembly);
            InitializeCustomLevelContext(_gameAssembly);

            _levelDataType = _gameAssembly.GetType("ADOFAI.LevelData", throwOnError: true)!;
            _loadLevel = _levelDataType.GetMethod(
                "LoadLevel",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string), _gameAssembly.GetType("ADOFAI.LoadResult", throwOnError: true)!.MakeByRefType() },
                modifiers: null)
                ?? throw new MissingMethodException(_levelDataType.FullName, "LoadLevel");
            _encode = _levelDataType.GetMethod("Encode", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMethodException(_levelDataType.FullName, "Encode");
        }
    }

    private static void InitializeDlcMetadata(Assembly assembly)
    {
        var neoType = assembly.GetType("NeoCosmosManager", throwOnError: true)!;
        var neo = Activator.CreateInstance(neoType)
                  ?? throw new InvalidOperationException("The installed game's NeoCosmosManager constructor returned null.");
        var dlcType = assembly.GetType("DLCManager", throwOnError: true)!;
        dlcType.GetField("installed", BindingFlags.Instance | BindingFlags.Public)?.SetValue(neo, true);
        dlcType.GetField("own", BindingFlags.Instance | BindingFlags.Public)?.SetValue(neo, true);
    }

    private static void EnableInstalledGameProEvents(Assembly assembly)
    {
        var gcsType = assembly.GetType("GCS", throwOnError: true)!;
        gcsType.GetField("steamBranchName", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, "nebula");
        var persistenceType = assembly.GetType("Persistence", throwOnError: true)!;
        persistenceType.GetProperty("enableProEvents", BindingFlags.Static | BindingFlags.Public)?.SetValue(null, true);
        var levelEventType = assembly.GetType("ADOFAI.LevelEvent", throwOnError: true)!;
        var enabled = (bool)(levelEventType.GetProperty(
            "enableProEvents",
            BindingFlags.Static | BindingFlags.Public)?.GetValue(null) ?? false);
        if (!enabled)
            throw new InvalidOperationException(
                "The installed game has Pro events disabled, so its converter would discard visual events.");
    }

    private static void InitializeCustomLevelContext(Assembly assembly)
    {
        var scnGameType = assembly.GetType("scnGame", throwOnError: true)!;
        _gameContextHost = new GameObject("ADOFAI Installed Chart Conversion Context");
        _gameContextHost.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_gameContextHost);
        var component = _gameContextHost.AddComponent(scnGameType);
        scnGameType.GetField("forceOldLevelStyle", BindingFlags.Instance | BindingFlags.Public)
            ?.SetValue(component, false);
        scnGameType.GetField("instance", BindingFlags.Static | BindingFlags.Public)
            ?.SetValue(null, component);
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
        catch
        {
            return null;
        }
    }
}
}
