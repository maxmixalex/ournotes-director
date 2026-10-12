using System.Globalization;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed class HomeSnapshot(string filePath) : IDisposable
{
    public string FilePath { get; } = filePath;
    public void Dispose() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
public sealed record HomeStudioKey(string Id, string TrackId, double Time, string Label, string Kind, string? CharacterKey);

/// <summary>Editable home composition, with atomic save and independent immutable render snapshots.</summary>
public sealed class HomeStudioDocument
{
    readonly Stack<HomeProject> undo = new(), redo = new();
    string savedJson = "";
    public HomeProject Project { get; private set; }
    public string? FilePath { get; private set; }
    public string? SelectedCharacterKey { get; set; }
    public double Playhead { get; set; }
    public bool HasUnsavedChanges => ToJson() != savedJson;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public HomeStudioDocument(HomeProject project)
    {
        project.Validate(); Project = HomeProject.FromJson(project.ToJson());
    }
    public static HomeStudioDocument Open(string path)
    {
        string full = Path.GetFullPath(path);
        var document = new HomeStudioDocument(HomeProject.Read(full)) { FilePath = full };
        document.savedJson = document.ToJson(); return document;
    }
    public static HomeStudioDocument Parse(string text) => new(HomeProject.Parse(text));
    public string ToJson() => Project.ToJson().ToJsonString(DesktopSettings.JsonOptions);
    public void ReplaceText(string text) => Change(HomeProject.Parse(text));

    public void Save(string? path = null)
    {
        string destination = Path.GetFullPath(path ?? FilePath ?? throw new ArgumentException("新主页项目需要保存路径。"));
        string text = ToJson(); DesktopSettings.AtomicWrite(destination, text);
        FilePath = destination; savedJson = text;
    }
    public HomeSnapshot CreateSnapshot(string directory)
    {
        string folder = Path.GetFullPath(directory); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "home-" + Guid.NewGuid().ToString("N") + ".json");
        DesktopSettings.AtomicWrite(path, ToJson()); return new(path);
    }
    public JsonObject ToDraft() => new()
    {
        ["project"] = Project.ToJson(), ["filePath"] = FilePath, ["savedJson"] = savedJson,
        ["selection"] = SelectedCharacterKey, ["playhead"] = Playhead
    };
    public static HomeStudioDocument FromDraft(JsonObject draft)
    {
        var document = new HomeStudioDocument(HomeProject.FromJson(draft["project"] as JsonObject ?? throw new InvalidDataException("主页草稿缺少项目。")))
        {
            FilePath = draft["filePath"]?.GetValue<string>(), savedJson = draft["savedJson"]?.GetValue<string>() ?? "",
            SelectedCharacterKey = draft["selection"]?.GetValue<string>(),
            Playhead = Math.Clamp(draft["playhead"]?.GetValue<double>() ?? 0, 0, 36000)
        };
        document.Playhead = Math.Min(document.Playhead, document.Project.Duration); return document;
    }

    public void SetSource(HomeProjectSource source) => Change(Project with { Source = source });
    public void SetDuration(double duration) { Change(Project with { Duration = duration }); Playhead = Math.Min(Playhead, duration); }
    public void SetOutput(HomeProjectOutput output) => Change(Project with { Output = output });
    public void SetScene(bool? background = null, bool? postProcessing = null, string? framing = null)
        => Change(Project with { EditState = Project.EditState with { Background = background ?? Project.EditState.Background,
            PostProcessing = postProcessing ?? Project.EditState.PostProcessing, Framing = framing ?? Project.EditState.Framing } });
    public HomeCharacterTrack Track(string character) => Project.EditState.Characters.GetValueOrDefault(character) ?? new();
    public HomeTransform TransformAt(string character, double time) => Track(character).At(time);

    public void SetTransform(string character, double time, HomeTransform value, bool keyframe = true)
    {
        var track = Track(character); time = Snap(time);
        SetTrack(character, track.WithTransformAt(time, value, keyframe));
    }
    public void SetAnimation(string character, double time, string? name, bool? loop = null, double speed = 1, double sourceTime = 0)
    {
        var track = Track(character); time = Snap(time);
        SetTrack(character, track with { Animations = Upsert(track.Animations, cue => cue.Time, new HomeAnimationCue(time, name, loop, speed, sourceTime), time) });
    }
    public void SetGrade(double time, HomeGrade grade, bool keyframe = true)
    {
        HomeProject.ValidateEdits(new() { Grade = grade });
        time = Snap(time);
        var current = Project.EditState.GradeAt(time);
        HomeGrade Shift(HomeGrade value) => new(Math.Clamp(value.Exposure + grade.Exposure - current.Exposure, -10, 10),
            Math.Clamp(value.Contrast + grade.Contrast - current.Contrast, 0, 4), Math.Clamp(value.Saturation + grade.Saturation - current.Saturation, 0, 4),
            Math.Clamp(value.Vignette + grade.Vignette - current.Vignette, 0, 1));
        Change(Project with { EditState = keyframe
            ? Project.EditState with { GradeKeys = Upsert(Project.EditState.GradeKeys, key => key.Time, new HomeGradeKey(time, grade), time) }
            : Project.EditState with { Grade = Shift(Project.EditState.Grade), GradeKeys = Project.EditState.GradeKeys.Select(key => key with { Value = Shift(key.Value) }).ToArray() } });
    }
    public void ResetCharacter(string character)
    {
        var tracks = new Dictionary<string, HomeCharacterTrack>(Project.EditState.Characters, StringComparer.Ordinal); tracks.Remove(character);
        Change(Project with { EditState = Project.EditState with { Characters = tracks } });
    }
    public void AddTransformKey(string character, double time) => SetTransform(character, time, TransformAt(character, time));
    public void AddGradeKey(double time) => SetGrade(time, Project.EditState.GradeAt(time));

    public IReadOnlyList<HomeStudioKey> TimelineKeys()
    {
        var result = new List<HomeStudioKey>();
        foreach (var pair in Project.EditState.Characters)
        {
            result.AddRange(pair.Value.Keys.Select(k => new HomeStudioKey(KeyId(pair.Key, "transform", k.Time), pair.Key + ":transform", k.Time,
                "位置 / 缩放", "key", pair.Key)));
            result.AddRange(pair.Value.Animations.Select(k => new HomeStudioKey(KeyId(pair.Key, "animation", k.Time), pair.Key + ":animation", k.Time,
                k.Name ?? "原始动作", "clip", pair.Key)));
        }
        result.AddRange(Project.EditState.GradeKeys.Select(k => new HomeStudioKey(KeyId("", "grade", k.Time), "grade", k.Time, "调色", "key", null)));
        return result.OrderBy(k => k.Time).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray();
    }
    public void MoveKey(string id, double time)
    {
        var key = TimelineKeys().FirstOrDefault(k => k.Id == id) ?? throw new ArgumentException("所选关键帧不存在。");
        time = Snap(time);
        if (Math.Abs(time - key.Time) < 1e-9) return;
        if (key.CharacterKey is null)
        {
            var all = Project.EditState.GradeKeys;
            if (all.Any(k => Math.Abs(k.Time - time) < 1e-9)) throw new ArgumentException("此时刻已有调色关键帧。");
            Change(Project with { EditState = Project.EditState with { GradeKeys = all.Select(k => k.Time == key.Time ? k with { Time = time } : k).OrderBy(k => k.Time).ToArray() } });
        }
        else
        {
            var track = Track(key.CharacterKey);
            if (key.Kind == "clip")
            {
                if (track.Animations.Any(k => Math.Abs(k.Time - time) < 1e-9)) throw new ArgumentException("此时刻已有动作片段。");
                SetTrack(key.CharacterKey, track with { Animations = track.Animations.Select(k => k.Time == key.Time ? k with { Time = time } : k).OrderBy(k => k.Time).ToArray() });
            }
            else
            {
                if (track.Keys.Any(k => Math.Abs(k.Time - time) < 1e-9)) throw new ArgumentException("此时刻已有位置关键帧。");
                SetTrack(key.CharacterKey, track with { Keys = track.Keys.Select(k => k.Time == key.Time ? k with { Time = time } : k).OrderBy(k => k.Time).ToArray() });
            }
        }
    }
    public void DeleteKey(string id)
    {
        var key = TimelineKeys().FirstOrDefault(k => k.Id == id) ?? throw new ArgumentException("所选关键帧不存在。");
        if (key.CharacterKey is null) Change(Project with { EditState = Project.EditState with { GradeKeys = Project.EditState.GradeKeys.Where(k => k.Time != key.Time).ToArray() } });
        else
        {
            var track = Track(key.CharacterKey);
            SetTrack(key.CharacterKey, key.Kind == "clip"
                ? track with { Animations = track.Animations.Where(k => k.Time != key.Time).ToArray() }
                : track with { Keys = track.Keys.Where(k => k.Time != key.Time).ToArray() });
        }
    }
    public void Undo() { if (undo.TryPop(out var previous)) { redo.Push(Project); Project = previous; Playhead = Math.Min(Playhead, Project.Duration); } }
    public void Redo() { if (redo.TryPop(out var next)) { undo.Push(Project); Project = next; Playhead = Math.Min(Playhead, Project.Duration); } }
    public double Snap(double time) => Math.Clamp(Math.Round(time * Project.Output.Fps) / Project.Output.Fps, 0, Project.Duration);
    void SetTrack(string character, HomeCharacterTrack track)
    {
        var tracks = new Dictionary<string, HomeCharacterTrack>(Project.EditState.Characters, StringComparer.Ordinal) { [character] = track };
        Change(Project with { EditState = Project.EditState with { Characters = tracks } });
    }
    void Change(HomeProject replacement)
    {
        replacement.Validate();
        if (JsonNode.DeepEquals(Project.ToJson(), replacement.ToJson())) return;
        undo.Push(Project); redo.Clear(); Project = replacement;
    }
    static IReadOnlyList<T> Upsert<T>(IReadOnlyList<T> values, Func<T, double> timeOf, T value, double time)
        => values.Where(v => Math.Abs(timeOf(v) - time) > 1e-9).Append(value).OrderBy(timeOf).ToArray();
    static string KeyId(string character, string kind, double time) => character + "|" + kind + "|" + time.ToString("R", CultureInfo.InvariantCulture);
}
