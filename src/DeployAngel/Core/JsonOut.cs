using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DeployAngel.Core;

/// <summary>
/// A thin wrapper over Utf8JsonWriter. Payloads are written by hand, without
/// reflection, so the agent works in trimmed and native AOT apps.
/// </summary>
internal sealed class JsonOut
{
    private readonly MemoryStream _stream = new();
    private readonly Utf8JsonWriter _writer;

    public JsonOut()
    {
        _writer = new Utf8JsonWriter(_stream);
    }

    public void StartObject() => _writer.WriteStartObject();
    public void EndObject() => _writer.WriteEndObject();
    public void StartArray() => _writer.WriteStartArray();
    public void EndArray() => _writer.WriteEndArray();
    public void Name(string name) => _writer.WritePropertyName(name);

    public void Property(string name, string? value)
    {
        if (value is null) _writer.WriteNull(name);
        else _writer.WriteString(name, value);
    }

    public void Property(string name, long value) => _writer.WriteNumber(name, value);
    public void Property(string name, bool value) => _writer.WriteBoolean(name, value);

    public void Value(string? value)
    {
        if (value is null) _writer.WriteNullValue();
        else _writer.WriteStringValue(value);
    }

    public void Value(long value) => _writer.WriteNumberValue(value);
    public void Value(bool value) => _writer.WriteBooleanValue(value);

    /// <summary>A string, number, bool, null, list, or string-keyed dictionary.</summary>
    public void Value(object? value)
    {
        switch (value)
        {
            case null: _writer.WriteNullValue(); break;
            case string s: _writer.WriteStringValue(s); break;
            case bool b: _writer.WriteBooleanValue(b); break;
            case int i: _writer.WriteNumberValue(i); break;
            case long l: _writer.WriteNumberValue(l); break;
            case double d: _writer.WriteNumberValue(d); break;
            case IDictionary<string, object?> map:
                _writer.WriteStartObject();
                foreach (var pair in map)
                {
                    _writer.WritePropertyName(pair.Key);
                    Value(pair.Value);
                }
                _writer.WriteEndObject();
                break;
            case IDictionary<string, string> strings:
                _writer.WriteStartObject();
                foreach (var pair in strings) _writer.WriteString(pair.Key, pair.Value);
                _writer.WriteEndObject();
                break;
            case IEnumerable<string> list:
                _writer.WriteStartArray();
                foreach (var item in list) _writer.WriteStringValue(item);
                _writer.WriteEndArray();
                break;
            case System.Collections.IEnumerable items:
                _writer.WriteStartArray();
                foreach (var item in items) Value(item);
                _writer.WriteEndArray();
                break;
            default: _writer.WriteStringValue(value.ToString()); break;
        }
    }

    public void Property(string name, object? value)
    {
        _writer.WritePropertyName(name);
        Value(value);
    }

    public byte[] ToBytes()
    {
        _writer.Flush();
        return _stream.ToArray();
    }
}
