using System.Text.Json;

namespace LolHighlight.Recording.Infrastructure;

internal static class JsonValues
{
    internal static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Normalize(p.Value)!),
        JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
