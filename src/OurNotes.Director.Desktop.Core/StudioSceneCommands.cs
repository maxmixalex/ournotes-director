using System.Text.Json.Nodes;

namespace OurNotes.Director.Desktop.Core;

public enum StudioDurationMode { Automatic, Fixed }

/// <summary>A detached native instruction. Id is stable during edits and undo/redo, but is not written into the native JSON.</summary>
public sealed record StudioCommand(string Id, string Section, int Index, string Command, bool Enabled,
    string Summary, JsonObject Data, double? Start, double? End, bool TimingKnown, bool IsGenerated)
{
    public bool IsKeyframe { get; init; }
    public bool IsObject { get; init; } = true;
    public JsonNode? RawData { get; init; }
    public string Target => Data["TargetName"]?.ToString() ?? "";
    public double? Duration => Start is double start && End is double end ? Math.Max(0, end - start) : null;
    public int? CommandId { get; init; }
    public bool IsKnownCommand { get; init; }
}

public sealed record StudioCommandSection(string Section, bool IsInherited, IReadOnlyList<StudioCommand> Commands);

/// <summary>An execution occurrence, not an assumed fixed interval. Repeated GoTo executions have distinct Ordinal values.</summary>
public sealed record StudioCommandFragment(string Id, string CommandId, string Section, int Index,
    string Command, string Label, double? Start, double? End, bool Enabled, bool IsEvent, bool IsNativeTiming,
    int Ordinal = 0);

public sealed record StudioExpansionResult(int Initialize, int Commands, int Finalize, int TextEntries)
{
    public int TotalCommands => Initialize + Commands + Finalize;
}

/// <summary>Field suggestions supplement the complete instruction JSON; unknown fields remain visible and editable.</summary>
public sealed record StudioCommandDescriptor(string Command, int? CommandId, string Label, string Description,
    IReadOnlyList<CommandField> Fields);
