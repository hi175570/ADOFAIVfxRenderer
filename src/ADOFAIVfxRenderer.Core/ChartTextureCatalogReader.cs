using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

public static class ChartTextureCatalogReader
{
    private static readonly string[] TextureProperties =
    [
        "decorationImage",
        "bgImage",
        "trackTexture",
    ];

    public static IReadOnlyList<string> ReadReferencedFiles(string chartPath)
    {
        chartPath = Path.GetFullPath(chartPath);
        var directory = Path.GetDirectoryName(chartPath)
                        ?? throw new InvalidDataException("谱面路径缺少父目录。");
        var root = AdoJson.ParseObject(File.ReadAllText(chartPath));
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(root["settings"] as JsonObject, directory, result);
        foreach (var entry in (root["decorations"] as JsonArray ?? []).OfType<JsonObject>())
            Collect(entry, directory, result);
        foreach (var entry in (root["actions"] as JsonArray ?? []).OfType<JsonObject>())
            Collect(entry, directory, result);
        return result.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Collect(JsonObject? source, string directory, ISet<string> result)
    {
        if (source == null) return;
        foreach (var property in TextureProperties)
        {
            var value = source[property]?.ToString().Trim() ?? "";
            if (value.Length == 0 || value.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.GetFullPath(Path.Combine(directory, value.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(path)) result.Add(path);
        }
    }
}
