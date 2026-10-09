using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OurNotes.Director.Core;

/// <summary>
/// A JSON object with record-style property naming. Unlike anonymous CLR objects,
/// its shape is explicit and does not require runtime reflection in Native AOT.
/// Ordinary dictionaries retain their keys, as System.Text.Json does by default.
/// </summary>
[JsonConverter(typeof(JsonRecordConverter))]
public sealed class JsonRecord : Dictionary<string, object?> { }

public sealed class JsonRecordConverter : JsonConverter<JsonRecord>
{
    public override JsonRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("JsonRecord is an output type; parse input with JsonNode or a declared DTO.");

    public override void Write(Utf8JsonWriter writer, JsonRecord value, JsonSerializerOptions options) =>
        JsonOutput.Write(writer, value, options);
}

public static class JsonOutput
{
    static readonly JsonSerializerOptions DefaultOptions = new();
    public static JsonRecord Object(params (string Name, object? Value)[] properties)
    {
        var result = new JsonRecord();
        foreach (var (name, value) in properties) result.Add(name, value);
        return result;
    }

    public static string Serialize(object? value, JsonSerializerOptions? options = null) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(value, options));

    public static JsonNode? SerializeToNode(object? value, JsonSerializerOptions? options = null) =>
        JsonNode.Parse(SerializeToUtf8Bytes(value, options));

    public static byte[] SerializeToUtf8Bytes(object? value, JsonSerializerOptions? options = null)
    {
        options ??= DefaultOptions;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = options.WriteIndented,
            Encoder = options.Encoder,
            MaxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth
        })) Write(writer, value, options);
        return stream.ToArray();
    }

    internal static void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        // Only the declared DTOs below need generated metadata. Clone the caller's
        // options so their use with another serializer/context remains valid.
        DirectorJsonContext? context = null;
        DirectorJsonContext Metadata() => context ??= new(new JsonSerializerOptions(options));
        void Value(object? item)
        {
            switch (item)
            {
                case null: writer.WriteNullValue(); break;
                case JsonRecord record:
                    writer.WriteStartObject();
                    foreach (var (name, content) in record)
                    {
                        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
                        Value(content);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonNode node: node.WriteTo(writer, options); break;
                case JsonElement element: element.WriteTo(writer); break;
                case string text: writer.WriteStringValue(text); break;
                case char character: writer.WriteStringValue(character.ToString()); break;
                case bool boolean: writer.WriteBooleanValue(boolean); break;
                case byte number: writer.WriteNumberValue(number); break;
                case sbyte number: writer.WriteNumberValue(number); break;
                case short number: writer.WriteNumberValue(number); break;
                case ushort number: writer.WriteNumberValue(number); break;
                case int number: writer.WriteNumberValue(number); break;
                case uint number: writer.WriteNumberValue(number); break;
                case long number: writer.WriteNumberValue(number); break;
                case ulong number: writer.WriteNumberValue(number); break;
                case float number: writer.WriteNumberValue(number); break;
                case double number: writer.WriteNumberValue(number); break;
                case decimal number: writer.WriteNumberValue(number); break;
                case DateTime date: writer.WriteStringValue(date); break;
                case DateTimeOffset date: writer.WriteStringValue(date); break;
                case Guid guid: writer.WriteStringValue(guid); break;
                case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
                case Shot shot: JsonSerializer.Serialize(writer, shot, Metadata().Shot); break;
                case GradeSpec grade: JsonSerializer.Serialize(writer, grade, Metadata().GradeSpec); break;
                case ImageSpec image: JsonSerializer.Serialize(writer, image, Metadata().ImageSpec); break;
                case CameraSpec camera: JsonSerializer.Serialize(writer, camera, Metadata().CameraSpec); break;
                case DialoguePlan plan: JsonSerializer.Serialize(writer, plan, Metadata().DialoguePlan); break;
                case AdvProgram program: JsonSerializer.Serialize(writer, program, Metadata().AdvProgram); break;
                case ContactCell cell: JsonSerializer.Serialize(writer, cell, Metadata().ContactCell); break;
                case IDictionary dictionary:
                    writer.WriteStartObject();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        string key = entry.Key is string name ? name : Convert.ToString(entry.Key, CultureInfo.InvariantCulture)
                            ?? throw new JsonException("A JSON dictionary key cannot be null.");
                        writer.WritePropertyName(options.DictionaryKeyPolicy?.ConvertName(key) ?? key);
                        Value(entry.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case IEnumerable sequence:
                    writer.WriteStartArray();
                    foreach (object? entry in sequence) Value(entry);
                    writer.WriteEndArray();
                    break;
                default:
                    throw new NotSupportedException($"JSON output type {item.GetType().Name} needs an explicit JsonRecord or source-generated DTO.");
            }
        }
        Value(value);
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Shot))]
[JsonSerializable(typeof(GradeSpec))]
[JsonSerializable(typeof(ImageSpec))]
[JsonSerializable(typeof(CameraSpec))]
[JsonSerializable(typeof(DialoguePlan))]
[JsonSerializable(typeof(AdvProgram))]
[JsonSerializable(typeof(ContactCell))]
internal partial class DirectorJsonContext : JsonSerializerContext { }
