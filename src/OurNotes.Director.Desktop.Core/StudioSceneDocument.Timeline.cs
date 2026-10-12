using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class StudioSceneDocument
{
    const string Marker = "ournotes-studio/1/";
    static readonly string[] Sections = ["initialize", "commands", "finalize"];
    readonly List<OriginalRow> originals = new();
    readonly List<TimelineKey> keys = new();
    int durationFrames = FramesPerSecond * 3;
    bool timingKnown;
    string? timingMessage;
    long nextOrder;
    StudioSceneSample? cachedSample;

    sealed class OriginalRow(string id, string section, int index, string command, JsonObject data, int frame, int endFrame, long order, bool known, bool afterTweens = false)
    {
        public string Id = id, Section = section, Command = command;
        public int Index = index, Frame = frame, EndFrame = endFrame;
        public long Order = order;
        public JsonObject Data = data;
        public bool Known = known;
        public bool StartsAfterTweens = afterTweens;
        public bool IsDelay => Command == "Delay";
    }
    sealed class TimelineKey(string id, string target, string track, int frame, string ease, JsonObject row)
    {
        public string Id = id, Target = target, Track = track, Ease = ease;
        public int Frame = frame;
        public JsonObject Row = row;
        public bool IsEvent => Track is "motion" or "expression";
        public string Property => Track.Contains('.') ? Track[(Track.IndexOf('.') + 1)..] : Track;
        public JsonNode? Value => IsEvent ? Row[Track == "motion" ? "MotionName" : "ExpressionName"] : GetPath(Row["Values"], Property);
        public bool Numeric => !IsEvent && NumericNode(Value) && IsTweenProperty(Track.Split('.')[0] switch
            { "actor" => "Director.Actor", "camera" => "Director.Camera", "scene" => "Director.Scene", _ => "" }, Property);
    }

    public bool CanEditTimeline => timingKnown;
    public bool TimingKnown => timingKnown;
    public string? TimingMessage => timingMessage;
    public IReadOnlyList<StudioRawCommand> RawCommands => originals.Select(row => new StudioRawCommand(row.Id, row.Section, row.Index,
        row.Command, row.Known ? row.Frame / (double)FramesPerSecond : null,
        row.Known ? (row.EndFrame - row.Frame) / (double)FramesPerSecond : null, (JsonObject)row.Data.DeepClone())).ToArray();
    public IReadOnlyList<StudioKeyframe> Keyframes => keys.Select(key =>
    {
        int previous = keys.Where(other => other.Target == key.Target && other.Track == key.Track && other.Frame < key.Frame)
            .Select(other => other.Frame).DefaultIfEmpty(0).Max();
        return new StudioKeyframe(key.Id, key.Target, key.Track, key.Frame / (double)FramesPerSecond,
            key.Numeric && key.Ease != "Step" ? (key.Frame - previous) / (double)FramesPerSecond : 0,
            key.Ease, KeyLabel(key.Track), key.IsEvent ? (JsonObject)key.Row.DeepClone() : (JsonObject)key.Row["Values"]!.DeepClone(), key.IsEvent || !key.Numeric);
    }).Concat(originals.Where(IsOriginalKey).Select(row => new StudioKeyframe(row.Id, Target(row.Data), Category(row.Command),
        row.EndFrame / (double)FramesPerSecond, (row.EndFrame - row.Frame) / (double)FramesPerSecond,
        Text(row.Data["Ease"], "Linear"), "原指令 · " + row.Command,
        row.Data["Values"] is JsonObject values ? (JsonObject)values.DeepClone() : (JsonObject)row.Data.DeepClone(), row.Command is "Motion" or "Expression")))
        .OrderBy(key => key.Time).ThenBy(key => key.ObjectId, StringComparer.Ordinal).ThenBy(key => key.Track, StringComparer.Ordinal).ToArray();

    void Reload()
    {
        originals.Clear(); keys.Clear(); nextOrder = 0; InvalidateSamples();
        var root = document.Root;
        timingKnown = document.Kind == SceneDocumentKind.UnifiedProject;
        timingMessage = timingKnown ? null : "此格式保留完整 JSON；可通过原生兼容入口播放与导出，视觉时间轴需要统一项目。";
        int cursor = 0, last = 0, declared = 0; bool cursorKnown = timingKnown, afterTweens = false;
        foreach (string section in Sections)
        {
            if (root[section] is not JsonArray rows)
            {
                timingKnown = cursorKnown = false;
                timingMessage ??= "省略的指令段沿用原剧情；读取归档中的原指令后才能安全编辑绝对时间，当前 JSON 将完整保留。";
                continue;
            }
            for (int index = 0; index < rows.Count; index++)
            {
                if (rows[index] is not JsonObject actual) { timingKnown = cursorKnown = false; timingMessage = "指令段包含非对象数据，请先修正高级 JSON。"; continue; }
                var row = (JsonObject)actual.DeepClone(); string command = Command(row);
                bool ignored = section == "commands" && Ignored(row);
                int span = 0; bool known = ignored || KnownTiming(row, command, out span);
                if (section != "commands" && span > 0) known = false;
                int start = cursor; bool rowAfterTweens = afterTweens;
                if (!known)
                {
                    timingKnown = cursorKnown = false;
                    timingMessage ??= "原剧情包含由资源或交互决定时长的指令（例如 Talk、Wait、视频）。原始轨道会完整保留；请使用原生预览确认时序。";
                }
                bool tween = span > 0 && command.StartsWith("Director.", StringComparison.Ordinal);
                int endFrame = checked(start + span - (tween && start > 0 && !afterTweens ? 1 : 0));
                if (cursorKnown && tween && afterTweens)
                {
                    timingKnown = false;
                    timingMessage = "连续原始补间依赖原生帧内执行阶段。为了保持逐帧结果，当前视觉轨道只读；原始 JSON 可照常编辑、预览与导出。";
                }
                if (command == "Delay" && cursorKnown && !ignored)
                {
                    cursor = checked(cursor + span);
                    if (span > 0) afterTweens = false;
                }
                else if (cursorKnown && !ignored && !Flag(row["IsNoWait"]))
                {
                    cursor = endFrame;
                    if (tween) afterTweens = true;
                }
                last = Math.Max(last, cursorKnown ? Math.Max(cursor, endFrame) : 0);
                string key = Text(row["Key"]);
                if (key.StartsWith(Marker + "wait/", StringComparison.Ordinal) && command == "Delay" && row.Count == 3 && row.ContainsKey("Frames")) continue;
                if (key.StartsWith(Marker + "bake/", StringComparison.Ordinal))
                {
                    if (key[(key.LastIndexOf('/') + 1)..] == RowDigest(row)) continue;
                    timingKnown = false;
                    timingMessage = "高级 JSON 修改了时间轴生成的中间帧。为保留这些修改，视觉轨道暂为只读；可继续原生预览与导出，或撤销该修改后编辑关键帧。";
                }
                if (key.StartsWith(Marker + "duration/", StringComparison.Ordinal)
                    && command == "Delay" && row.Count == 3 && Number(row["Frames"], -1) == 0
                    && int.TryParse(key[(Marker.Length + 9)..], NumberStyles.None, CultureInfo.InvariantCulture, out int end) && end is >= 1 and <= 1080000)
                { declared = Math.Max(declared, cursorKnown ? cursor : end); continue; }
                if (TryReadKey(row, start, cursorKnown, out var parsed)) { keys.Add(parsed!); last = Math.Max(last, parsed!.Frame); continue; }
                var original = RestoreTiming(row);
                int originalSpan = ignored ? 0 : KnownTiming(original, command, out int value) ? value : span;
                int originalEnd = start + originalSpan - (originalSpan > 0 && command.StartsWith("Director.", StringComparison.Ordinal) && start > 0 && !rowAfterTweens ? 1 : 0);
                originals.Add(new(NewId(), section, index, command, original, start, originalEnd, nextOrder++, cursorKnown && known, rowAfterTweens));
            }
        }
        durationFrames = Math.Clamp(declared > 0 ? declared : last > 0 ? last : FramesPerSecond * 3, 1, 1080000);
        if (!timingKnown && document.Kind == SceneDocumentKind.LegacyShot && Number(root["duration"], 3) is double legacyDuration)
            durationFrames = ToFrame(Math.Clamp(legacyDuration, 1d / FramesPerSecond, 36000), positive: true);
        frame = Math.Min(frame, durationFrames);
    }

    static bool KnownTiming(JsonObject row, string command, out int span)
    {
        span = 0;
        try
        {
            if (command == "Delay")
            {
                var native = AdvProgram.Row(row, "studio timing");
                span = AdvPlayback.FixedDelayFrames(native) ?? DelayFrameCount(native.Number("Duration")); return true;
            }
            if (command is "Character" or "Motion" or "Expression" or "Pause" or "Resume" or "Costume") return true;
            if (command == "Stage" && Number(row["Duration"]) == 0) return true;
            if (!command.StartsWith("Director.", StringComparison.Ordinal)) return false;
            DirectorValidation.Check(AdvProgram.Row(row, "studio timing"));
            if (command == "Director.Parameter" && Text(row["Operation"]) == "release") return true;
            bool tween = command == "Director.Parameter" || row["Values"] is JsonObject values
                && Leaves(values).Any(pair => NumericNode(pair.Value) && IsTweenProperty(command, pair.Path));
            if (tween) span = TweenFrameCount((float)Number(row["Duration"]));
            return true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or NotSupportedException or OverflowException) { return false; }
    }
    static int DelayFrameCount(float duration)
    {
        if (!float.IsFinite(duration) || duration < 0 || duration > 36000) throw new ArgumentException("Invalid delay duration");
        int frames = 0;
        while (duration > 0) { duration -= 1f / FramesPerSecond; if (++frames > 1200000) throw new ArgumentException("Delay exceeds timeline limit"); }
        return frames;
    }
    static int TweenFrameCount(float duration)
    {
        if (!float.IsFinite(duration) || duration < 0 || duration > 36000) throw new ArgumentException("Invalid tween duration");
        float elapsed = 0; int frames = 0;
        while (elapsed < duration) { elapsed += 1f / FramesPerSecond; if (++frames > 1200000) throw new ArgumentException("Tween exceeds timeline limit"); }
        return frames;
    }
    void EnsureTimeline()
    {
        if (!timingKnown) throw new InvalidOperationException(timingMessage ?? "请先载入可确定时序的统一场景。");
    }
    void AddOriginal(JsonObject row, int at)
    {
        string command = Command(row); KnownTiming(row, command, out int span);
        originals.Add(new(NewId(), "commands", originals.Count, command, row, at, at + span, nextOrder++, true));
    }

    void Compile()
    {
        EnsureTimeline();
        InvalidateSamples();
        var root = document.Root;
        foreach (string section in new[] { "initialize", "finalize" })
            root[section] = new JsonArray(originals.Where(row => row.Section == section).OrderBy(row => row.Index)
                .Select(row => (JsonNode?)row.Data.DeepClone()).ToArray());
        var scheduled = new List<(int Frame, long Order, JsonObject Row)>();
        foreach (var row in originals.Where(row => row.Section == "commands"))
            scheduled.Add((row.Frame, row.Order, PrepareTiming(row)));
        var numeric = keys.Where(key => key.Numeric).GroupBy(key => (key.Target, key.Track)).ToArray();
        var baseState = EvaluateOriginal(0, includeEvents: false);
        var baked = new Dictionary<(int Frame, string Target, string Category), JsonObject>();
        foreach (var group in numeric)
        {
            var track = group.OrderBy(key => key.Frame).ToArray(); var first = track[0];
            JsonNode baseline = PropertyValue(baseState, first.Target, first.Property)?.DeepClone() ?? first.Value!.DeepClone();
            var candidateFrames = new SortedSet<int>();
            int previous = 0; JsonNode previousValue = baseline;
            foreach (var key in track)
            {
                if (key.Ease != "Step" && !JsonNode.DeepEquals(previousValue, key.Value))
                    for (int at = previous; at < key.Frame; at++) candidateFrames.Add(at);
                candidateFrames.Add(key.Frame); previous = key.Frame; previousValue = key.Value!;
            }
            // Reassert only where an original row can overwrite this authored property.
            var overwriteFrames = originals.Where(row => row.Frame <= durationFrames && Touches(row.Data, first.Target, first.Property))
                .Select(row => row.Frame).ToHashSet();
            foreach (int at in overwriteFrames) candidateFrames.Add(at);
            JsonNode? lastValue = null;
            foreach (int at in candidateFrames)
            {
                if (at > durationFrames) continue;
                var value = EvaluateTrack(track, baseline, at);
                bool forced = overwriteFrames.Contains(at);
                if (!forced && JsonNode.DeepEquals(value, lastValue)) continue;
                lastValue = value;
                if (track.Any(key => key.Frame == at)) continue; // The editable key row itself writes this endpoint.
                string category = first.Track[..first.Track.IndexOf('.')];
                var address = (at, first.Target, category);
                if (!baked.TryGetValue(address, out var row))
                {
                    row = new JsonObject { ["cmd"] = first.Row["cmd"]!.DeepClone(), ["Values"] = new JsonObject(), ["Duration"] = 0, ["IsNoWait"] = 1 };
                    if (category == "actor") { row["TargetName"] = first.Target; if (first.Row["Space"] is { } space) row["Space"] = space.DeepClone(); }
                    baked.Add(address, row);
                }
                SetPath(row["Values"]!.AsObject(), first.Property, value.DeepClone());
            }
        }
        foreach (var pair in baked)
        {
            pair.Value["Key"] = Marker + "bake/" + pair.Key.Frame.ToString(CultureInfo.InvariantCulture) + "/" + RowDigest(pair.Value);
            scheduled.Add((pair.Key.Frame, long.MaxValue - 2, pair.Value));
        }
        foreach (var key in keys)
        {
            var row = (JsonObject)key.Row.DeepClone();
            row["Duration"] = 0; row["IsNoWait"] = 1;
            if (!key.IsEvent) row["Ease"] = key.Ease == "Step" ? "Linear" : key.Ease;
            row["Key"] = WriteKey(key); scheduled.Add((key.Frame, long.MaxValue - 1, row));
        }
        scheduled.Add((durationFrames, long.MaxValue, new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0,
            ["Key"] = Marker + "duration/" + durationFrames.ToString(CultureInfo.InvariantCulture) }));
        var commands = new JsonArray(); int cursor = 0;
        foreach (var item in scheduled.OrderBy(item => item.Frame).ThenBy(item => item.Order))
        {
            if (item.Frame > cursor)
                commands.Add((JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = item.Frame - cursor,
                    ["Key"] = Marker + "wait/" + cursor.ToString(CultureInfo.InvariantCulture) + "/" + item.Frame.ToString(CultureInfo.InvariantCulture) });
            commands.Add((JsonNode)item.Row); cursor = item.Frame;
        }
        root["commands"] = commands;
        document.ReplaceText(root.ToJsonString(DesktopSettings.JsonOptions)); InvalidateSamples();
        frame = Math.Min(frame, durationFrames);
    }

    // Only scheduling fields are normalized. Their original presence and JSON tokens remain
    // in the allowed diagnostic index field, so source rows can be reconstructed on reopen.
    // Other fields (including local image paths) stay in their normal native locations.
    static JsonObject PrepareTiming(OriginalRow original)
    {
        var row = (JsonObject)original.Data.DeepClone();
        if (Ignored(row)) return row;
        string[] fields = original.IsDelay ? ["i", "Duration", "Frames"]
            : original.Command.StartsWith("Director.", StringComparison.Ordinal) && !Flag(row["IsNoWait"]) && original.EndFrame > original.Frame ? ["i", "IsNoWait"] : [];
        if (fields.Length == 0) return row;
        var metadata = new JsonObject();
        foreach (string field in fields) metadata[field] = new JsonObject { ["present"] = row.ContainsKey(field), ["value"] = row[field]?.DeepClone() };
        row["i"] = Marker + "source/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata.ToJsonString()));
        if (original.IsDelay) { row.Remove("Duration"); row["Frames"] = 0; }
        else row["IsNoWait"] = 1;
        return row;
    }
    static JsonObject RestoreTiming(JsonObject actual)
    {
        string marker = Text(actual["i"]); string prefix = Marker + "source/";
        if (!marker.StartsWith(prefix, StringComparison.Ordinal)) return actual;
        try
        {
            var metadata = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(marker[prefix.Length..])))?.AsObject();
            if (metadata is null || metadata.Any(pair => pair.Key is not ("i" or "IsNoWait" or "Duration" or "Frames"))) return actual;
            if (Command(actual) == "Delay" && (Number(actual["Frames"]) != 0 || Number(actual["Duration"]) != 0)) return actual;
            if (metadata.ContainsKey("IsNoWait") && !Flag(actual["IsNoWait"])) return actual;
            var source = (JsonObject)actual.DeepClone();
            foreach (var (field, value) in metadata)
            {
                if (value is not JsonObject saved || saved["present"] is not JsonValue present || !present.TryGetValue<bool>(out bool exists)) return actual;
                if (exists) source[field] = saved["value"]?.DeepClone(); else source.Remove(field);
            }
            return source;
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or InvalidOperationException) { return actual; }
    }

    static string WriteKey(TimelineKey key) => Marker + "key/" + key.Id + "/" + key.Frame.ToString(CultureInfo.InvariantCulture)
        + "/" + Uri.EscapeDataString(key.Track) + "/" + Uri.EscapeDataString(key.Target) + "/" + Uri.EscapeDataString(key.Ease);
    static string RowDigest(JsonObject row)
    {
        var copy = (JsonObject)row.DeepClone(); copy.Remove("Key");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString())));
    }
    static bool TryReadKey(JsonObject row, int actualFrame, bool known, out TimelineKey? key)
    {
        key = null; string marker = Text(row["Key"]), prefix = Marker + "key/";
        if (!marker.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            string[] parts = marker[prefix.Length..].Split('/');
            if (parts.Length != 5 || parts[0].Length == 0 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int at) || at is < 0 or > 1080000) return false;
            string track = Uri.UnescapeDataString(parts[2]), target = Uri.UnescapeDataString(parts[3]), ease = Uri.UnescapeDataString(parts[4]);
            CheckEase(ease);
            if (Number(row["Duration"]) != 0 || !Flag(row["IsNoWait"])) return false;
            var candidate = new TimelineKey(parts[0], target, track, known ? actualFrame : at, ease, (JsonObject)row.DeepClone());
            if (candidate.Value is null || Target(row) != target) return false;
            if (track.StartsWith("actor.", StringComparison.Ordinal) && Command(row) != "Director.Actor"
                || track.StartsWith("camera.", StringComparison.Ordinal) && Command(row) != "Director.Camera"
                || track.StartsWith("scene.", StringComparison.Ordinal) && Command(row) != "Director.Scene"
                || track == "motion" && Command(row) != "Motion" || track == "expression" && Command(row) != "Expression") return false;
            if (!candidate.IsEvent && track.IndexOf('.') < 0) return false;
            key = candidate; return true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException) { return false; }
    }

    static JsonNode EvaluateTrack(TimelineKey[] track, JsonNode baseline, int at)
    {
        int previousFrame = 0; JsonNode previous = baseline;
        foreach (var key in track)
        {
            if (at >= key.Frame) { previousFrame = key.Frame; previous = key.Value!; continue; }
            if (key.Ease == "Step" || !key.Numeric || key.Frame == previousFrame) return previous.DeepClone();
            float fraction = AdvTweens.Evaluate(Enum.Parse<AdvEase>(key.Ease), (at - previousFrame) / (float)FramesPerSecond,
                (key.Frame - previousFrame) / (float)FramesPerSecond);
            return Interpolate(previous, key.Value!, fraction);
        }
        return previous.DeepClone();
    }
    static JsonNode Interpolate(JsonNode from, JsonNode to, float fraction)
    {
        if (from is JsonArray first && to is JsonArray last && first.Count == last.Count)
            return new JsonArray(first.Select((value, index) => Interpolate(value!, last[index]!, fraction)).Cast<JsonNode?>().ToArray());
        float start = (float)Number(from), target = (float)Number(to);
        return JsonValue.Create(start + (target - start) * fraction)!;
    }
    static void CheckEase(string ease)
    {
        if (ease == "Step") return;
        if (!Enum.TryParse<AdvEase>(ease, out var parsed) || !Enum.IsDefined(parsed) || parsed is >= AdvEase.Flash and <= AdvEase.INTERNAL_Custom)
            throw new ArgumentException("不支持此关键帧插值：" + ease, nameof(ease));
    }
    static bool IsOriginalKey(OriginalRow row) => row.Known && !Ignored(row.Data) && (row.Command is "Motion" or "Expression"
        || row.Command.StartsWith("Director.", StringComparison.Ordinal) && row.EndFrame > row.Frame);
    static string KeyLabel(string track) => track switch
    {
        "motion" => "动作", "expression" => "表情", "actor.position" => "位置", "actor.height" => "高度", "actor.rotationZ" or "actor.rotation" => "旋转",
        "actor.scale" => "缩放", "actor.opacity" => "透明度", "actor.visible" => "显示", "scene.background" => "背景色",
        "scene.grade.exposure" => "曝光", "scene.grade.contrast" => "对比度", "scene.grade.saturation" => "饱和度", "scene.grade.vignette" => "暗角", _ => track
    };
    static string Category(string command) => command switch { "Director.Actor" => "actor", "Director.Camera" => "camera", "Director.Scene" => "scene", "Motion" => "motion", "Expression" => "expression", _ => command };
    static string Target(JsonObject row) => Command(row) switch { "Director.Camera" => CameraId, "Director.Scene" => SceneId, _ => Text(row["TargetName"]) };
    static string Command(JsonObject row)
    {
        try { return AdvProgram.Row(row, "studio").Command; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.InvalidDataException or FormatException)
        { return Text(row["cmd"], "未知指令"); }
    }
    static string NewId() => Guid.NewGuid().ToString("N");
    static bool Flag(JsonNode? value) => value?.ToString() is "1" or "true";
    static bool Ignored(JsonObject row) => Flag(row["IgnoreData"]);
    static string Text(JsonNode? value, string fallback = "") => value is null ? fallback : value.ToString();
    static double Number(JsonNode? value, double fallback = 0) => value is not null
        && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : fallback;
    static bool NumericNode(JsonNode? node) => node is JsonArray array ? array.Count > 0 && array.All(NumericNode)
        : node is JsonValue value && !value.TryGetValue<string>(out _) && !value.TryGetValue<bool>(out _)
            && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number);
    static IEnumerable<(string Path, JsonNode Value)> Leaves(JsonObject obj, string prefix = "")
    {
        foreach (var (key, value) in obj)
        {
            if (value is null) continue;
            string path = prefix + key;
            if (value is JsonObject nested && key is "framing" or "grade")
                foreach (var child in Leaves(nested, path + ".")) yield return child;
            else yield return (path, value);
        }
    }
    static JsonNode? GetPath(JsonNode? root, string path)
    {
        JsonNode? value = root;
        foreach (string field in path.Split('.')) { if (value is not JsonObject obj) return null; value = obj[field]; }
        return value;
    }
    static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        string[] fields = path.Split('.'); JsonObject obj = root;
        for (int index = 0; index < fields.Length - 1; index++)
        {
            if (obj[fields[index]] is not JsonObject nested) obj[fields[index]] = nested = new JsonObject();
            obj = nested;
        }
        obj[fields[^1]] = value;
    }
    static void Merge(JsonObject destination, JsonObject patch)
    {
        foreach (var (key, value) in patch)
            if (value is JsonObject nested && destination[key] is JsonObject old) Merge(old, nested);
            else destination[key] = value?.DeepClone();
    }
    static bool Touches(JsonObject row, string target, string property) => Target(row) == target && row["Values"] is JsonObject values
        && Leaves(values).Any(pair => CanonicalProperty(pair.Path) == CanonicalProperty(property)
            || CanonicalProperty(pair.Path).StartsWith(CanonicalProperty(property) + ".", StringComparison.Ordinal)
            || CanonicalProperty(property).StartsWith(CanonicalProperty(pair.Path) + ".", StringComparison.Ordinal));
    static string CanonicalProperty(string property) => property switch { "positionX" => "position.0", "positionY" => "position.1", "rotationZ" => "rotation.2", _ => property };
}
