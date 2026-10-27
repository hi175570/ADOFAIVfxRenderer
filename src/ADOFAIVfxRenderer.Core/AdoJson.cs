using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace ADOFAIVfxRenderer.Core;

internal static class AdoJson
{
    public static bool ReadBoolean(JsonNode? node, bool fallback)
    {
        if (node is not JsonValue value) return fallback;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        if (value.TryGetValue<int>(out var integer)) return integer != 0;
        if (!value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return fallback;
        if (bool.TryParse(text, out boolean)) return boolean;
        if (int.TryParse(text, out integer)) return integer != 0;
        return text.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ? true
            : text.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? false
            : fallback;
    }

    public static JsonObject ParseObject(string json)
    {
        using var document = JsonDocument.Parse(NormalizeLegacyJson(json), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return ConvertElement(document.RootElement) as JsonObject
               ?? throw new InvalidDataException("谱面根节点不是 JSON 对象。");
    }

    private static string NormalizeLegacyJson(string json)
    {
        // Older ADOFAI editors occasionally emitted an extra comma between
        // properties (for example `"requiredMods": [],,`). The game parser
        // accepts these files, so the desktop-side metadata/export path must
        // accept the same legacy spelling without modifying the source chart.
        var normalized = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                normalized.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                normalized.Append(character);
                continue;
            }

            if (character == ',')
            {
                var next = index + 1;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                if (next < json.Length && json[next] == ',') continue;
            }
            normalized.Append(character);
        }
        return normalized.ToString();
    }

    private static JsonNode? ConvertElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    result[property.Name] = ConvertElement(property.Value);
                return result;
            }
            case JsonValueKind.Array:
            {
                var result = new JsonArray();
                foreach (var item in element.EnumerateArray()) result.Add(ConvertElement(item));
                return result;
            }
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString());
            case JsonValueKind.Number:
                // Keep the JsonElement-backed numeric value used by JsonNode.Parse so
                // existing callers can request int, double, or decimal as appropriate.
                return JsonValue.Create(element.Clone());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                throw new InvalidDataException($"不支持的 JSON 值类型：{element.ValueKind}。");
        }
    }
}
