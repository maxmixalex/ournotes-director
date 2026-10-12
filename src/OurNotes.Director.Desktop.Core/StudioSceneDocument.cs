using System.Globalization;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Screen positions are normalized from the upper left; world positions remain world units.</summary>
public sealed record StudioTransform(double X = .5, double Y = .5, double Height = .95, double Roll = 0,
    double ScaleX = 1, double ScaleY = 1, double Opacity = 1, double Z = 0,
    double RotationX = 0, double RotationY = 0, double ScaleZ = 1);

/// <summary>Independent screen framing. World camera values are exposed separately in WorldValues.</summary>
public sealed record StudioCamera(double X = 0, double Y = 0, double Zoom = 1, double Roll = 0)
{
    public JsonObject WorldValues { get; init; } = new();
}

public sealed record StudioGrade(double Exposure = 0, double Contrast = 1, double Saturation = 1, double Vignette = 0);

public sealed record StudioActor(string Id, string Name, string Model, string Space, StudioTransform Transform,
    bool Visible = true, string? Motion = null, string? Expression = null, double MotionTime = 0, int Order = 0);

/// <summary>Time is the target/key time; tween duration spans the preceding value to this target.</summary>
public sealed record StudioKeyframe(string Id, string ObjectId, string Track, double Time, double Duration,
    string Ease, string Label, JsonObject Values, bool IsEvent = false);

/// <summary>Original commands retain their complete data, including fields not understood by the visual editor.</summary>
public sealed record StudioRawCommand(string Id, string Section, int Index, string Command,
    double? Time, double? Duration, JsonObject Data);

/// <summary>Authored transforms for editor handles; pixels and evaluated model motion come from the native session.</summary>
public sealed record StudioSceneSample(double Time, IReadOnlyList<StudioActor> Actors, StudioCamera Camera,
    StudioGrade Grade, JsonArray Background, bool TimingKnown);

/// <summary>A reversible visual editor over the same native project consumed by rendering and export.</summary>
public sealed partial class StudioSceneDocument
{
    public const int FramesPerSecond = 30;
    public const string CameraId = "$camera";
    public const string SceneId = "$scene";
}
