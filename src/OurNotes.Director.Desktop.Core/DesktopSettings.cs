using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Desktop.Core;

public sealed class DesktopSettings
{
    public string ArchivePath { get; set; } = "";
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "OurNotes Director");
    public string Site { get; set; } = "intl";
    public string Language { get; set; } = "zh-Hans";
    public string Theme { get; set; } = "Dark";
    public string EnginePath { get; set; } = "";
    public string FFmpegPath { get; set; } = "";
    public string FFprobePath { get; set; } = "";
    public string CubismSdkPath { get; set; } = "";
    public string NativeLibraryPath { get; set; } = "";
    public List<string> RecentProjects { get; } = [];
    public Dictionary<string, Dictionary<string, string?>> Forms { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, WorkspaceLayoutSettings> WorkspaceLayouts { get; } = new(StringComparer.Ordinal);
    public string? LoadWarning { get; private set; }

    public static string DataDirectory => Environment.GetEnvironmentVariable("OURNOTES_DIRECTOR_DATA") is { Length: > 0 } custom
        ? Path.GetFullPath(custom) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OurNotes", "Director");
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public CommandContext Context(string? workingDirectory = null) => new()
    {
        ArchivePath = ArchivePath, OutputDirectory = OutputDirectory, Site = Site, Language = Language,
        FFmpegPath = FFmpegPath, FFprobePath = FFprobePath, CubismSdkPath = CubismSdkPath,
        NativeLibraryPath = NativeLibraryPath, WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
    };

    public string ResolveEngine(string? applicationDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(EnginePath)) return Path.GetFullPath(EnginePath);
        return Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "advkit.exe" : "advkit");
    }

    public static DesktopSettings Load(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        var settings = new DesktopSettings();
        if (!File.Exists(path))
        {
            var candidate = Environment.GetEnvironmentVariable("OURNOTES_ARCHIVE");
            if (!string.IsNullOrWhiteSpace(candidate)) settings.ArchivePath = candidate;
            else foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                var directory = new DirectoryInfo(start);
                while (directory is not null)
                {
                    var archive = Path.Combine(directory.FullName, "archive");
                    if (File.Exists(Path.Combine(archive, "index", "stats.json"))) { settings.ArchivePath = archive; break; }
                    directory = directory.Parent;
                }
                if (settings.ArchivePath.Length > 0) break;
            }
            return settings;
        }
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("设置不是 JSON 对象。");
            string Read(string key, string fallback) => root[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
            settings.ArchivePath = Read("archive", ""); settings.OutputDirectory = Read("output", settings.OutputDirectory);
            settings.Site = Read("site", "intl"); settings.Language = Read("language", "zh-Hans"); settings.Theme = Read("theme", settings.Theme);
            settings.EnginePath = Read("engine", ""); settings.FFmpegPath = Read("ffmpeg", ""); settings.FFprobePath = Read("ffprobe", "");
            settings.CubismSdkPath = Read("cubismSdk", ""); settings.NativeLibraryPath = Read("nativeLibrary", "");
            if (root["recentProjects"] is JsonArray recent)
                settings.RecentProjects.AddRange(recent.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0).Take(12));
            if (root["forms"] is JsonObject forms)
                foreach (var pair in forms)
                    if (pair.Value is JsonObject form)
                        settings.Forms[pair.Key] = form.ToDictionary(v => v.Key, v => v.Value?.ToString(), StringComparer.Ordinal);
            if (root["workspaceLayouts"] is JsonObject layouts)
                foreach (var pair in layouts)
                    if (pair.Value is JsonObject layout)
                    {
                        var defaults = WorkspaceLayoutSettings.DefaultFor(pair.Key);
                        double Fraction(string key, double fallback) => layout[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;
                        settings.WorkspaceLayouts[pair.Key] = new WorkspaceLayoutSettings(Fraction("topLeft", defaults.TopLeft),
                            Fraction("bottomLeft", defaults.BottomLeft), Fraction("topHeight", defaults.TopHeight)).Normalize(pair.Key);
                    }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or InvalidOperationException)
        { settings.LoadWarning = "无法读取上次设置，已使用默认值。原设置文件仍保留：" + error.Message; }
        return settings;
    }

    public void RememberProject(string path)
    {
        path = Path.GetFullPath(path);
        RecentProjects.RemoveAll(p => string.Equals(p, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 12) RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
    }

    public void Save(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        var forms = new JsonObject();
        foreach (var entry in Forms)
        {
            var form = new JsonObject();
            foreach (var field in entry.Value) form[field.Key] = field.Value;
            forms[entry.Key] = form;
        }
        var layouts = new JsonObject();
        foreach (var entry in WorkspaceLayouts)
        {
            var layout = entry.Value.Normalize(entry.Key);
            layouts[entry.Key] = new JsonObject { ["topLeft"] = layout.TopLeft, ["bottomLeft"] = layout.BottomLeft, ["topHeight"] = layout.TopHeight };
        }
        var root = new JsonObject
        {
            ["version"] = 1, ["archive"] = ArchivePath, ["output"] = OutputDirectory, ["site"] = Site,
            ["language"] = Language, ["theme"] = Theme, ["engine"] = EnginePath, ["ffmpeg"] = FFmpegPath,
            ["ffprobe"] = FFprobePath, ["cubismSdk"] = CubismSdkPath, ["nativeLibrary"] = NativeLibraryPath,
            ["recentProjects"] = new JsonArray(RecentProjects.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()), ["forms"] = forms,
            ["workspaceLayouts"] = layouts
        };
        AtomicWrite(path, root.ToJsonString(JsonOptions));
    }

    public static void AtomicWrite(string path, string contents)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            // Windows MoveFileEx cannot overwrite an open destination even when
            // its readers share deletion. ReplaceFile supports that snapshot-read
            // contract and retains the original file's access controls.
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
