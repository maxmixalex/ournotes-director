using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class StudioSceneDocument
{
    const string NativeEditMarker = Marker + "native-edits";
    sealed record NativeRowIdentity(string Id, string Section, int Index, string Fingerprint, JsonNode? Data);
    readonly List<NativeRowIdentity> nativeRows = new();
    Dictionary<string, string>? pendingCommandIds;
    string? selectedCommand;
    string? indexedText;

    public bool CanEditCommands => document.Kind == SceneDocumentKind.UnifiedProject;
    public string? SelectedCommandId
    {
        get => selectedCommand;
        set { if (selectedCommand != value) { selectedCommand = value; SelectionChanged?.Invoke(); } }
    }

    public IReadOnlyList<StudioCommandSection> CommandSections
    {
        get
        {
            var root = document.Root;
            var commands = Commands;
            return Sections.Select(section => new StudioCommandSection(section, root[section] is null,
                commands.Where(command => command.Section == section).ToArray())).ToArray();
        }
    }

    /// <summary>The complete native arrays, including generated rows. Filtering IsGenerated is a presentation choice only.</summary>
    public IReadOnlyList<StudioCommand> Commands
    {
        get
        {
            RefreshCommandIndex();
            var root = document.Root;
            var times = StaticCommandTimes(root);
            var observed = nativeTiming?.Commands.GroupBy(t => (t.Section, t.Index))
                .ToDictionary(group => group.Key, group => group.OrderBy(t => t.Ordinal).First());
            return nativeRows.Select(row =>
            {
                var data = row.Data is JsonObject value ? (JsonObject)value.DeepClone() : new JsonObject();
                string command = NativeCommandName(row.Data);
                var timing = times.GetValueOrDefault((row.Section, row.Index));
                double? start = timing.Start, end = timing.End;
                if (observed?.TryGetValue((row.Section, row.Index), out var execution) == true)
                { start = execution.StartFrame / (double)FramesPerSecond; end = execution.EndFrame / (double?)FramesPerSecond; }
                string key = Text(data["Key"]);
                bool knownCommand = AdvProgram.KnownCommand(command);
                int commandId = Array.IndexOf(AdvProgram.Names, command);
                return new StudioCommand(row.Id, row.Section, row.Index, command, !Ignored(data),
                    CommandSummary(command, data, root), data, start, end, start.HasValue && end.HasValue,
                    IsGeneratedCommand(data))
                {
                    IsKeyframe = key.StartsWith(Marker + "key/", StringComparison.Ordinal),
                    IsObject = row.Data is JsonObject, RawData = row.Data?.DeepClone(),
                    CommandId = commandId >= 0 ? commandId : null, IsKnownCommand = knownCommand
                };
            }).ToArray();
        }
    }

    public IReadOnlyList<StudioCommandFragment> TimelineCommands
    {
        get
        {
            var result = new List<StudioCommandFragment>();
            var observed = nativeTiming?.Commands.GroupBy(t => (t.Section, t.Index)).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Ordinal).ToArray());
            foreach (var command in Commands.Where(command => !command.IsGenerated))
            {
                if (observed?.TryGetValue((command.Section, command.Index), out var events) == true)
                {
                    foreach (var entry in events)
                    {
                        double start = entry.StartFrame / (double)FramesPerSecond;
                        double? end = entry.EndFrame / (double?)FramesPerSecond;
                        result.Add(new(command.Id + "/" + entry.Ordinal.ToString(CultureInfo.InvariantCulture), command.Id,
                            command.Section, command.Index, command.Command, command.Summary, start, end,
                            command.Enabled, end == start, true, entry.Ordinal));
                    }
                }
                else result.Add(new(command.Id, command.Id, command.Section, command.Index, command.Command,
                    command.Summary, command.Start, command.End, command.Enabled,
                    command.End is double end && end == command.Start, false));
            }
            return result;
        }
    }

    public string InsertCommand(string section, int index, JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string id = NewId();
        EditNativeCommands("插入指令", maps =>
        {
            document.InsertCommand(section, index, row);
            maps[section].Insert(index, id); selectedCommand = id;
        });
        return id;
    }

    public void UpdateCommand(string id, JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row); var entry = RequireCommand(id);
        bool valueOnly = entry.Data is JsonObject original && SafeVisualValueEdit(original, row);
        EditNativeCommands("编辑指令", _ => document.UpdateCommand(new(entry.Section, entry.Index), row), valueOnly);
    }

    public void PatchCommand(string id, JsonObject patch)
    {
        ArgumentNullException.ThrowIfNull(patch); var entry = RequireCommand(id);
        var row = entry.Data as JsonObject ?? throw new InvalidDataException("该指令不是对象，请使用逐行 JSON 替换。");
        var changed = (JsonObject)row.DeepClone(); Merge(changed, patch); UpdateCommand(id, changed);
    }

    public void RemoveCommand(string id)
    {
        var entry = RequireCommand(id);
        EditNativeCommands("删除指令", maps =>
        {
            document.RemoveCommand(new(entry.Section, entry.Index)); maps[entry.Section].RemoveAt(entry.Index);
            if (selectedCommand == id) selectedCommand = null;
        });
    }

    public string DuplicateCommand(string id)
    {
        var entry = RequireCommand(id);
        var row = entry.Data as JsonObject ?? throw new InvalidDataException("该指令不是对象，可先修正或删除。");
        string next = NewId();
        EditNativeCommands("复制指令", maps =>
        {
            document.InsertCommand(entry.Section, entry.Index + 1, row);
            maps[entry.Section].Insert(entry.Index + 1, next); selectedCommand = next;
        });
        return next;
    }

    /// <summary>Index is the final index after removing this instruction; commands in other sections are unaffected.</summary>
    public void MoveCommand(string id, string section, int index)
    {
        var entry = RequireCommand(id);
        if (entry.Section == section && entry.Index == index) return;
        EditNativeCommands("移动指令", maps =>
        {
            document.MoveCommand(new(entry.Section, entry.Index), section, index);
            maps[entry.Section].RemoveAt(entry.Index); maps[section].Insert(index, id); selectedCommand = id;
        });
    }

    public void SetCommandEnabled(string id, bool enabled)
    {
        var entry = RequireCommand(id);
        EditNativeCommands(enabled ? "启用指令" : "禁用指令", _ => document.SetCommandEnabled(new(entry.Section, entry.Index), enabled));
    }

    public string GetCommandText(string id, string? language = null)
    {
        var row = RequireCommand(id).Data as JsonObject ?? throw new InvalidDataException("该指令不是对象。");
        return LocalizedCommandText(row, document.Root, language ?? document.Source.Language);
    }

    /// <summary>Forks shared/source text on first edit, then reuses the instruction's private text ID until the instruction is copied.</summary>
    public void SetCommandText(string id, string text, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entry = RequireCommand(id);
        if (entry.Data is not JsonObject original) throw new InvalidDataException("该指令不是对象。");
        string selectedLanguage = language ?? document.Source.Language, field = TextLanguageField(selectedLanguage);
        if (NativeCommandName(original) is not ("Talk" or "Subtitles" or "Location" or "ChatTalk") && !original.ContainsKey("AdvTextID"))
            throw new ArgumentException("该指令不引用 AdvTextID 对白。", nameof(id));
        EditNativeCommands("编辑对白文本", _ =>
        {
            var root = document.Root;
            if (root["text"] is not null && root["text"] is not JsonObject)
                throw new InvalidDataException("项目 text 必须为对象，原文保持不变。");
            var table = root["text"] as JsonObject ?? new JsonObject();
            if (root["text"] is null) root["text"] = table;
            var row = root[entry.Section]!.AsArray()[entry.Index]!.AsObject();
            string oldId = Text(row["AdvTextID"]);
            int references = Sections.Where(section => root[section] is JsonArray)
                .SelectMany(section => root[section]!.AsArray().OfType<JsonObject>()).Count(item => Text(item["AdvTextID"]) == oldId);
            string textId = oldId;
            if (!oldId.StartsWith("studio-text-", StringComparison.Ordinal) || references != 1)
            {
                do { textId = "studio-text-" + NewId(); } while (table.ContainsKey(textId));
                table[textId] = table[oldId] is JsonObject inherited ? inherited.DeepClone() : new JsonObject();
                row["AdvTextID"] = textId;
            }
            if (table[textId] is not JsonObject translations)
                throw new InvalidDataException("已有自定义文本不是语言对象，请先修正该文本条目。");
            translations[field] = text;
            // If an alias is also present, keep it synchronized rather than leaving a stale translation.
            if (translations.ContainsKey(selectedLanguage)) translations[selectedLanguage] = text;
            document.ReplaceText(root.ToJsonString(DesktopSettings.JsonOptions));
        });
    }

    public StudioExpansionResult ExpandInheritedSections(Archive archive)
    {
        ResolveSourceModels(archive);
        StudioExpansionResult result = new(0, 0, 0, 0);
        EditDocument(value => result = value.ExpandInheritedSections(archive), "展开原剧情指令");
        return result;
    }
    public StudioExpansionResult ExpandInheritedSections(JsonObject episode, JsonObject playerSettings)
    {
        StudioExpansionResult result = new(0, 0, 0, 0);
        EditDocument(value => result = value.ExpandInheritedSections(episode, playerSettings), "展开原剧情指令");
        return result;
    }

    public void ReplaceCommandSection(string section, JsonArray? rows)
        => EditNativeCommands(rows is null ? "恢复原剧情继承段" : "替换指令段", maps =>
        {
            document.ReplaceSection(section, rows);
            var previous = nativeRows.Where(row => row.Section == section).GroupBy(row => row.Fingerprint, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new Queue<string>(group.Select(row => row.Id)), StringComparer.Ordinal);
            maps[section] = rows?.Select(row => previous.TryGetValue(row?.ToJsonString() ?? "null", out var matching)
                && matching.TryDequeue(out string? id) ? id : NewId()).ToList() ?? [];
        });

    public void SetOutput(int? width = null, int? height = null, int? supersample = null,
        string? codec = null, string? cineformQuality = null, bool? clean = null, string? letterbox = null)
        => EditDocument(value => value.SetOutput(width, height, supersample, codec, cineformQuality, clean, letterbox), "修改输出设置");

    public void SetSource(string? adv = null, string? site = null, string? language = null)
        => EditDocument(value => value.SetSource(adv, site, language), "修改素材来源");

    void EditNativeCommands(string label, Action<Dictionary<string, List<string>>> mutation, bool safeVisualValueEdit = false)
    {
        if (!CanEditCommands) throw new InvalidOperationException("逐条指令编辑需要统一项目；请先将旧 Shot 转为原生项目。");
        RefreshCommandIndex();
        var maps = Sections.ToDictionary(section => section,
            section => nativeRows.Where(row => row.Section == section).OrderBy(row => row.Index).Select(row => row.Id).ToList());
        Edit(label, () =>
        {
            string previous = document.Text;
            mutation(maps);
            if (document.Text == previous) return;
            // A raw instruction edit is authoritative. Keep existing sparse metadata and cached frames,
            // but prevent the visual compiler from undoing deliberate changes to scheduling/native rows.
            if (!safeVisualValueEdit) ProtectNativeEdits(maps);
            pendingCommandIds = maps.SelectMany(pair => pair.Value.Select((id, index) => (Address: Address(pair.Key, index), Id: id)))
                .ToDictionary(pair => pair.Address, pair => pair.Id, StringComparer.Ordinal);
            RefreshCommandIndex();
            Reload();
        });
    }

    void ProtectNativeEdits(Dictionary<string, List<string>> maps)
    {
        var root = document.Root;
        if (root["commands"] is not JsonArray commands) return;
        bool protectedAlready = commands.OfType<JsonObject>().Any(row => Text(row["Key"]) == NativeEditMarker);
        bool hasTimeline = commands.OfType<JsonObject>().Any(row =>
            Text(row["Key"]).StartsWith(Marker, StringComparison.Ordinal) && !IsPlaybackMarker(row)
            || Text(row["i"]).StartsWith(Marker + "source/", StringComparison.Ordinal));
        bool changed = false;
        // The authoring preset's initial duration is an implicit range. Once scheduling is edited
        // as native instructions, follow the complete program unless the user explicitly fixed it.
        if (!playbackMarkerPresent && durationMode == StudioDurationMode.Fixed)
        {
            commands.Add((JsonNode)PlaybackRow(StudioDurationMode.Automatic, null));
            maps["commands"].Add(NewId()); changed = true;
        }
        if (hasTimeline && !protectedAlready)
        {
            commands.Add((JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0, ["Key"] = NativeEditMarker });
            maps["commands"].Add(NewId()); changed = true;
        }
        if (changed) document.ReplaceText(root.ToJsonString(DesktopSettings.JsonOptions));
    }

    static bool SafeVisualValueEdit(JsonObject before, JsonObject after)
    {
        string command = NativeCommandName(before);
        if (command is not ("Director.Actor" or "Director.Camera" or "Director.Scene" or "Motion" or "Expression")) return false;
        var left = (JsonObject)before.DeepClone(); var right = (JsonObject)after.DeepClone();
        string property = command == "Motion" ? "MotionName" : command == "Expression" ? "ExpressionName" : "Values";
        left.Remove(property); right.Remove(property);
        return JsonNode.DeepEquals(left, right);
    }

    NativeRowIdentity RequireCommand(string id)
    {
        RefreshCommandIndex();
        return nativeRows.FirstOrDefault(row => row.Id == id)
            ?? throw new ArgumentException("指令已不存在，请刷新指令列表。", nameof(id));
    }
    Dictionary<string, string> CaptureCommandIds()
    {
        RefreshCommandIndex();
        return nativeRows.ToDictionary(row => Address(row.Section, row.Index), row => row.Id, StringComparer.Ordinal);
    }

    void RefreshCommandIndex()
    {
        if (indexedText == document.Text && pendingCommandIds is null) return;
        var old = nativeRows.GroupBy(row => row.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<NativeRowIdentity>(group), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var next = new List<NativeRowIdentity>();
        var root = document.Root;
        foreach (string section in Sections)
            if (root[section] is JsonArray rows)
                for (int index = 0; index < rows.Count; index++)
                {
                    string fingerprint = rows[index]?.ToJsonString() ?? "null";
                    string? id = pendingCommandIds?.GetValueOrDefault(Address(section, index));
                    if (id is null && old.TryGetValue(fingerprint, out var matching))
                        while (matching.TryDequeue(out var candidate))
                            if (!used.Contains(candidate.Id)) { id = candidate.Id; break; }
                    if (id is null || !used.Add(id)) { id = NewId(); used.Add(id); }
                    next.Add(new(id, section, index, fingerprint, rows[index]?.DeepClone()));
                }
        nativeRows.Clear(); nativeRows.AddRange(next); pendingCommandIds = null; indexedText = document.Text;
        if (selectedCommand is not null && !nativeRows.Any(row => row.Id == selectedCommand)) selectedCommand = null;
        InvalidateNativeTiming();
    }

    Dictionary<(string Section, int Index), (double? Start, double? End)> StaticCommandTimes(JsonObject root)
    {
        var result = new Dictionary<(string Section, int Index), (double? Start, double? End)>();
        bool known = document.Kind == SceneDocumentKind.UnifiedProject, afterTweens = false;
        int cursor = 0;
        foreach (string section in Sections)
        {
            if (root[section] is not JsonArray rows) { known = false; continue; }
            for (int index = 0; index < rows.Count; index++)
            {
                if (rows[index] is not JsonObject row) { known = false; result[(section, index)] = (null, null); continue; }
                double? start = known ? cursor / (double)FramesPerSecond : null;
                bool ignored = Ignored(row); string command = NativeCommandName(row);
                int span = 0; bool rowKnown = ignored || KnownTiming(row, command, out span);
                if (!known || !rowKnown) { result[(section, index)] = (start, null); known = false; continue; }
                int end;
                try { end = checked(cursor + span - (span > 0 && command.StartsWith("Director.", StringComparison.Ordinal) && cursor > 0 && !afterTweens ? 1 : 0)); }
                catch (OverflowException) { result[(section, index)] = (start, null); known = false; continue; }
                result[(section, index)] = (start, end / (double)FramesPerSecond);
                if (!ignored && command == "Delay") { cursor = end; if (span > 0) afterTweens = false; }
                else if (!ignored && !Flag(row["IsNoWait"])) { cursor = end; if (span > 0 && command.StartsWith("Director.", StringComparison.Ordinal)) afterTweens = true; }
            }
        }
        return result;
    }

    static bool IsGeneratedCommand(JsonObject row)
    {
        string key = Text(row["Key"]);
        return key.StartsWith(Marker, StringComparison.Ordinal) && !key.StartsWith(Marker + "key/", StringComparison.Ordinal);
    }
    static string Address(string section, int index) => section + "/" + index.ToString(CultureInfo.InvariantCulture);
    static string NativeCommandName(JsonNode? data)
    {
        if (data is not JsonObject row) return "（非对象指令）";
        string command = Text(row["cmd"]);
        if (command.Length > 0) return command;
        string number = Text(row["Command"] ?? row["raw"]);
        return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 0 && index < AdvProgram.Names.Length && AdvProgram.Names[index] is string name ? name
            : number.Length > 0 ? "Command " + number : "（未指定指令）";
    }
    string CommandSummary(string command, JsonObject row, JsonObject root)
    {
        var details = new List<string>();
        foreach (string field in new[] { "TargetName", "Model", "TargetAssetName", "MotionName", "ExpressionName", "AdvTextID" })
            if (Text(row[field]) is { Length: > 0 } value) details.Add(value);
        if (command == "Delay") details.Add(row["Frames"] is { } frames ? frames + " 帧" : Text(row["Duration"], "0") + " 秒");
        if (command is "Talk" or "ChatTalk" or "Subtitles" or "Location")
        {
            string dialogue = LocalizedCommandText(row, root, document.Source.Language);
            if (dialogue.Length > 0) details.Add(dialogue.Replace('\r', ' ').Replace('\n', ' '));
        }
        if (details.Count == 0 && row["Parameter1"] is { } parameter) details.Add(parameter.ToString());
        string label = StudioCommandCatalog.All.FirstOrDefault(item => item.Command == command)?.Label ?? command;
        return details.Count == 0 ? label : label + " · " + string.Join(" · ", details);
    }

    static string LocalizedCommandText(JsonObject row, JsonObject root, string language)
    {
        string id = Text(row["AdvTextID"]);
        if (id.Length == 0 || root["text"] is not JsonObject table || table[id] is not JsonObject entry) return "";
        string field;
        try { field = TextLanguageField(language); } catch (ArgumentException) { return Text(entry[language]); }
        return Text(entry[field], Text(entry[language]));
    }
    static string TextLanguageField(string language) => language switch
    {
        "zh-Hans" => "simplifiedChinese", "zh-Hant" => "traditionalChinese", "ja" => "japanese",
        "en" => "english", "ko" => "korean",
        _ => throw new ArgumentException("原生对白语言必须为 zh-Hans、zh-Hant、ja、en 或 ko。", nameof(language))
    };
}
