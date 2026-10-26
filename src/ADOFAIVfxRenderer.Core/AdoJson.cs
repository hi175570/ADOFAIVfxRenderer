using System.Text.Json;
using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

internal static class AdoJson
{
    public static JsonObject ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return ConvertElement(document.RootElement) as JsonObject
               ?? throw new InvalidDataException("谱面根节点不是 JSON 对象。");
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
