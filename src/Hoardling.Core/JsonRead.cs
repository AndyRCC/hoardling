using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hoardling.Core;

/// <summary>Small tolerant helpers for reading undocumented JSON payloads.</summary>
internal static class JsonRead
{
    public static JsonObject ParseObject(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Invalid JSON.", ex);
        }
        return node as JsonObject ?? throw new FormatException("Expected a JSON object.");
    }

    public static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue(out double d))
        {
            return double.IsFinite(d) ? d : null;
        }
        if (value.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
        {
            return double.IsFinite(d) ? d : null;
        }
        return null;
    }

    /// <summary>Walks nested objects by name; null as soon as a step is missing or not an object.</summary>
    public static JsonObject? Path(JsonObject? node, params string[] names)
    {
        foreach (var name in names)
        {
            node = node?[name] as JsonObject;
        }
        return node;
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    public static DateTimeOffset? Time(JsonNode? node) =>
        String(node) is { } s && DateTimeOffset.TryParse(TrimFraction(s), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

    /// <summary>Protobuf JSON timestamps carry up to 9 fractional digits; .NET parses at most 7.</summary>
    private static string TrimFraction(string s)
    {
        var dot = s.IndexOf('.');
        if (dot < 0)
        {
            return s;
        }
        var end = dot + 1;
        while (end < s.Length && char.IsAsciiDigit(s[end]))
        {
            end++;
        }
        var digits = end - dot - 1;
        return digits <= 7 ? s : string.Concat(s.AsSpan(0, dot + 8), s.AsSpan(end));
    }

    public static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}
