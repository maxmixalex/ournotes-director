using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public enum SceneDocumentKind { Unknown, UnifiedProject, LegacyShot }
public enum SceneIssueSeverity { Info, Warning, Error }

/// <summary>One-based line/column and a zero-based UTF-16 offset in the supplied editor text.</summary>
public sealed record SceneParseError(string Message, int Line, int Column, int Offset);
public sealed class SceneDocumentParseException(SceneParseError error)
    : FormatException($"{error.Message}（第 {error.Line} 行，第 {error.Column} 列）")
{
    public SceneParseError Error { get; } = error;
}

public sealed record SceneValidationIssue(SceneIssueSeverity Severity, string Code, string Path, string Message);
public sealed record SceneValidationResult(IReadOnlyList<SceneValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == SceneIssueSeverity.Error);
    public bool RequiresNativeValidation => true;
    public string Summary => HasErrors
        ? "JSON 结构或数值有错误；修正后仍需运行原生验证。"
        : "JSON 结构检查通过；尚未验证归档资源、模型参数或完整动画。请继续原生验证和预览。";
}

public sealed record SceneSource(string Adv, string Site, string Language)
{
    public bool IsLibrary => string.IsNullOrWhiteSpace(Adv) || Adv == DirectorProject.LibrarySource;
    public string EffectiveAdv => IsLibrary ? DirectorProject.LibrarySource : Adv;
}
public sealed record SceneOutput(int Width, int Height, int Supersample, string Codec,
    string CineformQuality, bool Clean, string Letterbox);
public sealed record SceneRowLocation(string Section, int Index);
/// <summary>Values are the exact row patch, not evaluated state. Empty Space means the row inherits the current mode.</summary>
public sealed record SceneActorEntry(SceneRowLocation Location, string TargetName, string? Model,
    string Space, JsonObject Values);
public sealed record SceneCameraEntry(SceneRowLocation Location, JsonObject Values);

/// <summary>A private render input whose local image references are independent of its temporary directory.</summary>
public sealed class SceneSnapshot : IDisposable
{
    internal SceneSnapshot(string filePath, string? sourceFilePath, string baseDirectory)
    {
        FilePath = filePath;
        SourceFilePath = sourceFilePath;
        BaseDirectory = baseDirectory;
    }

    public string FilePath { get; }
    public string? SourceFilePath { get; }
    public string BaseDirectory { get; }
    bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        File.Delete(FilePath);
        disposed = true;
    }
}

/// <summary>
/// A lossless editor envelope, not a renderer. Unknown fields remain editable and are reported by Validate.
/// Root and row values are detached copies so every mutation has an explicit document transaction.
/// </summary>
public sealed partial class SceneDocument
{
    static readonly string[] Sections = ["initialize", "commands", "finalize"];
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly UTF8Encoding Utf8 = new(false);
    JsonObject root;
    string? savedText;

    SceneDocument(JsonObject value, string text, string? filePath, string? baseDirectory)
    {
        root = value;
        Text = text;
        FilePath = filePath is null ? null : Path.GetFullPath(filePath);
        BaseDirectory = FilePath is not null ? Path.GetDirectoryName(FilePath)!
            : Path.GetFullPath(baseDirectory ?? Environment.CurrentDirectory);
    }

    public SceneDocumentKind Kind => Scalar(root["format"]) == DirectorProject.Format ? SceneDocumentKind.UnifiedProject
        : root["format"] is null && root["actors"] is JsonArray ? SceneDocumentKind.LegacyShot : SceneDocumentKind.Unknown;
    public string? FilePath { get; private set; }
    public string BaseDirectory { get; private set; }
    public string Text { get; private set; }
    public bool IsModified => savedText is null || !StringComparer.Ordinal.Equals(savedText, Text);
    public JsonObject Root => (JsonObject)root.DeepClone();
    public SceneSource Source
    {
        get
        {
            var source = root["source"] as JsonObject;
            return new(Scalar(source?["adv"], Kind == SceneDocumentKind.LegacyShot ? "10060" : ""),
                Scalar(source?["site"], "intl"), Scalar(source?["language"], "zh-Hans"));
        }
    }
    /// <summary>Effective file defaults for clip/frame; explicit CLI options can override these values.</summary>
    public SceneOutput Output
    {
        get
        {
            var output = root["output"] as JsonObject;
            bool legacy = Kind == SceneDocumentKind.LegacyShot;
            return new(Integer(output?["width"], 1920), Integer(output?["height"], 1080),
                Integer(output?[legacy ? "renderScale" : "supersample"], 2),
                legacy ? "prores" : Scalar(output?["codec"], "h264"),
                legacy ? VideoEncoding.DefaultCineFormQuality : Scalar(output?["cineformQuality"], VideoEncoding.DefaultCineFormQuality),
                legacy || Boolean(output?["clean"], false), legacy ? "none" : Scalar(output?["letterbox"], "game"));
        }
    }

    public static SceneDocument Parse(string text, string? filePath = null, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (payload, start) = Unwrap(text);
        var bytes = Utf8.GetBytes(payload);
        try
        {
            // JsonObject cannot represent duplicate keys losslessly. Reject them before materialization.
            var reader = new Utf8JsonReader(bytes);
            var scopes = new Stack<HashSet<string>?>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) scopes.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.StartArray) scopes.Push(null);
                else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject) scopes.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !scopes.Peek()!.Add(reader.GetString()!))
                    throw Failure(text, start + Utf8.GetCharCount(bytes.AsSpan(0, (int)reader.TokenStartIndex)),
                        $"重复的 JSON 字段：{reader.GetString()}。请明确保留一个值。");
            }
            var parsed = JsonNode.Parse(payload);
            if (parsed is not JsonObject obj)
                throw Failure(text, start + FirstContent(payload), "场景 JSON 必须是一个对象。");
            return new(obj, payload, filePath, baseDirectory);
        }
        catch (JsonException error)
        {
            int offset = 0;
            for (long line = 0; line < (error.LineNumber ?? 0) && offset < bytes.Length; line++)
            {
                while (offset < bytes.Length && bytes[offset++] != (byte)'\n') { }
            }
            offset = (int)Math.Min(bytes.Length, offset + (error.BytePositionInLine ?? 0));
            string message = error.Message;
            int position = message.IndexOf("LineNumber:", StringComparison.Ordinal);
            if (position >= 0) message = message[..position].TrimEnd(' ', '|');
            throw Failure(text, start + Utf8.GetCharCount(bytes.AsSpan(0, offset)), "JSON 语法错误：" + message);
        }
    }

    public static bool TryParse(string text, out SceneDocument? document, out SceneParseError? error,
        string? filePath = null, string? baseDirectory = null)
    {
        try { document = Parse(text, filePath, baseDirectory); error = null; return true; }
        catch (SceneDocumentParseException failure) { document = null; error = failure.Error; return false; }
    }

    public static SceneDocument Open(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var document = Parse(File.ReadAllText(fullPath, Utf8), fullPath);
        document.savedText = document.Text;
        return document;
    }

    public void ReplaceText(string text)
    {
        // A failed paste/edit leaves the previous valid document and its file identity intact.
        var replacement = Parse(text, FilePath, BaseDirectory);
        root = replacement.root;
        Text = replacement.Text;
    }

    public string ToJson(bool indented = true) => Serialize(root, indented);
    public void Format() => Text = ToJson();
    public string ResolveAssetPath(string path) => Path.GetFullPath(path, BaseDirectory);

    internal string TextAtDirectory(string directory)
    {
        string destination = Path.GetFullPath(directory);
        if (SamePath(destination, BaseDirectory)) return Text;
        var clone = (JsonObject)root.DeepClone();
        return RewriteImagePaths(clone, original => Path.IsPathRooted(original) ? original
            : Path.GetRelativePath(destination, ResolveAssetPath(original))) ? Serialize(clone) : Text;
    }

    public void Save(string? path = null)
    {
        string destination = Path.GetFullPath(path ?? FilePath ?? throw new ArgumentException("新文档需要保存路径。", nameof(path)));
        string directory = Path.GetDirectoryName(destination)!;
        var clone = (JsonObject)root.DeepClone();
        bool relocated = !SamePath(directory, BaseDirectory) && RewriteImagePaths(clone,
            original => Path.IsPathRooted(original) ? original : Path.GetRelativePath(directory, ResolveAssetPath(original)));
        string text = relocated ? Serialize(clone) : Text;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".scene-save-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8)) writer.Write(text);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        root = clone;
        Text = savedText = text;
        FilePath = destination;
        BaseDirectory = directory;
    }

    public SceneSnapshot CreateSnapshot(string directory)
    {
        string folder = Path.GetFullPath(directory);
        var clone = (JsonObject)root.DeepClone();
        RewriteImagePaths(clone, ResolveAssetPath);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "scene-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, Utf8)) writer.Write(Serialize(clone));
            return new(path, FilePath, BaseDirectory);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    public void SetSource(string? adv = null, string? site = null, string? language = null)
    {
        EnsureKnown();
        foreach (var (value, name) in new[] { (adv, "adv"), (site, "site"), (language, "language") })
            if (value is not null && string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " 不能为空。", name);
        Mutate(copy =>
        {
            var source = ObjectField(copy, "source");
            if (adv is not null) source["adv"] = adv;
            if (site is not null) source["site"] = site;
            if (language is not null) source["language"] = language;
        });
    }

    public void SetOutput(int? width = null, int? height = null, int? supersample = null,
        string? codec = null, string? cineformQuality = null, bool? clean = null, string? letterbox = null)
    {
        EnsureKnown();
        if (width is not null) CheckRange(width.Value, 16, RenderDimensions.MaxSide, nameof(width));
        if (height is not null) CheckRange(height.Value, 16, RenderDimensions.MaxSide, nameof(height));
        if (supersample is not null) CheckRange(supersample.Value, 1, 4, nameof(supersample));
        if (codec is not null) codec = VideoEncoding.NormalizeCodec(codec);
        if (cineformQuality is not null) cineformQuality = VideoEncoding.NormalizeCineFormQuality(cineformQuality);
        if (letterbox is not null && letterbox is not ("game" or "none")) throw new ArgumentException("letterbox 必须为 game 或 none。", nameof(letterbox));
        var existingOutput = Output;
        RenderDimensions.Validate(width ?? existingOutput.Width, height ?? existingOutput.Height, supersample ?? existingOutput.Supersample);
        bool legacy = Kind == SceneDocumentKind.LegacyShot;
        if (legacy && (codec is not null || cineformQuality is not null || clean is not null || letterbox is not null))
            throw new NotSupportedException("旧 Shot 不支持 codec、cineformQuality、clean 或 letterbox 字段；请在原生导出选项中设置，或使用统一项目格式。");
        Mutate(copy =>
        {
            var output = ObjectField(copy, "output");
            if (width is not null) output["width"] = width.Value;
            if (height is not null) output["height"] = height.Value;
            if (supersample is not null) output[legacy ? "renderScale" : "supersample"] = supersample.Value;
            if (codec is not null) output["codec"] = codec;
            if (cineformQuality is not null) output["cineformQuality"] = cineformQuality;
            if (clean is not null) output["clean"] = clean.Value;
            if (letterbox is not null) output["letterbox"] = letterbox;
        });
    }

    /// <summary>Rows absent from an inherited source cannot be enumerated without loading that source.</summary>
    public IReadOnlyList<SceneActorEntry> GetActors()
    {
        var result = new List<SceneActorEntry>();
        if (Kind == SceneDocumentKind.LegacyShot && root["actors"] is JsonArray actors)
        {
            for (int index = 0; index < actors.Count; index++)
            {
                if (actors[index] is not JsonObject actor) continue;
                var values = new JsonObject
                {
                    ["position"] = new JsonArray(CloneOr(actor["x"], .5), CloneOr(actor["y"], .5)),
                    ["rotation"] = new JsonArray(0, 0, CloneOr(actor["rotation"], 0)),
                    ["height"] = CloneOr(actor["height"], .95), ["opacity"] = CloneOr(actor["opacity"], 1),
                    ["brightness"] = CloneOr(actor["brightness"], 1), ["speed"] = CloneOr(actor["speed"], 1)
                };
                foreach (string key in new[] { "tint", "physics", "breath", "blink", "loop" })
                    if (actor[key] is { } value) values[key] = value.DeepClone();
                result.Add(new(new("actors", index), Scalar(actor["name"], "actor"), Scalar(actor["model"]), "screen", values));
            }
        }
        else if (Kind == SceneDocumentKind.UnifiedProject)
        {
            var models = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (location, row) in Rows(root))
            {
                string command = CommandName(row), name = Scalar(row["TargetName"]);
                if (command == "Character" && Scalar(row["IgnoreData"]) is not ("1" or "true"))
                    models[name] = Scalar(row["Model"], Scalar(row["TargetAssetName"]));
                if (command == "Director.Actor")
                    result.Add(new(location, name, models.GetValueOrDefault(name), Scalar(row["Space"]),
                        row["Values"] is JsonObject values ? (JsonObject)values.DeepClone() : new()));
            }
        }
        return result;
    }

    public IReadOnlyList<SceneCameraEntry> GetCameras()
    {
        if (Kind == SceneDocumentKind.LegacyShot)
            return [new(new("camera", 0), new() { ["framing"] = root["camera"]?.DeepClone() ?? new JsonObject() })];
        if (Kind != SceneDocumentKind.UnifiedProject) return [];
        return Rows(root).Where(pair => CommandName(pair.Row) == "Director.Camera")
            .Select(pair => new SceneCameraEntry(pair.Location, pair.Row["Values"] is JsonObject values ? (JsonObject)values.DeepClone() : new())).ToArray();
    }

    public void PatchActor(SceneRowLocation location, JsonObject values, string? space = null,
        double? duration = null, string? ease = null, bool? isNoWait = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Kind == SceneDocumentKind.LegacyShot)
        {
            RejectLegacyTiming(duration, ease, isNoWait);
            if (space is not null && space != "screen") throw new NotSupportedException("旧 Shot 角色仅支持 screen 坐标。");
            if (location.Section != "actors") throw new ArgumentException("不是旧 Shot 角色位置。", nameof(location));
            Mutate(copy => MergeLegacyActor(ArrayRow(copy, location), values));
            return;
        }
        PatchDirector(location, "Director.Actor", values, space, duration, ease, isNoWait);
    }

    public void PatchCamera(SceneRowLocation location, JsonObject values, double? duration = null,
        string? ease = null, bool? isNoWait = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Kind == SceneDocumentKind.LegacyShot)
        {
            RejectLegacyTiming(duration, ease, isNoWait);
            if (location is not { Section: "camera", Index: 0 }) throw new ArgumentException("不是旧 Shot 相机位置。", nameof(location));
            if (values.Any(pair => pair.Key != "framing") || values["framing"] is not JsonObject framing)
                throw new NotSupportedException("旧 Shot 相机仅支持 framing 的 x、y、zoom、roll。");
            ValidatePatch("Director.Camera", null, values, null, null, null, null);
            Mutate(copy => Merge(ObjectField(copy, "camera"), framing));
            return;
        }
        PatchDirector(location, "Director.Camera", values, null, duration, ease, isNoWait);
    }

    public SceneRowLocation AppendActor(string targetName, JsonObject values, string space = "screen", string section = "commands")
    {
        EnsureUnified();
        ValidatePatch("Director.Actor", targetName, values, space, null, null, null);
        return Append(section, new() { ["cmd"] = "Director.Actor", ["TargetName"] = targetName, ["Space"] = space, ["Values"] = values.DeepClone() });
    }

    public SceneRowLocation AppendCamera(JsonObject values, string section = "commands")
    {
        EnsureUnified();
        ValidatePatch("Director.Camera", null, values, null, null, null, null);
        return Append(section, new() { ["cmd"] = "Director.Camera", ["Values"] = values.DeepClone() });
    }

    /// <summary>Explicitly replace a whole section. Null restores inheritance; [] explicitly clears it.</summary>
    public void ReplaceSection(string section, JsonArray? rows)
    {
        EnsureUnified();
        CheckSection(section);
        Mutate(copy => { if (rows is null) copy.Remove(section); else copy[section] = rows.DeepClone(); });
    }

    public SceneValidationResult Validate()
    {
        var issues = new List<SceneValidationIssue>();
        void Error(string code, string path, string message) => issues.Add(new(SceneIssueSeverity.Error, code, path, message));
        if (Kind == SceneDocumentKind.UnifiedProject) ValidateUnified(issues);
        else if (Kind == SceneDocumentKind.LegacyShot)
        {
            try
            {
                var shot = JsonSerializer.Deserialize(Text, SceneEditorJsonContext.Default.Shot)
                    ?? throw new JsonException("旧 Shot 为空。");
                if (shot.Output is null || shot.Grade is null || shot.Camera is null || shot.Background is null ||
                    shot.Actors is null || shot.Tracks is null || shot.Events is null ||
                    shot.Actors.Any(actor => actor is null || actor.Tint is null || actor.Parameters is null) ||
                    shot.Tracks.Any(track => track is null || track.Keys is null || track.Keys.Any(key => key is null)) ||
                    shot.Events.Any(entry => entry is null))
                    throw new ArgumentException("旧 Shot 的 output、grade、camera、background、actors、tracks、events 及其必要子项不能为 null。");
                shot.Validate();
                ValidateImage(root["backgroundImage"], "$.backgroundImage", Error);
            }
            catch (JsonException error) { Error("legacy.shape", error.Path ?? "$", error.Message); }
            catch (ArgumentException error) { Error("legacy.value", "$", error.Message); }
        }
        else Error("document.format", "$.format", "需要 ournotes-director/project version 1，或包含 actors 数组的旧 Shot 对象。原文及未知字段已保留。");
        issues.Add(new(SceneIssueSeverity.Info, "native.validation-required", "$",
            "此检查不加载归档、Cubism、Spine、材质或视频编码器。原生 validate 还会加载资源并渲染首帧；完整动画仍需预览或导出验收。"));
        return new(issues.AsReadOnly());
    }

    void ValidateUnified(List<SceneValidationIssue> issues)
    {
        void Error(string code, string path, string message) => issues.Add(new(SceneIssueSeverity.Error, code, path, message));
        void Keys(JsonObject value, string[] allowed, string path)
        {
            foreach (var pair in value)
                if (!allowed.Contains(pair.Key, StringComparer.Ordinal))
                    Error("field.unknown", path + "." + pair.Key, "原生项目不支持此字段；编辑器保留了原文，请核对字段名或版本。");
        }
        Keys(root, ["format", "version", "source", "output", "initialize", "commands", "finalize", "text"], "$");
        if (Scalar(root["version"]) != "1") Error("project.version", "$.version", "统一项目 version 必须为 1。");
        if (root["source"] is not JsonObject source) Error("source.required", "$.source", "需要 source 对象指定素材站点。空白场景不需要 adv。");
        else
        {
            Keys(source, ["adv", "site", "language"], "$.source");
            if (Source.IsLibrary && Sections.Any(section => root[section] is not JsonArray))
                Error("source.sections", "$.source", "空白场景必须明确提供开场、正文和结束数组，不能继承不存在的原剧情。");
            if (source["adv"] is {} advValue && (advValue is not JsonValue av || !av.TryGetValue<string>(out _)))
                Error("source.adv", "$.source.adv", "原剧情编号必须为字符串，空白场景请省略 adv。");
            foreach (string key in new[] { "site", "language" })
                if (source[key] is { } value && (value is not JsonValue v || !v.TryGetValue<string>(out string? s) || string.IsNullOrWhiteSpace(s)))
                    Error("source.value", "$.source." + key, key + " 必须是非空字符串。");
        }
        if (root["output"] is not null && root["output"] is not JsonObject)
            Error("output.object", "$.output", "output 必须是对象。");
        else if (root["output"] is JsonObject output)
        {
            Keys(output, ["width", "height", "supersample", "codec", "cineformQuality", "clean", "letterbox", "isolateActor", "playbackViewport"], "$.output");
            foreach (string key in new[] { "width", "height", "supersample" })
                if (output[key] is { } value && (!TryInteger(value, out int n) || n < (key == "supersample" ? 1 : 16) || n > (key == "supersample" ? 4 : RenderDimensions.MaxSide)))
                    Error("output.integer", "$.output." + key, key + " 超出范围或不是整数。");
            if (output["isolateActor"] is {} isolated && (isolated is not JsonValue iv || !iv.TryGetValue<string>(out var target) || string.IsNullOrWhiteSpace(target)))
                Error("output.actor", "$.output.isolateActor", "独立素材必须指定角色标识。");
            try { ActorIsolation.ValidateViewport(output["playbackViewport"]); }
            catch (ArgumentException error) { Error("output.playbackViewport", "$.output.playbackViewport", error.Message); }
            if (output["clean"] is { } clean && (clean is not JsonValue boolean || !boolean.TryGetValue<bool>(out _)))
                Error("output.boolean", "$.output.clean", "clean 必须为布尔值。");
            try { if (output["codec"] is not null) _ = VideoEncoding.NormalizeCodec(Scalar(output["codec"])); }
            catch (ArgumentException error) { Error("output.codec", "$.output.codec", error.Message); }
            try { if (output["cineformQuality"] is not null) _ = VideoEncoding.NormalizeCineFormQuality(Scalar(output["cineformQuality"])); }
            catch (ArgumentException error) { Error("output.quality", "$.output.cineformQuality", error.Message); }
            if (output["letterbox"] is not null && Scalar(output["letterbox"]) is not ("game" or "none"))
                Error("output.letterbox", "$.output.letterbox", "letterbox 必须为 game 或 none。");
            if (!issues.Any(issue => issue.Path.StartsWith("$.output", StringComparison.Ordinal)))
            {
                var current = Output;
                try { RenderDimensions.Validate(current.Width, current.Height, current.Supersample); }
                catch (ArgumentException error) { Error("output.encoding-size", "$.output", error.Message); }
            }
        }
        foreach (string section in Sections)
        {
            if (root[section] is null) continue;
            if (root[section] is not JsonArray rows) { Error("section.array", "$." + section, section + " 必须是指令数组；省略时继承原剧情，[] 明确清空。"); continue; }
            for (int index = 0; index < rows.Count; index++)
            {
                string path = $"$.{section}[{index}]";
                if (rows[index] is not JsonObject row) { Error("row.object", path, "指令行必须是对象。"); continue; }
                try
                {
                    var command = AdvProgram.Row(row, path);
                    DirectorValidation.Check(command);
                    if (!command.Ignored) _ = AdvPlayback.FixedDelayFrames(command);
                    if (command.Command == "Director.Actor" && !command.Ignored && row["Values"] is JsonObject actorValues)
                        ValidatePositionSpace(actorValues, Scalar(row["Space"]));
                    if (command.Command == "Director.Scene" && !command.Ignored && row["Values"] is JsonObject values)
                        ValidateImage(values["backgroundImage"], path + ".Values.backgroundImage", Error);
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or InvalidDataException or InvalidOperationException or FormatException or OverflowException)
                { Error("row.invalid", path, error.Message); }
            }
        }
        if (root["text"] is not null && root["text"] is not JsonObject) Error("text.object", "$.text", "text 必须为对象。");
    }

    static void ValidateImage(JsonNode? value, string path, Action<string, string, string> error)
    {
        if (value is null) return;
        if (value is not JsonObject image) { error("image.object", path, "背景图片必须为对象。"); return; }
        if (image["path"] is { } file)
        {
            if (file is not JsonValue v || !v.TryGetValue<string>(out string? s) || string.IsNullOrWhiteSpace(s))
                error("image.path", path + ".path", "本地背景图片 path 必须为非空字符串，并相对场景文件所在目录解析。");
        }
        else if (string.IsNullOrWhiteSpace(Scalar(image["adv"])) || string.IsNullOrWhiteSpace(Scalar(image["logicalPath"])))
            error("image.reference", path, "背景图片需要 path，或 adv 与 logicalPath。");
    }

    void PatchDirector(SceneRowLocation location, string command, JsonObject values, string? space,
        double? duration, string? ease, bool? isNoWait)
    {
        EnsureUnified();
        Mutate(copy =>
        {
            CheckSection(location.Section);
            var row = ArrayRow(copy, location);
            if (CommandName(row) != command) throw new ArgumentException("此位置不是 " + command + " 指令。", nameof(location));
            ValidatePatch(command, Scalar(row["TargetName"]), values, space ?? Scalar(row["Space"]), duration, ease, isNoWait);
            Merge(ObjectField(row, "Values"), values);
            if (space is not null) row["Space"] = space;
            if (duration is not null) row["Duration"] = duration.Value;
            if (ease is not null) row["Ease"] = ease;
            if (isNoWait is not null) row["IsNoWait"] = isNoWait.Value ? 1 : 0;
        });
    }

    static void ValidatePatch(string command, string? target, JsonObject values, string? space,
        double? duration, string? ease, bool? isNoWait)
    {
        var row = new JsonObject { ["cmd"] = command, ["Values"] = values.DeepClone() };
        if (target is not null) row["TargetName"] = target;
        if (space is not null) row["Space"] = space;
        if (duration is not null)
        {
            if (!double.IsFinite(duration.Value)) throw new ArgumentException("Duration 必须为有限数值。", nameof(duration));
            row["Duration"] = duration.Value;
        }
        if (ease is not null) row["Ease"] = ease;
        if (isNoWait is not null) row["IsNoWait"] = isNoWait.Value ? 1 : 0;
        DirectorValidation.Check(AdvProgram.Row(row, "edit"));
        if (command == "Director.Actor") ValidatePositionSpace(values, space);
    }

    static void ValidatePositionSpace(JsonObject values, string? space)
    {
        if (values["position"] is not JsonArray position) return;
        if (space == "screen" && position.Count != 2 || space == "world" && position.Count != 3)
            throw new ArgumentException("screen 的 position 需要 [x,y]；world 的 position 需要 [x,y,z]。省略 Space 时沿用运行中的角色模式。");
    }

    SceneRowLocation Append(string section, JsonObject row)
    {
        CheckSection(section);
        if (root[section] is not JsonArray original)
            throw new InvalidOperationException($"{section} 当前继承原剧情。请先载入完整原段，或明确用 ReplaceSection 替换该段，再追加指令。");
        var location = new SceneRowLocation(section, original.Count);
        Mutate(copy => ((JsonArray)copy[section]!).Add(row.DeepClone()));
        return location;
    }

    static void MergeLegacyActor(JsonObject actor, JsonObject values)
    {
        ValidatePatch("Director.Actor", Scalar(actor["name"], "actor"), values, "screen", null, null, null);
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "position":
                    if (value is not JsonArray { Count: 2 } position) throw new NotSupportedException("旧 Shot position 只支持二维坐标。");
                    actor["x"] = position[0]?.DeepClone(); actor["y"] = position[1]?.DeepClone(); break;
                case "rotation":
                    if (value is not JsonArray { Count: 3 } rotation || !IsZero(rotation[0]) || !IsZero(rotation[1]))
                        throw new NotSupportedException("旧 Shot 仅支持 Z 轴旋转。");
                    actor["rotation"] = rotation[2]?.DeepClone(); break;
                case "positionX": actor["x"] = value?.DeepClone(); break;
                case "positionY": actor["y"] = value?.DeepClone(); break;
                case "rotationZ": actor["rotation"] = value?.DeepClone(); break;
                case "height": case "opacity": case "brightness": case "tint": case "speed":
                case "loop": case "physics": case "breath": case "blink": actor[key] = value?.DeepClone(); break;
                default: throw new NotSupportedException("旧 Shot 角色不支持此值：" + key + "。完整 tracks/events 保持原样。");
            }
        }
    }

    static void RejectLegacyTiming(double? duration, string? ease, bool? isNoWait)
    {
        if (duration is not null || ease is not null || isNoWait is not null)
            throw new NotSupportedException("旧 Shot 的动画时间由 tracks/events 表达；该编辑只更新基础姿势，不改写轨道。");
    }

    void Mutate(Action<JsonObject> mutation)
    {
        var copy = (JsonObject)root.DeepClone();
        mutation(copy);
        string text = Serialize(copy);
        root = copy;
        Text = text;
    }
    void EnsureKnown() { if (Kind == SceneDocumentKind.Unknown) throw new InvalidOperationException("请先在 JSON 中指定受支持的场景格式。"); }
    void EnsureUnified() { if (Kind != SceneDocumentKind.UnifiedProject) throw new NotSupportedException("此操作需要统一项目格式；旧 Shot 的完整 JSON 仍可编辑。"); }
    static void CheckSection(string section) { if (!Sections.Contains(section, StringComparer.Ordinal)) throw new ArgumentException("section 必须为 initialize、commands 或 finalize。", nameof(section)); }
    static void CheckRange(int value, int min, int max, string name) { if (value < min || value > max) throw new ArgumentOutOfRangeException(name, $"{name} 必须为 {min}..{max}。"); }
    static JsonObject ObjectField(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject obj) return obj;
        if (parent[key] is not null) throw new InvalidOperationException(key + " 不是对象；请在 JSON 中修正，避免丢失原值。");
        var value = new JsonObject(); parent[key] = value; return value;
    }
    static JsonObject ArrayRow(JsonObject document, SceneRowLocation location)
    {
        if (document[location.Section] is not JsonArray array || location.Index < 0 || location.Index >= array.Count || array[location.Index] is not JsonObject row)
            throw new ArgumentOutOfRangeException(nameof(location), "指令位置不存在或不是对象；请重新读取当前行列表。");
        return row;
    }
    static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var pair in patch)
        {
            if (pair.Value is JsonObject child && target[pair.Key] is JsonObject existing) Merge(existing, child);
            else target[pair.Key] = pair.Value?.DeepClone();
        }
    }
    static IEnumerable<(SceneRowLocation Location, JsonObject Row)> Rows(JsonObject document)
    {
        foreach (string section in Sections)
            if (document[section] is JsonArray rows)
                for (int index = 0; index < rows.Count; index++)
                    if (rows[index] is JsonObject row) yield return (new(section, index), row);
    }
    static string CommandName(JsonObject row)
    {
        string name = Scalar(row["cmd"]);
        if (name.Length > 0) return name;
        var number = row["Command"] ?? row["raw"];
        return TryInteger(number, out int index) && index >= 0 && index < AdvProgram.Names.Length ? AdvProgram.Names[index] ?? "" : "";
    }
    static bool RewriteImagePaths(JsonObject document, Func<string, string> transform)
    {
        bool changed = false;
        void Image(JsonNode? node)
        {
            if (node is not JsonObject image || image["path"] is not JsonValue value || !value.TryGetValue<string>(out string? path) || string.IsNullOrWhiteSpace(path)) return;
            string rewritten = transform(path);
            if (path == rewritten) return;
            image["path"] = rewritten; changed = true;
        }
        if (document["format"] is null && document["actors"] is JsonArray) Image(document["backgroundImage"]);
        else if (Scalar(document["format"]) == DirectorProject.Format)
            foreach (var pair in Rows(document))
                if (CommandName(pair.Row) == "Director.Scene" && pair.Row["Values"] is JsonObject values) Image(values["backgroundImage"]);
        return changed;
    }
    static string Scalar(JsonNode? value, string fallback = "") => value is JsonValue ? value.ToString() : fallback;
    static bool TryInteger(JsonNode? value, out int number) => int.TryParse(Scalar(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    static int Integer(JsonNode? value, int fallback) => TryInteger(value, out int number) ? number : fallback;
    static bool Boolean(JsonNode? value, bool fallback) => value is JsonValue boolean && boolean.TryGetValue<bool>(out bool b) ? b : fallback;
    static JsonNode CloneOr(JsonNode? value, double fallback) => value?.DeepClone() ?? JsonValue.Create(fallback)!;
    static bool IsZero(JsonNode? value) => double.TryParse(Scalar(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number == 0;
    static string Serialize(JsonObject value, bool indented = true) => value.ToJsonString(indented ? Pretty : Compact);
    static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    static int FirstContent(string text) { int i = 0; while (i < text.Length && char.IsWhiteSpace(text[i])) i++; return i; }
    static (string Payload, int Start) Unwrap(string text)
    {
        int first = 0;
        while (first < text.Length && (char.IsWhiteSpace(text[first]) || text[first] == '\ufeff')) first++;
        if (first == text.Length) throw Failure(text, first, "请输入场景 JSON 对象。");
        string fence = text.AsSpan(first).StartsWith("```", StringComparison.Ordinal) ? "```"
            : text.AsSpan(first).StartsWith("~~~", StringComparison.Ordinal) ? "~~~" : "";
        if (fence.Length == 0)
        {
            int bom = text.IndexOf('\ufeff');
            return bom >= 0 && bom < first ? (text[(bom + 1)..], bom + 1) : (text, 0);
        }
        int lineEnd = text.IndexOf('\n', first);
        if (lineEnd < 0) throw Failure(text, first, "JSON 代码围栏需要独立的起止行。");
        string header = text[first..lineEnd].Trim();
        if (header != fence && !header.Equals(fence + "json", StringComparison.OrdinalIgnoreCase))
            throw Failure(text, first, "仅支持 JSON 或不标注语言的 Markdown 代码围栏。");
        int end = text.Length;
        while (end > lineEnd && char.IsWhiteSpace(text[end - 1])) end--;
        int lastLine = text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        if (lastLine <= lineEnd || text[lastLine..end].Trim() != fence)
            throw Failure(text, end, "缺少 Markdown JSON 代码围栏的结束行。");
        return (text[(lineEnd + 1)..lastLine], lineEnd + 1);
    }
    static SceneDocumentParseException Failure(string text, int offset, string message)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1, lineStart = 0;
        for (int index = 0; index < offset; index++) if (text[index] == '\n') { line++; lineStart = index + 1; }
        return new(new(message, line, offset - lineStart + 1, offset));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Shot))]
internal partial class SceneEditorJsonContext : JsonSerializerContext;
