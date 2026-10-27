using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

public static class RenderChartSanitizer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string CreateTemporaryCopy(
        string sourceChartPath,
        IReadOnlySet<int>? excludedDecorationIndices)
    {
        sourceChartPath = Path.GetFullPath(sourceChartPath);
        if (excludedDecorationIndices is null || excludedDecorationIndices.Count == 0)
            return sourceChartPath;

        var root = AdoJson.ParseObject(File.ReadAllText(sourceChartPath, Encoding.UTF8));
        if (root["decorations"] is JsonArray decorations)
        {
            foreach (var index in excludedDecorationIndices
                         .Where(index => index >= 0 && index < decorations.Count)
                         .OrderByDescending(static index => index))
                decorations.RemoveAt(index);
        }

        var directory = Path.GetDirectoryName(sourceChartPath)!;
        var name = Path.GetFileNameWithoutExtension(sourceChartPath);
        var temporaryPath = Path.Combine(
            directory,
            $".{name}.adofai-vfx-render-{Guid.NewGuid():N}.adofai");
        File.WriteAllText(
            temporaryPath,
            root.ToJsonString(JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return temporaryPath;
    }
}
