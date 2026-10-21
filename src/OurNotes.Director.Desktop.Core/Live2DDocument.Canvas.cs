using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class Live2DDocument
{
    public IsolatedActorCanvas? Canvas => ActorIsolation.ReadCanvas(root["output"]?["isolateCanvas"], ActorId);
    public double CanvasMultiplier => Number(root["output"]!.AsObject(), "canvasMultiplier", 1);

    /// <summary>One undoable canvas operation. The actor's presentation and complete
    /// performance stay in their original coordinates, including after repeated fits.</summary>
    public void ApplyCanvasFit(ActorCanvasFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit); fit.Validate();
        if (fit.ActorId.Length > 0 && fit.ActorId != ActorId)
            throw new ArgumentException("动作画布分析属于另一角色，请重新分析当前模型。");
        Change(next => next["output"] = new JsonObject
        {
            ["width"] = fit.Width, ["height"] = fit.Height,
            ["isolateCanvas"] = ActorIsolation.CanvasJson(fit.Canvas), ["canvasMultiplier"] = fit.Multiplier
        });
    }

    void ValidateCanvas()
    {
        var canvas = Canvas;
        var multiplier = root["output"]?["canvasMultiplier"];
        if (multiplier is not null && (multiplier is not JsonValue number
            || number.GetValueKind() != JsonValueKind.Number || canvas is null
            || !double.IsFinite(CanvasMultiplier) || CanvasMultiplier < 1))
            throw new ArgumentException("完整动作画布倍率必须与有效的固定画布一起保存，且不小于 1。");
    }
}
