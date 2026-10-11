using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>The baked room meshes exported by nnnotes, restored from glTF to Unity coordinates.</summary>
public sealed class HomeGlb
{
    readonly byte[] binary;
    public JsonObject Document { get; }
    public sealed record Primitive(float[] Positions, float[] Uvs, float[]? Normals, uint[] Indices, int Material);
    public sealed record Mesh(string Name, Vector3 Center, Primitive[] Primitives);
    public Mesh[] Meshes { get; }

    public HomeGlb(byte[] bytes)
    {
        if (bytes.Length < 20 || ReadUInt(bytes, 0) != 0x46546c67 || ReadUInt(bytes, 4) != 2)
            throw new InvalidDataException("Home room must be a binary glTF 2.0 file.");
        int total = checked((int)ReadUInt(bytes, 8));
        if (total != bytes.Length) throw new InvalidDataException("Home GLB length does not match its header.");
        JsonObject? document = null;
        byte[]? payload = null;
        int offset = 12;
        while (offset < total)
        {
            if (total - offset < 8) throw new InvalidDataException("Truncated GLB chunk header.");
            int length = checked((int)ReadUInt(bytes, offset));
            uint type = ReadUInt(bytes, offset + 4);
            offset += 8;
            if (length > total - offset || length % 4 != 0) throw new InvalidDataException("Invalid GLB chunk length.");
            if (type == 0x4e4f534a)
            {
                if (document is not null) throw new InvalidDataException("Duplicate GLB JSON chunk.");
                document = JsonNode.Parse(bytes.AsSpan(offset, length)) as JsonObject;
            }
            else if (type == 0x004e4942)
            {
                if (payload is not null) throw new InvalidDataException("Duplicate GLB binary chunk.");
                payload = bytes.AsSpan(offset, length).ToArray();
            }
            offset += length;
        }
        Document = document ?? throw new InvalidDataException("Home GLB has no JSON object.");
        binary = payload ?? throw new InvalidDataException("Home GLB has no embedded geometry.");
        var meshes = new List<Mesh>();
        foreach (var node in Document["nodes"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (node["matrix"] is not null || node["translation"] is not null || node["rotation"] is not null || node["scale"] is not null || (node["children"] as JsonArray)?.Count > 0)
                throw new NotSupportedException("Home GLB nodes must have baked transforms.");
            var mesh = Element("meshes", Integer(node, "mesh"));
            var parts = new List<Primitive>();
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            foreach (var part in mesh["primitives"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (Integer(part, "mode", 4) != 4) throw new NotSupportedException("Home GLB supports triangle primitives.");
                var attributes = part["attributes"] ?? throw new InvalidDataException("Home mesh has no attributes.");
                var positions = Floats(Integer(attributes, "POSITION"), 3);
                var uvs = Floats(Integer(attributes, "TEXCOORD_0"), 2);
                var normals = attributes["NORMAL"] is null ? null : Floats(Integer(attributes, "NORMAL"), 3);
                if (uvs.Length / 2 != positions.Length / 3 || normals is not null && normals.Length != positions.Length)
                    throw new InvalidDataException("Home mesh attribute counts disagree.");
                for (int i = 0; i < positions.Length; i += 3)
                {
                    positions[i + 2] = -positions[i + 2];
                    var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                    if (normals is not null) normals[i + 2] = -normals[i + 2];
                }
                for (int i = 1; i < uvs.Length; i += 2) uvs[i] = 1 - uvs[i];
                var indices = Indices(Integer(part, "indices"));
                if (indices.Length % 3 != 0 || indices.Any(i => i >= positions.Length / 3)) throw new InvalidDataException("Invalid home mesh indices.");
                for (int i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                parts.Add(new(positions, uvs, normals, indices, Integer(part, "material")));
            }
            meshes.Add(new(Archive.S(node, "name", Archive.S(mesh, "name")), parts.Count == 0 ? Vector3.Zero : (min + max) / 2, parts.ToArray()));
        }
        Meshes = meshes.ToArray();
    }

    public JsonNode Element(string collection, int index)
    {
        var array = Document[collection] as JsonArray;
        return array is not null && index >= 0 && index < array.Count && array[index] is { } value
            ? value : throw new InvalidDataException($"Missing GLB {collection}[{index}].");
    }

    public Pixels Image(int index)
    {
        var image = Element("images", index);
        if (Archive.S(image, "mimeType", "image/png") != "image/png") throw new NotSupportedException("Home GLB currently requires embedded PNG textures.");
        return Png.Decode(View(Integer(image, "bufferView")).ToArray());
    }

    ReadOnlySpan<byte> View(int index)
    {
        var view = Element("bufferViews", index);
        if (Integer(view, "buffer", 0) != 0) throw new NotSupportedException("External GLB buffers are unsupported.");
        int start = Integer(view, "byteOffset", 0), length = Integer(view, "byteLength");
        if (start < 0 || length < 0 || start > binary.Length - length) throw new InvalidDataException("GLB buffer view exceeds its binary chunk.");
        return binary.AsSpan(start, length);
    }

    (int Count, int Type, int Start, int Stride, int View) Accessor(int index, int components)
    {
        var accessor = Element("accessors", index);
        if (accessor["sparse"] is not null) throw new NotSupportedException("Sparse home GLB accessors are unsupported.");
        string expected = components == 1 ? "SCALAR" : "VEC" + components;
        if (Archive.S(accessor, "type") != expected) throw new InvalidDataException("Unexpected home GLB accessor shape.");
        int type = Integer(accessor, "componentType");
        int size = type switch { 5121 => 1, 5123 => 2, 5125 or 5126 => 4, _ => throw new NotSupportedException("Unsupported GLB component type: " + type) };
        int view = Integer(accessor, "bufferView"), count = Integer(accessor, "count"), start = Integer(accessor, "byteOffset", 0);
        int stride = Integer(Element("bufferViews", view), "byteStride", components * size);
        if (count < 0 || start < 0 || stride < components * size || (long)start + (long)Math.Max(0, count - 1) * stride + (count > 0 ? components * size : 0) > View(view).Length)
            throw new InvalidDataException("Home GLB accessor exceeds its buffer view.");
        return (count, type, start, stride, view);
    }

    float[] Floats(int index, int components)
    {
        var a = Accessor(index, components);
        if (a.Type != 5126) throw new NotSupportedException("Home GLB positions and UVs must be floats.");
        var source = View(a.View); var values = new float[checked(a.Count * components)];
        for (int i = 0; i < a.Count; i++) for (int j = 0; j < components; j++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(source[(a.Start + i * a.Stride + j * 4)..]);
            if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite home GLB geometry.");
            values[i * components + j] = value;
        }
        return values;
    }

    uint[] Indices(int index)
    {
        var a = Accessor(index, 1); var source = View(a.View); var values = new uint[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            var span = source[(a.Start + i * a.Stride)..];
            values[i] = a.Type switch { 5121 => span[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(span), 5125 => BinaryPrimitives.ReadUInt32LittleEndian(span), _ => throw new InvalidDataException("Home GLB indices must be unsigned integers.") };
        }
        return values;
    }

    static uint ReadUInt(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    static int Integer(JsonNode node, string key, int? fallback = null) => node[key]?.GetValue<int>() ?? fallback ?? throw new InvalidDataException("Missing GLB " + key);
}
