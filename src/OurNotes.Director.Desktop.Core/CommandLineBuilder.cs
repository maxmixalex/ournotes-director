using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed record CommandContext
{
    public string? ArchivePath { get; init; }
    public string? Site { get; init; }
    public string? Language { get; init; }
    public string? OutputDirectory { get; init; }
    public string? CubismSdkPath { get; init; }
    public string? NativeLibraryPath { get; init; }
    public string? FFmpegPath { get; init; }
    public string? FFprobePath { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? SourceWidth { get; init; }
    public int? SourceHeight { get; init; }
    public string? SourceCodec { get; init; }
    public bool? SourceHasAlpha { get; init; }
}

public sealed record CommandValidationError(string FieldKey, string Message);

public sealed record CommandBuildResult(
    IReadOnlyList<string> Arguments,
    IReadOnlyList<CommandValidationError> Errors,
    string? OutputPath,
    CommandOutputKind OutputKind)
{
    public bool IsValid => Errors.Count == 0;
}

public static class CommandLineBuilder
{
    public static CommandBuildResult Build(string commandId, IReadOnlyDictionary<string, string?> values,
        CommandContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        context ??= new();
        CommandDefinition command;
        try { command = CommandCatalog.Get(commandId); }
        catch (ArgumentException exception) { return new([], [new("", exception.Message)], null, CommandOutputKind.None); }

        var errors = new List<CommandValidationError>();
        var fields = command.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var aliases = command.Fields.SelectMany(field => field.Aliases.Select(alias => (alias, field.Key)))
            .ToDictionary(item => item.alias, item => item.Key, StringComparer.Ordinal);
        var supplied = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            string key = aliases.GetValueOrDefault(pair.Key, pair.Key);
            if (!fields.ContainsKey(key)) { errors.Add(new(pair.Key, "此操作不支持设置“" + pair.Key + "”。")); continue; }
            if (!supplied.TryAdd(key, pair.Value)) errors.Add(new(key, "同一选项及其别名不能同时设置。"));
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        string? workingDirectory;
        try { workingDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(context.WorkingDirectory) ? Environment.CurrentDirectory : context.WorkingDirectory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add(new("", "工作目录无效：" + exception.Message));
            workingDirectory = Environment.CurrentDirectory;
        }

        foreach (var field in command.Fields)
        {
            string? raw = supplied.TryGetValue(field.Key, out string? explicitValue)
                ? explicitValue : Inherited(command, field.Key, context, supplied);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string value = field.Kind == CommandFieldKind.Text ? raw : raw.Trim();
            if (value.IndexOf('\0') >= 0) { errors.Add(new(field.Key, field.Label + "不能包含空字符。")); continue; }
            if (field.Key is "codec" or "cineform-quality") value = value.ToLowerInvariant();
            if (field.Key == "codec" && value == "cfhd") value = "cineform";

            if (field.Kind == CommandFieldKind.Boolean)
            {
                if (!bool.TryParse(value, out bool flag)) { errors.Add(new(field.Key, field.Label + "必须为开或关。")); continue; }
                value = flag ? "true" : "false";
            }
            else if (field.Kind is CommandFieldKind.Integer or CommandFieldKind.Number)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)
                    || field.Kind == CommandFieldKind.Integer && (number != Math.Truncate(number) || number > int.MaxValue || number < int.MinValue))
                { errors.Add(new(field.Key, field.Label + (field.Kind == CommandFieldKind.Integer ? "必须为整数。" : "必须为有限数字，使用小数点。"))); continue; }
                if (field.Minimum is { } minimum && (number < minimum || field.MinimumExclusive && number == minimum))
                    errors.Add(new(field.Key, field.Label + "必须" + (field.MinimumExclusive ? "大于 " : "不少于 ") + minimum.ToString(CultureInfo.InvariantCulture) + "。"));
                if (field.Maximum is { } maximum && number > maximum)
                    errors.Add(new(field.Key, field.Label + "不能超过 " + maximum.ToString(CultureInfo.InvariantCulture) + "。"));
                value = number.ToString("R", CultureInfo.InvariantCulture);
            }
            else if (field.Kind is CommandFieldKind.Choice or CommandFieldKind.MultiChoice)
            {
                var choices = field.Kind == CommandFieldKind.MultiChoice
                    ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray() : [value];
                if (choices.Length == 0) errors.Add(new(field.Key, "请选择" + field.Label + "。"));
                if (!field.AllowCustomValue)
                    foreach (string item in choices)
                        if (!field.Choices.Any(choice => choice.Value == item)) errors.Add(new(field.Key, field.Label + "不支持“" + item + "”。"));
                value = string.Join(',', choices);
            }

            if (IsPath(field.Kind))
            {
                // Bare media executable names retain NativeDependencies' PATH lookup semantics.
                bool bareTool = field.Key is "ffmpeg" or "ffprobe" && !Path.IsPathRooted(value)
                    && value.IndexOfAny(['/', '\\']) < 0;
                if (!bareTool)
                {
                    try { value = Path.GetFullPath(value, workingDirectory); }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    { errors.Add(new(field.Key, field.Label + "路径无效：" + exception.Message)); continue; }
                    if (field.Kind == CommandFieldKind.InputFile && !File.Exists(value)) errors.Add(new(field.Key, field.Label + "不存在：" + value));
                    if (field.Kind == CommandFieldKind.InputDirectory && !Directory.Exists(value)) errors.Add(new(field.Key, field.Label + "不存在：" + value));
                }
                if (field.FileExtensions.Count > 0 && !field.FileExtensions.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase))
                    errors.Add(new(field.Key, field.Label + "支持的扩展名为 " + string.Join("、", field.FileExtensions) + "。"));
            }
            else if (value.StartsWith('-'))
                errors.Add(new(field.Key, field.Label + "不能以减号开头；当前命令行不支持这种参数值。"));

            resolved[field.Key] = value;
        }

        // Reading only local JSON lets validation respect project settings without loading an archive,
        // graphics driver or native SDK. The CLI remains authoritative for full project validation.
        var project = ReadProject(command, resolved, errors);
        if (commandId.StartsWith("home.", StringComparison.Ordinal) && project.SourceSpot is { Length: > 0 } homeSpot)
        {
            if (resolved.TryGetValue("spotId", out var selectedSpot) && selectedSpot != homeSpot)
                errors.Add(new("spotId", "主页项目的编号为 " + homeSpot + "，与所选主页不一致。"));
            else resolved["spotId"] = homeSpot;
            if (supplied.TryGetValue("site", out var selectedSite) && !string.IsNullOrWhiteSpace(selectedSite) && selectedSite.Trim() != project.SourceSite)
                errors.Add(new("site", "主页项目的来源站点为 " + project.SourceSite + "，请选择相同站点。"));
            else if (project.SourceSite is not null) resolved["site"] = project.SourceSite;
            if (project.SourceAdv is not null)
            {
                if (resolved.TryGetValue("adv", out var selectedAdv) && selectedAdv != project.SourceAdv)
                    errors.Add(new("adv", "主页项目的来源剧情为 " + project.SourceAdv + "，与所选来源不一致。"));
                else resolved["adv"] = project.SourceAdv;
            }
        }
        if (commandId.StartsWith("story.", StringComparison.Ordinal) && project.SourceSite is { Length: > 0 } sourceSite)
        {
            // A script's declared site identifies its assets. A global UI default must not
            // silently redirect a Japanese project to the international archive.
            if (supplied.TryGetValue("site", out string? chosenSite) && !string.IsNullOrWhiteSpace(chosenSite) && chosenSite.Trim() != sourceSite)
                errors.Add(new("site", "指令项目的来源站点为 " + sourceSite + "，请选择相同站点。"));
            else resolved["site"] = sourceSite;
            if (fields.ContainsKey("lang") && (!supplied.TryGetValue("lang", out string? chosenLanguage) || string.IsNullOrWhiteSpace(chosenLanguage))
                && project.SourceLanguage is { Length: > 0 } sourceLanguage) resolved["lang"] = sourceLanguage;
        }
        string Effective(string key, string fallback = "")
        {
            if (resolved.TryGetValue(key, out string? selected) && selected.Length > 0) return selected;
            if ((commandId.StartsWith("render.", StringComparison.Ordinal) || commandId.StartsWith("home.", StringComparison.Ordinal)) && project.Defaults.TryGetValue(key, out string? inherited)) return inherited;
            return fields.GetValueOrDefault(key)?.DefaultValue ?? fallback;
        }

        string codec = Effective("codec", commandId.StartsWith("home.", StringComparison.Ordinal) ? "cineform" : commandId == "media.clip" ? "prores" : "h264");
        if (codec == "cfhd") codec = "cineform";
        if (!resolved.ContainsKey("out") && fields.TryGetValue("out", out var outputField) && outputField.Required
            && commandId != "archive" && !string.IsNullOrWhiteSpace(context.OutputDirectory) && !supplied.ContainsKey("out"))
        {
            try
            {
                string directory = Path.GetFullPath(context.OutputDirectory, workingDirectory);
                resolved["out"] = Path.Combine(directory, SuggestedOutput(command, outputField, resolved, codec));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { errors.Add(new("out", "全局输出目录无效：" + exception.Message)); }
        }
        foreach (var field in command.Fields)
            if (field.Required && !resolved.ContainsKey(field.Key)) errors.Add(new(field.Key, "请选择或填写" + field.Label + "。"));

        string Value(string key) => resolved.GetValueOrDefault(key, "");
        bool Enabled(string key) => Value(key) == "true";
        bool Nonempty(string key) => Value(key).Length > 0;

        // Unknown combinations should fail in the form, not silently lose a user's settings.
        if (Nonempty("adv") && Nonempty("model")) errors.Add(new("model", "剧情来源与模型来源必须择一使用。"));
        if (commandId is "asset.extract" or "asset.verify" or "asset.show")
        {
            bool voice = commandId == "asset.extract" && Value("logicalPath").StartsWith("voice:", StringComparison.Ordinal);
            if (!voice && Nonempty("adv") == Nonempty("model")) errors.Add(new("adv", "必须选择且只选择一个来源：剧情或模型。"));
            if (voice && Nonempty("model")) errors.Add(new("model", "按语音编号提取时不接受模型来源，请使用剧情筛选。"));
        }
        if (Nonempty("dll") && Nonempty("cubism-sdk")) errors.Add(new("dll", "原生运行库文件与 SDK 目录请选择一个。"));
        if (commandId.StartsWith("home.", StringComparison.Ordinal))
        {
            if (Nonempty("spotId") && Value("spotId") != "all"
                && (!long.TryParse(Value("spotId"), NumberStyles.None, CultureInfo.InvariantCulture, out long spot) || spot <= 0))
                errors.Add(new("spotId", "主页编号必须为正整数。"));
            if (Value("spotId") == "all" && commandId != "home.export") errors.Add(new("spotId", "全部主页仅用于批量导出。"));
            if (Value("character") == "all" && commandId != "home.export") errors.Add(new("character", "“全部角色”仅用于批量导出；渲染整组动画请留空。"));
            if (Value("spotId") == "all" && Nonempty("adv")) errors.Add(new("adv", "全部主页不能指定单个来源剧情。"));
            if (Nonempty("adv") && (!long.TryParse(Value("adv"), NumberStyles.None, CultureInfo.InvariantCulture, out long source) || source <= 0))
                errors.Add(new("adv", "主页来源剧情编号必须为正整数。"));
        }
        if (project.SourceAdv is { Length: > 0 } sourceAdv && commandId.StartsWith("story.", StringComparison.Ordinal)
            && Nonempty("advId") && Value("advId") != "all" && Value("advId") != sourceAdv)
            errors.Add(new("script", "项目中的来源剧情为 " + sourceAdv + "，与所选剧情不一致。"));

        bool home = commandId.StartsWith("home.", StringComparison.Ordinal);
        bool render = commandId.StartsWith("render.", StringComparison.Ordinal) || commandId.StartsWith("story.", StringComparison.Ordinal);
        bool encoded = commandId.EndsWith(".clip", StringComparison.Ordinal) || commandId == "home.export";
        bool timeline = home && command.OutputKind is CommandOutputKind.Image or CommandOutputKind.Video or CommandOutputKind.Directory
            || render && command.CommandTokens.Last() is "frame" or "clip" or "sequence" or "state";
        double Parse(string key, double fallback)
            => double.TryParse(Effective(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : fallback;
        if (timeline)
        {
            double time = Parse("time", 0);
            double? duration = resolved.ContainsKey("duration") ? Parse("duration", 0)
                : home ? project.SourceSpot is not null && project.LegacyDuration is { } projectDuration ? projectDuration - time : null
                : Enabled("until-end") ? 3600 : project.LegacyDuration ?? 10;
            if (home && command.CommandTokens.Last() is "clip" or "sequence" or "export" && duration is <= 0)
                errors.Add(new("time", "开始时间必须早于主页项目结束时间。"));
            if (command.CommandTokens.Last() == "state") duration = null;
            if (time > 36000 || duration is { } seconds && time + seconds > 36000)
                errors.Add(new("duration", "开始时间与时长之和不能超过 36000 秒。"));
        }

        int fallbackWidth = commandId.EndsWith(".contact", StringComparison.Ordinal) ? 640 : 1920;
        int fallbackHeight = commandId.EndsWith(".contact", StringComparison.Ordinal) ? 360 : 1080;
        int? width = commandId == "media.clip" ? context.SourceWidth : fields.ContainsKey("width") ? SafeInt(Parse("width", fallbackWidth)) : null;
        int? height = commandId == "media.clip" ? context.SourceHeight : fields.ContainsKey("height") ? SafeInt(Parse("height", fallbackHeight)) : null;
        int supersample = SafeInt(Parse("supersample", 2));
        if (width is { } w && height is { } h)
        {
            if (fields.ContainsKey("width") && (w < 16 || w > RenderDimensions.MaxSide || h < 16 || h > RenderDimensions.MaxSide))
                errors.Add(new("width", "画面尺寸超出此操作支持的范围。"));
            if (fields.ContainsKey("supersample") && (long)w * h * supersample * supersample > RenderDimensions.MaxPixels)
                errors.Add(new("supersample", "超采样后的画布超过约 1.32 亿像素。原始尺寸请选择 1 倍采样，或降低尺寸。"));
            if (encoded && codec == "cineform" && (w <= 0 || w % 16 != 0 || h < 32 || h % 8 != 0))
                errors.Add(new("width", "CineForm 要求宽度为 16 的倍数、高度为 8 的倍数且至少为 32；不会自动缩放或补边。"));
            if (encoded && codec == "h264" && (w % 2 != 0 || h % 2 != 0)) errors.Add(new("width", "H.264 的宽度和高度必须为偶数。"));
        }
        if (commandId.EndsWith(".contact", StringComparison.Ordinal))
        {
            string timesText = Effective("times", home ? "0,1,2,3" : "0,1,2");
            var times = timesText.Split(',', StringSplitOptions.TrimEntries);
            int maximum = home ? 100 : 64;
            if (times.Length < 1 || times.Length > maximum || times.Any(item => !double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || !double.IsFinite(t) || t < 0 || t > 36000))
                errors.Add(new("times", "采样时间须为 1 至 " + maximum + " 个 0 至 36000 秒的数字，以逗号分隔。"));
            int columns = SafeInt(Parse("columns", 3));
            if (columns > 0 && width is { } cw && height is { } ch && (long)cw * ch * Math.Min(times.Length, columns) * ((times.Length + columns - 1) / columns) > 64_000_000)
                errors.Add(new("columns", "图集总尺寸超过 6400 万像素，请减少采样点、列数或每格尺寸。"));
        }
        if (encoded && codec == "h264" && (commandId == "home.export" || home && (Nonempty("character") || Enabled("no-background"))
            || project.Transparent || context.SourceHasAlpha == true))
            errors.Add(new("codec", "透明画面不能使用 H.264，请选择 CineForm、ProRes 4444 或 FFV1。"));

        if (Nonempty("out"))
        {
            string output = Value("out");
            string extension = Path.GetExtension(output).ToLowerInvariant();
            bool fileOutput = fields["out"].Kind == CommandFieldKind.OutputFile;
            if (encoded && fileOutput)
            {
                string requiredExtension = codec switch { "h264" => ".mp4", "ffv1" => ".mkv", _ => ".mov" };
                if (extension != requiredExtension) errors.Add(new("out", "所选编码需要 " + requiredExtension + " 输出文件。"));
            }
            if (commandId == "media.mux" && context.SourceCodec is { Length: > 0 } sourceCodec)
            {
                sourceCodec = sourceCodec.ToLowerInvariant();
                if (extension == ".mp4" && sourceCodec is not ("h264" or "hevc"))
                    errors.Add(new("out", "当前视频编码不能直接封装为 MP4，请使用 MOV 或 MKV。"));
                if (extension == ".mov" && sourceCodec is not ("cfhd" or "cineform" or "prores" or "h264" or "hevc"))
                    errors.Add(new("out", "当前视频编码不能直接封装为 MOV，请使用 MKV。"));
            }
            foreach (var field in command.Fields.Where(field => field.Kind == CommandFieldKind.InputFile && resolved.ContainsKey(field.Key)))
                if (SamePath(Value(field.Key), output)) errors.Add(new("out", "输出路径不能与“" + field.Label + "”相同。"));
            if (fileOutput)
            {
                if (Directory.Exists(output)) errors.Add(new("out", "输出应为文件，当前路径已经是目录。"));
                if (!Enabled("overwrite") && (File.Exists(output) || File.Exists(output + ".source.json")
                    || commandId is "asset.gallery" or "model.gallery" && File.Exists(output + ".json")))
                    errors.Add(new("out", "输出文件或来源记录已存在，请选择新文件或显式允许替换。"));
            }
            else if (commandId != "archive")
            {
                if (File.Exists(output)) errors.Add(new("out", "输出应为目录，当前路径已经是文件。"));
                bool sequence = commandId is "render.sequence" or "story.sequence";
                if (Directory.Exists(output))
                {
                    if (!sequence || !Enabled("resume")) errors.Add(new("out", sequence ? "帧序列目录已存在，请选择新目录或启用续渲。" : "此操作必须使用尚不存在的新目录。"));
                    else
                    {
                        try
                        {
                            if (Directory.EnumerateFileSystemEntries(output).Any() && !File.Exists(Path.Combine(output, "render.json")))
                                errors.Add(new("resume", "已有目录缺少 render.json，无法安全续渲。"));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        { errors.Add(new("out", "无法读取帧序列目录：" + exception.Message)); }
                    }
                }
            }
            else if (File.Exists(output)) errors.Add(new("out", "归档目标必须为目录，不能是已有文件。"));
        }

        if (errors.Count > 0) return new([], errors.AsReadOnly(), resolved.GetValueOrDefault("out"), command.OutputKind);
        var arguments = new List<string>(command.CommandTokens);
        foreach (var field in command.Fields.Where(field => field.Position is not null).OrderBy(field => field.Position))
            if (resolved.TryGetValue(field.Key, out string? value)) arguments.Add(value);
        foreach (var field in command.Fields.Where(field => field.Option is not null))
        {
            if (!resolved.TryGetValue(field.Key, out string? value)) continue;
            if (field.Kind == CommandFieldKind.Boolean)
            {
                if (value == "true") arguments.Add(field.Option!);
            }
            else { arguments.Add(field.Option!); arguments.Add(value); }
        }
        return new(arguments.AsReadOnly(), [], resolved.GetValueOrDefault("out"), command.OutputKind);
    }

    private static string? Inherited(CommandDefinition command, string key, CommandContext context,
        IReadOnlyDictionary<string, string?> supplied) => key switch
    {
        "out" when command.Id == "archive" => context.ArchivePath,
        "archive" => context.ArchivePath,
        "site" => context.Site,
        "lang" => context.Language,
        "dll" when !supplied.ContainsKey("cubism-sdk") => context.NativeLibraryPath,
        "cubism-sdk" when !supplied.ContainsKey("dll") && string.IsNullOrWhiteSpace(context.NativeLibraryPath) => context.CubismSdkPath,
        "ffmpeg" => context.FFmpegPath,
        "ffprobe" => context.FFprobePath,
        _ => null
    };

    private static string SuggestedOutput(CommandDefinition command, CommandField field, IReadOnlyDictionary<string, string> values, string codec)
    {
        string name = field.SuggestedFileName ?? command.Id.Replace('.', '-');
        string selector = new[] { "spotId", "advId", "modelId", "voiceId" }.Select(key => values.GetValueOrDefault(key, "")).FirstOrDefault(value => value.Length > 0) ?? "";
        if (selector.Length > 0)
        {
            var safe = new string(selector.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray()).Trim('-');
            if (safe.Length > 64) safe = safe[..64];
            if (safe.Length > 0) name = Path.GetFileNameWithoutExtension(name) + "-" + safe + Path.GetExtension(name);
        }
        if (command.Id.EndsWith(".clip", StringComparison.Ordinal))
            name = Path.ChangeExtension(name, codec switch { "h264" => ".mp4", "ffv1" => ".mkv", _ => ".mov" });
        return name;
    }

    private static bool IsPath(CommandFieldKind kind) => kind is CommandFieldKind.InputFile or CommandFieldKind.InputDirectory
        or CommandFieldKind.OutputFile or CommandFieldKind.OutputDirectory or CommandFieldKind.NewDirectory;
    private static bool SamePath(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static int SafeInt(double number) => number is < int.MinValue or > int.MaxValue ? 0 : (int)number;

    private sealed record ProjectFacts(IReadOnlyDictionary<string, string> Defaults, string? SourceAdv = null,
        double? LegacyDuration = null, bool Transparent = false, string? SourceSite = null, string? SourceLanguage = null, string? SourceSpot = null);

    private static ProjectFacts ReadProject(CommandDefinition command, IReadOnlyDictionary<string, string> values,
        List<CommandValidationError> errors)
    {
        string key = values.ContainsKey("project") ? "project" : "script";
        if (!values.TryGetValue(key, out string? path) || !File.Exists(path)) return new(new Dictionary<string, string>());
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(path));
            if (document is JsonArray commandArray)
            {
                if (!command.Id.StartsWith("story.", StringComparison.Ordinal)) errors.Add(new(key, "统一渲染需要项目对象；原始指令数组请使用剧情操作的替代指令文件。"));
                else if (command.Id == "story.sequence") errors.Add(new(key, "剧情帧序列需要统一项目或包含 commands 数组的对象，当前不支持顶层指令数组。"));
                return new(new Dictionary<string, string>(), Transparent: CommandsTransparent(commandArray));
            }
            if (document is not JsonObject root)
            { errors.Add(new(key, "项目或指令文件必须为 JSON 对象。")); return new(new Dictionary<string, string>()); }
            var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
            if (command.Id.StartsWith("home.", StringComparison.Ordinal))
            {
                var home = HomeProject.FromJson(root);
                defaults["width"] = home.Output.Width.ToString(CultureInfo.InvariantCulture);
                defaults["height"] = home.Output.Height.ToString(CultureInfo.InvariantCulture);
                defaults["supersample"] = home.Output.Supersample.ToString(CultureInfo.InvariantCulture);
                defaults["fps"] = home.Output.Fps.ToString(CultureInfo.InvariantCulture);
                defaults["codec"] = VideoEncoding.NormalizeCodec(home.Output.Codec);
                defaults["cineform-quality"] = VideoEncoding.NormalizeCineFormQuality(home.Output.CineformQuality);
                defaults["framing"] = home.EditState.Framing;
                return new(defaults, home.Source.AdvId?.ToString(CultureInfo.InvariantCulture), home.Duration, !home.EditState.Background,
                    home.Source.Site, SourceSpot: home.Source.SpotId.ToString(CultureInfo.InvariantCulture));
            }
            bool unified = root["format"]?.ToString() == "ournotes-director/project";
            bool legacy = root["format"] is null && root["actors"] is JsonArray;
            if (command.Id.StartsWith("render.", StringComparison.Ordinal) && !unified && !legacy)
                errors.Add(new(key, "请选择统一项目或包含 actors 的旧版 Shot 文件。普通指令文件请使用剧情操作。"));
            string? sourceAdv = (root["source"] as JsonObject)?["adv"]?.ToString();
            string? sourceSite = null, sourceLanguage = null;
            if (unified)
            {
                if (root["source"] is not JsonObject || string.IsNullOrWhiteSpace(sourceAdv)) errors.Add(new(key, "统一项目必须提供 source.adv。"));
                sourceSite = root["source"]?["site"]?.ToString() ?? "intl";
                sourceLanguage = root["source"]?["language"]?.ToString() ?? "zh-Hans";
                if (root["output"] is JsonObject output)
                {
                    foreach (string name in new[] { "width", "height", "supersample", "codec" })
                        if (output[name] is { } value) defaults[name] = value.ToString();
                    if (output["cineformQuality"] is { } quality) defaults["cineform-quality"] = quality.ToString();
                }
            }
            double? duration = null;
            bool transparent = false;
            if (legacy)
            {
                sourceAdv ??= "10060";
                sourceSite = root["source"]?["site"]?.ToString() ?? "intl";
                sourceLanguage = root["source"]?["language"]?.ToString() ?? "zh-Hans";
                duration = 3;
                defaults["width"] = "1920";
                defaults["height"] = "1080";
                defaults["supersample"] = "2";
                if (root["output"] is JsonObject output)
                {
                    if (output["width"] is { } width) defaults["width"] = width.ToString();
                    if (output["height"] is { } height) defaults["height"] = height.ToString();
                    if (output["renderScale"] is { } scale) defaults["supersample"] = scale.ToString();
                }
                defaults["codec"] = "prores";
                if (double.TryParse(root["duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double length)) duration = length;
                transparent = root["background"] is null || HasTransparency(root["background"]);
            }
            foreach (string section in new[] { "initialize", "commands", "finalize" })
                if (root[section] is JsonArray rows && CommandsTransparent(rows)) transparent = true;
            if (sourceSite is not (null or "intl" or "jp")) errors.Add(new(key, "项目来源站点必须为 intl 或 jp。"));
            if (defaults.TryGetValue("codec", out string? codec)) defaults["codec"] = codec.ToLowerInvariant() == "cfhd" ? "cineform" : codec.ToLowerInvariant();
            foreach (var pair in defaults)
            {
                if (pair.Key == "codec" && pair.Value is not ("cineform" or "prores" or "ffv1" or "h264")) errors.Add(new(key, "项目包含不支持的视频编码：" + pair.Value));
                if (pair.Key is "width" or "height" or "supersample"
                    && (!int.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int integer)
                        || integer < (pair.Key == "supersample" ? 1 : 16) || integer > (pair.Key == "supersample" ? 4 : RenderDimensions.MaxSide)))
                    errors.Add(new(key, "项目中的 " + pair.Key + " 超出允许范围。"));
                if (pair.Key == "cineform-quality" && !CommandCatalog.Get("home.clip").Fields.First(field => field.Key == "cineform-quality").Choices.Any(choice => choice.Value == pair.Value.ToLowerInvariant()))
                    errors.Add(new(key, "项目中的 CineForm 质量名称无效。"));
            }
            return new(defaults, sourceAdv, duration, transparent, sourceSite, sourceLanguage);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
        {
            errors.Add(new(key, "无法读取项目或指令文件：" + exception.Message));
            return new(new Dictionary<string, string>());
        }
    }

    private static bool HasTransparency(JsonNode? value) => value is JsonArray { Count: 4 } rgba
        && double.TryParse(rgba[3]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha) && alpha < 1;

    private static bool CommandsTransparent(JsonArray rows) => rows.OfType<JsonObject>()
        .Where(row => row["IgnoreData"]?.ToString() is not ("1" or "true"))
        .Any(row => row["cmd"]?.ToString() == "Director.Scene" && row["Values"] is JsonObject scene && HasTransparency(scene["background"]));
}
