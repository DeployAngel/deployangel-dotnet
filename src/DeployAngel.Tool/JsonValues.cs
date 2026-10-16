using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeployAngel.Tool;

/// <summary>
/// Reads API documents the way the Python command reads its dicts: a missing
/// key and JSON null are the same, "or" falls through on any empty value, and
/// values print as Python's str() prints them. Everything the command shows
/// goes through here, so all three commands print the same text.
/// </summary>
internal static class JsonValues
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    public static bool Has(JsonNode? node, string key) => node is JsonObject obj && obj.ContainsKey(key);

    /// <summary>The object, or an empty one for null, a missing key, or anything else (Python's <c>x or {}</c>).</summary>
    public static JsonObject ObjectOf(JsonNode? node) => node as JsonObject ?? new JsonObject();

    /// <summary>The array's items, or none (Python's <c>x or []</c>).</summary>
    public static IReadOnlyList<JsonNode?> ItemsOf(JsonNode? node) =>
        node is JsonArray array ? array.ToList() : Array.Empty<JsonNode?>();

    /// <summary>The value if it's a JSON string, else null.</summary>
    public static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Python's bool(): null, false, 0, "", [], and {} are false.</summary>
    public static bool Truthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonArray array:
                return array.Count > 0;
            case JsonObject obj:
                return obj.Count > 0;
        }
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => node.GetValue<string>().Length > 0,
            JsonValueKind.Number => ToNumber(node) != 0,
            _ => false,
        };
    }

    /// <summary>The first truthy value, else the last, as Python's <c>a or b</c> returns.</summary>
    public static JsonNode? Or(JsonNode? first, JsonNode? second) => Truthy(first) ? first : second;

    /// <summary>Python's str() of a JSON value: None, True, 1.0, or the string itself.</summary>
    public static string Text(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonArray or JsonObject:
                return Repr(node);
        }
        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => NumberText(node),
            _ => "None",
        };
    }

    /// <summary>Python's int(): truncates numbers, parses strings, and throws on anything else.</summary>
    public static long ToInteger(JsonNode? node)
    {
        switch (node?.GetValueKind())
        {
            case JsonValueKind.Number:
                var raw = node!.ToJsonString();
                return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                    ? whole
                    : (long)Math.Truncate(ToNumber(node));
            case JsonValueKind.String:
                var text = node!.GetValue<string>().Trim();
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                throw new FormatException($"invalid literal for int() with base 10: '{text}'");
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                throw new FormatException($"int() argument must be a string or a number, not '{TypeName(node)}'");
        }
    }

    /// <summary>Python's float(): numbers, numeric strings, and booleans.</summary>
    public static double ToNumber(JsonNode? node)
    {
        switch (node?.GetValueKind())
        {
            case JsonValueKind.Number:
                return double.Parse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                if (double.TryParse(node!.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                throw new FormatException($"could not convert string to float: '{node.GetValue<string>()}'");
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                throw new FormatException($"float() argument must be a string or a real number, not '{TypeName(node)}'");
        }
    }

    /// <summary>A detached copy, since a node can belong to only one document.</summary>
    public static JsonNode? Copy(JsonNode? node) => node?.DeepClone();

    /// <summary>JSON as the command prints it: indented two spaces, with non-ASCII text left as is.</summary>
    public static string Pretty(JsonNode? node) => node is null ? "null" : node.ToJsonString(Indented);

    /// <summary>One line, for the MCP server and request bodies.</summary>
    public static string OneLine(JsonNode? node) => node is null ? "null" : node.ToJsonString(Compact);

    /// <summary>Python's repr() of a float: the shortest text that round-trips, with ".0" on whole numbers.</summary>
    public static string FloatText(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }
        if (value == Math.Floor(value) && Math.Abs(value) < 1e16)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture) + ".0";
        }
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E', StringComparison.Ordinal) ? text.Replace("E", "e", StringComparison.Ordinal) : text;
    }

    private static string NumberText(JsonNode node)
    {
        var raw = node.ToJsonString();
        if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
        {
            return raw;
        }
        return FloatText(ToNumber(node));
    }

    private static string Repr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonArray array:
                return "[" + string.Join(", ", array.Select(Repr)) + "]";
            case JsonObject obj:
                return "{" + string.Join(", ", obj.Select(pair => $"{Quote(pair.Key)}: {Repr(pair.Value)}")) + "}";
        }
        return node.GetValueKind() == JsonValueKind.String ? Quote(node.GetValue<string>()) : Text(node);
    }

    private static string Quote(string text)
    {
        var builder = new StringBuilder("'");
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '\'' => "\\'",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString(),
            });
        }
        return builder.Append('\'').ToString();
    }

    private static string TypeName(JsonNode? node) => node switch
    {
        null => "NoneType",
        JsonArray => "list",
        JsonObject => "dict",
        _ => "str",
    };
}
