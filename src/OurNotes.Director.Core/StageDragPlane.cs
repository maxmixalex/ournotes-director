using System.Numerics;

namespace OurNotes.Director.Core;

/// <summary>A snapshot of the real slot's translation plane, not a screen-space overlay.
/// Converting a gesture preserves local Z and uses the actual camera/parent projection.</summary>
public sealed record StageDragPlane(int Slot, Vector3 Position, Vector3 Anchor, float[] LocalToClip,
    double ViewportHeight = 1)
{
    public Vector3 Displacement(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || LocalToClip.Length != 16 ||
            !double.IsFinite(ViewportHeight) || ViewportHeight <= 0)
            throw new ArgumentException("无效的舞台拖动坐标。");
        var m = LocalToClip;
        var p = AdvMatrix.Transform(m, new Vector4(Anchor, 1));
        if (p.W <= 1e-6f) throw new InvalidOperationException("人物位于相机后方，无法在画布上定位。");
        double x = p.X / (double)p.W + 2 * dx, y = p.Y / (double)p.W - 2 * dy / ViewportHeight;
        double a = m[0] - x * m[3], b = m[4] - x * m[7];
        double c = m[1] - y * m[3], d = m[5] - y * m[7];
        double u = x * (m[11] * Anchor.Z + m[15]) - (m[8] * Anchor.Z + m[12]);
        double v = y * (m[11] * Anchor.Z + m[15]) - (m[9] * Anchor.Z + m[13]);
        double determinant = a * d - b * c;
        if (Math.Abs(determinant) < 1e-10) throw new InvalidOperationException("当前镜头与舞台平面平行，无法拖动定位。");
        var delta = new Vector3((float)((u * d - b * v) / determinant - Anchor.X),
            (float)((a * v - u * c) / determinant - Anchor.Y), 0);
        CheckVector(delta);
        return delta;
    }

    internal static void CheckVector(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || value.LengthSquared() > 1e8f)
            throw new ArgumentException("舞台位移超出有效范围。");
    }
}

public sealed partial class AdvScene
{
    public StageDragPlane? StageDragGeometry(string actorId)
    {
        loop.VerifyThread();
        var actor = Character(actorId);
        var entry = shown.FirstOrDefault(p => ReferenceEquals(p.Value, actor));
        if (actor is null || entry.Value is null) return null;
        var slot = Slot(entry.Key);
        // Custom overlays deliberately opt out: their post-projection coordinates do not
        // describe the native plane. Do not silently approximate one with the other.
        if (actor.EditorOffsetX != 0 || actor.EditorOffsetY != 0 || actor.EditorScale != 1) return null;
        var projection = AdvMatrix.Multiply(Camera.ViewProjection(gl.Width / (float)gl.Height),
            slot.Parent?.WorldMatrix() ?? AdvMatrix.Identity());
        var anchor = AdvMatrix.Transform(slot.LocalMatrix(), new Vector4(actor.Prefab.Root.Position, 1));
        return new(entry.Key, slot.Position, new Vector3(anchor.X, anchor.Y, anchor.Z), projection);
    }

    /// <summary>Transient absolute placement on the render thread. Saved edits use the
    /// existing MoveToDirection command; this does not add a new game command.</summary>
    public void PreviewStagePosition(string actorId, int slot, Vector3 position)
    {
        loop.VerifyThread(); StageDragPlane.CheckVector(position);
        if (!shown.TryGetValue(slot, out var actor) || !ReferenceEquals(actor, Character(actorId)))
            throw new InvalidOperationException("人物站位已改变，请重新拖动。");
        Slot(slot).Position = position;
    }
}
