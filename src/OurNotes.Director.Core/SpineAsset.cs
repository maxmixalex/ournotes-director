using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Spine;

namespace OurNotes.Director.Core;

public sealed record SpineAnimationInfo(string Name, float Duration);
public sealed record SpineSlotInfo(string Name, string Bone);
/// <summary>The exporter's setup-pose rectangle in original Spine image pixels, before SkeletonDataAsset.scale.</summary>
public sealed record SpineDesignBounds(float X, float Y, float Width, float Height);

// Pure managed asset loading, shared by inspection, bounds sampling, and the native renderer.
// File names are resolved by the caller, so the same code reads an archive or an extracted spot directory.
public sealed class SpineAsset
{
    public const string RuntimeVersion = "4.2";
    public const string RuntimeCommit = "e7dc1435fa4a0083ab431f1b28e083c14a1f5c68";
    internal SkeletonData Data { get; }
    internal AnimationStateData StateData { get; }
    public string Name { get; }
    public string File { get; }
    public string Version => Data.Version;
    public float FramesPerSecond => Data.Fps;
    public float SourceScale { get; }
    public SpineDesignBounds DesignBounds => new(Data.X, Data.Y, Data.Width, Data.Height);
    public IReadOnlyList<SpineAnimationInfo> Animations { get; }
    public IReadOnlyList<string> Skins { get; }
    public IReadOnlyList<SpineSlotInfo> Slots { get; }
    public IReadOnlyList<SpineTexture> Textures { get; }

    SpineAsset(JsonNode record, Func<string, byte[]> readFile)
    {
        Name = Archive.S(record, "name");
        File = Archive.S(record, "skeleton");
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(File))
            throw new InvalidDataException("Spine skeleton record needs name and skeleton");
        float scale = Actor.N(record["scale"], 1);
        if (!float.IsFinite(scale) || scale <= 0) throw new InvalidDataException($"Invalid Spine scale: {Name}");
        SourceScale = scale;
        var loader = new PageLoader(readFile);
        var atlasFiles = record["atlases"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").ToArray() ?? [];
        if (atlasFiles.Length == 0 || atlasFiles.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"Spine skeleton has no atlas: {Name}");
        var atlases = atlasFiles.Select(file =>
        {
            using var text = new StringReader(Encoding.UTF8.GetString(readFile(file)));
            return new Atlas(text, Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar)) ?? "", loader);
        }).ToArray();
        var attachments = new AtlasAttachmentLoader(atlases);
        byte[] bytes = readFile(File);
        if (File.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            string text = Encoding.UTF8.GetString(bytes);
            string? version = JsonNode.Parse(text)?["skeleton"]?["spine"]?.GetValue<string>();
            CheckVersion(File, version);
            using var reader = new StringReader(text);
            Data = new SkeletonJson(attachments) { Scale = scale }.ReadSkeletonData(reader);
        }
        else
        {
            using (var header = new MemoryStream(bytes, false)) CheckVersion(File, SkeletonBinary.GetVersionString(header));
            using var input = new MemoryStream(bytes, false);
            Data = new SkeletonBinary(attachments) { Scale = scale }.ReadSkeletonData(input)
                ?? throw new InvalidDataException($"Invalid Spine binary: {File}");
        }
        StateData = new(Data) { DefaultMix = Actor.N(record["defaultMix"]) };
        if (!float.IsFinite(StateData.DefaultMix) || StateData.DefaultMix < 0)
            throw new InvalidDataException($"Invalid Spine default mix: {Name}");
        foreach (var mix in record["mixes"]?.AsArray().OfType<JsonNode>() ?? [])
        {
            float duration = Actor.N(mix["duration"]);
            if (!float.IsFinite(duration) || duration < 0) throw new InvalidDataException($"Invalid Spine animation mix: {Name}");
            StateData.SetMix(Archive.S(mix, "from"), Archive.S(mix, "to"), duration);
        }
        Animations = Data.Animations.Select(a => new SpineAnimationInfo(a.Name, a.Duration)).ToArray();
        Skins = Data.Skins.Select(s => s.Name).ToArray();
        Slots = Data.Slots.Select(s => new SpineSlotInfo(s.Name, s.BoneData.Name)).ToArray();
        Textures = loader.Textures;
    }

    public static SpineAsset Load(JsonNode skeletonRecord, Func<string, byte[]> readFile)
        => new(skeletonRecord, readFile);

    static void CheckVersion(string file, string? version)
    {
        if (version != RuntimeVersion && version?.StartsWith(RuntimeVersion + ".", StringComparison.Ordinal) != true)
            throw new NotSupportedException($"Spine skeleton {file} was exported by {version ?? "an unknown version"}; the bundled runtime requires Spine {RuntimeVersion}.x");
    }

    sealed class PageLoader(Func<string, byte[]> readFile) : TextureLoader
    {
        public List<SpineTexture> Textures { get; } = [];
        public void Load(AtlasPage page, string path)
        {
            path = path.Replace('\\', '/');
            byte[] bytes = readFile(path);
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException($"Spine atlas page is not a PNG: {path}");
            int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (width <= 0 || height <= 0 || page.width != 0 && page.width != width || page.height != 0 && page.height != height)
                throw new InvalidDataException($"Spine atlas page dimensions differ from its PNG: {path}");
            page.width = width;
            page.height = height;
            var texture = new SpineTexture(path, bytes, page);
            page.rendererObject = texture;
            Textures.Add(texture);
        }
        public void Unload(object texture) { }
    }
}

public sealed class SpineTexture
{
    internal byte[] Bytes { get; }
    internal AtlasPage Page { get; }
    internal string Key { get; }
    public string File { get; }
    public int Width => Page.width;
    public int Height => Page.height;
    public bool PremultipliedAlpha => Page.pma;
    internal SpineTexture(string file, byte[] bytes, AtlasPage page)
    {
        File = file;
        Bytes = bytes;
        Page = page;
        // Separate skeleton records often share one atlas. Decode/upload those bytes once per GL context.
        Key = Archive.Hash(bytes) + $":{page.minFilter}:{page.magFilter}:{page.uWrap}:{page.vWrap}";
    }
}
