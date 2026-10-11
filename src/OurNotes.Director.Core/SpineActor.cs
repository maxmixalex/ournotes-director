using System.Numerics;
using System.Text.Json.Nodes;
using Spine;

namespace OurNotes.Director.Core;

public readonly record struct SpineBounds(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * .5f;
    public Vector3 Size => Max - Min;
}

public sealed record SpineMeshBatch(SpineTexture Texture, int BlendMode, float[] Positions, float[] UVs,
    float[] Colors, float[] DarkColors, uint[] Indices);
public sealed record SpineMesh(IReadOnlyList<SpineMeshBatch> Batches, SpineBounds? Bounds);

// Each instance is a real SkeletonAnimation component, including a character's separate shadow/back/prop skeleton.
// The caller decides how those components are grouped. Reset reconstructs mutable runtime state for repeatable seeks.
public sealed class SpineActor
{
    readonly string skin;
    readonly bool flipX, flipY, pmaVertexColors, tintBlack;
    readonly float zSpacing;
    readonly HashSet<string>? includedSlots;
    Skeleton skeleton = null!;
    AnimationState state = null!;
    TrackEntry? track;
    readonly SkeletonClipping clipper = new();
    float[] world = new float[2048];
    SpineMesh? mesh;
    int currentFrame, currentFps;
    public SpineAsset Asset { get; }
    public string AnimationName { get; }
    public bool Loop { get; }
    public float TimeScale { get; }
    public float AnimationDuration { get; }
    public double Duration => TimeScale > 0 ? AnimationDuration / (double)TimeScale : 0;
    public SpineMesh Mesh => mesh ??= BuildMesh();
    public SpineBounds? Bounds => Mesh.Bounds;
    public int CurrentFrame => currentFrame;

    public SpineActor(SpineAsset asset, JsonNode animation, string? animationOverride = null, bool? loopOverride = null, IReadOnlySet<string>? includedSlots = null)
    {
        Asset = asset;
        AnimationName = animationOverride ?? Archive.S(animation, "_animationName");
        Loop = loopOverride ?? Flag(animation["loop"]);
        TimeScale = Actor.N(animation["timeScale"], 1);
        if (!float.IsFinite(TimeScale) || TimeScale < 0) throw new InvalidDataException($"Invalid Spine timeScale: {asset.Name}");
        skin = Archive.S(animation, "initialSkinName");
        flipX = Flag(animation["initialFlipX"]);
        flipY = Flag(animation["initialFlipY"]);
        pmaVertexColors = Flag(animation["pmaVertexColors"], true);
        tintBlack = Flag(animation["tintBlack"]);
        zSpacing = Actor.N(animation["zSpacing"]);
        if (!float.IsFinite(zSpacing)) throw new InvalidDataException($"Invalid Spine zSpacing: {asset.Name}");
        if (AnimationName.Length > 0)
            AnimationDuration = (asset.Data.FindAnimation(AnimationName)
                ?? throw new ArgumentException($"Spine animation '{AnimationName}' is not in {asset.Name}")).Duration;
        if (includedSlots is not null)
        {
            this.includedSlots = new(includedSlots, StringComparer.Ordinal);
            if (this.includedSlots.Count == 0) throw new ArgumentException("Spine character slot selection is empty");
            var available = asset.Slots.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            if (!this.includedSlots.IsSubsetOf(available))
                throw new ArgumentException($"Spine character refers to unknown slots in {asset.Name}: {string.Join(", ", this.includedSlots.Except(available))}");
        }
        Reset();
    }

    static bool Flag(JsonNode? value, bool fallback = false)
        => value is null ? fallback : value is JsonValue scalar && scalar.TryGetValue<bool>(out bool flag) ? flag : Actor.N(value) != 0;

    public void Reset()
    {
        skeleton = new(Asset.Data) { ScaleX = flipX ? -1 : 1, ScaleY = flipY ? -1 : 1 };
        if (skin.Length > 0 && skin != "default") skeleton.SetSkin(skin);
        skeleton.SetSlotsToSetupPose();
        state = new(Asset.StateData);
        track = AnimationName.Length == 0 ? null : state.SetAnimation(0, AnimationName, Loop);
        if (track is not null) track.MixDuration = 0;
        state.Update(0);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform(Skeleton.Physics.Reset);
        currentFrame = 0;
        currentFps = 0;
        clipper.ClipEnd();
        mesh = null;
    }

    // Frame zero is the actual beginning. Frame n always performs the same n fixed Update steps.
    public void Advance(int frame, int fps)
    {
        if (frame < 0 || fps <= 0 || fps > 1000) throw new ArgumentOutOfRangeException(nameof(frame), "Spine frames require frame >= 0 and fps in 1..1000");
        if (frame < currentFrame || currentFps != 0 && currentFps != fps) Reset();
        currentFps = fps;
        float step = TimeScale / fps;
        while (currentFrame < frame)
        {
            state.Update(step);
            // Keep animation keys on the output frame clock: accumulating 1/60f otherwise leaves frame 60
            // at 0.9999997, delaying discrete keys and loop boundaries. Physics still receives fixed steps.
            if (track is not null) track.TrackTime = (float)((currentFrame + 1) * (double)TimeScale / fps);
            skeleton.Update(step);
            state.Apply(skeleton);
            skeleton.UpdateWorldTransform(Skeleton.Physics.Update);
            currentFrame++;
            mesh = null;
        }
    }

    // The upstream home talk renderer holds the entry animation's final pose. Export uses Advance from t=0 instead.
    public void ApplyEndState()
    {
        Reset();
        if (track is not null) track.TrackTime = AnimationDuration;
        state.Update(0);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform(Skeleton.Physics.Reset);
        currentFrame = int.MaxValue; // An ensuing Advance starts a fresh, deterministic simulation.
        mesh = null;
    }

    SpineMesh BuildMesh()
    {
        var batches = new List<SpineMeshBatch>();
        Batch? current = null;
        Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        bool haveBounds = false;
        var drawOrder = skeleton.DrawOrder;
        clipper.ClipEnd();
        try
        {
            for (int order = 0; order < drawOrder.Count; order++)
            {
                var slot = drawOrder.Items[order];
                if (!slot.Bone.Active) { clipper.ClipEnd(slot); continue; }
                var attachment = slot.Attachment;
                if (attachment is ClippingAttachment clip) { clipper.ClipStart(slot, clip); continue; }
                // Shared home skeletons can contain several characters. Their complete animation and constraint
                // state is preserved; only renderable slots are filtered, without changing clipping end markers.
                if (includedSlots is not null && !includedSlots.Contains(slot.Data.Name)) { clipper.ClipEnd(slot); continue; }
                int vertexCount;
                float[] uvs;
                int[] triangles;
                TextureRegion region;
                Vector4 color;
                if (attachment is RegionAttachment quad)
                {
                    EnsureWorld(8);
                    quad.ComputeWorldVertices(slot, world, 0, 2);
                    vertexCount = 4;
                    uvs = quad.UVs;
                    triangles = QuadTriangles;
                    region = quad.Region;
                    color = new(quad.R, quad.G, quad.B, quad.A);
                }
                else if (attachment is MeshAttachment attachmentMesh)
                {
                    EnsureWorld(attachmentMesh.WorldVerticesLength);
                    attachmentMesh.ComputeWorldVertices(slot, 0, attachmentMesh.WorldVerticesLength, world, 0, 2);
                    vertexCount = attachmentMesh.WorldVerticesLength / 2;
                    uvs = attachmentMesh.UVs;
                    triangles = attachmentMesh.Triangles;
                    region = attachmentMesh.Region;
                    color = new(attachmentMesh.R, attachmentMesh.G, attachmentMesh.B, attachmentMesh.A);
                }
                else { clipper.ClipEnd(slot); continue; }
                float alpha = skeleton.A * slot.A * color.W;
                if (alpha <= 0) { clipper.ClipEnd(slot); continue; }
                var texture = (region as AtlasRegion)?.page.rendererObject as SpineTexture
                    ?? throw new InvalidDataException($"Spine attachment has no atlas page: {Asset.Name}/{attachment.Name}");
                float[] vertices = world;
                int indexCount = triangles.Length;
                if (clipper.IsClipping)
                {
                    clipper.ClipTriangles(vertices, triangles, indexCount, uvs);
                    vertices = clipper.ClippedVertices.Items;
                    vertexCount = clipper.ClippedVertices.Count / 2;
                    uvs = clipper.ClippedUVs.Items;
                    triangles = clipper.ClippedTriangles.Items;
                    indexCount = clipper.ClippedTriangles.Count;
                }
                if (vertexCount == 0 || indexCount == 0) { clipper.ClipEnd(slot); continue; }
                int blend = (int)slot.Data.BlendMode;
                if (current is null || current.Texture != texture || current.BlendMode != blend)
                {
                    if (current is not null) batches.Add(current.Finish());
                    current = new(texture, blend);
                }
                // Spine-Unity's MeshGenerator writes Color32 after alpha premultiplication.
                float alphaByte = Byte(alpha * 255);
                float rgbScale = pmaVertexColors ? alphaByte : 255;
                var light = new Vector4(Byte(skeleton.R * slot.R * color.X * rgbScale) / 255,
                    Byte(skeleton.G * slot.G * color.Y * rgbScale) / 255,
                    Byte(skeleton.B * slot.B * color.Z * rgbScale) / 255, alphaByte / 255);
                if (!pmaVertexColors) { light.X *= light.W; light.Y *= light.W; light.Z *= light.W; }
                var dark = tintBlack && slot.HasSecondColor ? new Vector3(slot.R2, slot.G2, slot.B2) * alpha : Vector3.Zero;
                // An additive PMA slot keeps destination coverage, including when normal and additive slots share a page.
                if (slot.Data.BlendMode == BlendMode.Additive) light.W = 0;
                uint start = (uint)(current.Positions.Count / 3);
                float z = zSpacing * order;
                for (int i = 0; i < vertexCount; i++)
                {
                    var p = new Vector3(vertices[i * 2], vertices[i * 2 + 1], z);
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) throw new InvalidDataException($"Nonfinite Spine geometry: {Asset.Name}/{slot.Data.Name}");
                    current.Positions.Add(p.X); current.Positions.Add(p.Y); current.Positions.Add(p.Z);
                    current.UVs.Add(uvs[i * 2]); current.UVs.Add(1 - uvs[i * 2 + 1]);
                    current.Colors.Add(light.X); current.Colors.Add(light.Y); current.Colors.Add(light.Z); current.Colors.Add(light.W);
                    current.DarkColors.Add(dark.X); current.DarkColors.Add(dark.Y); current.DarkColors.Add(dark.Z);
                    low = Vector3.Min(low, p); high = Vector3.Max(high, p); haveBounds = true;
                }
                for (int i = 0; i < indexCount; i++) current.Indices.Add(start + checked((uint)triangles[i]));
                clipper.ClipEnd(slot);
            }
            if (current is not null) batches.Add(current.Finish());
            return new(batches, haveBounds ? new(low, high) : null);
        }
        finally { clipper.ClipEnd(); }
    }

    static readonly int[] QuadTriangles = [0, 1, 2, 2, 3, 0];
    static float Byte(float value) => Math.Clamp(MathF.Truncate(value), 0, 255);
    void EnsureWorld(int length) { if (world.Length < length) Array.Resize(ref world, Math.Max(length, world.Length * 2)); }

    sealed class Batch(SpineTexture texture, int blendMode)
    {
        public SpineTexture Texture { get; } = texture;
        public int BlendMode { get; } = blendMode;
        public List<float> Positions { get; } = [];
        public List<float> UVs { get; } = [];
        public List<float> Colors { get; } = [];
        public List<float> DarkColors { get; } = [];
        public List<uint> Indices { get; } = [];
        public SpineMeshBatch Finish() => new(Texture, BlendMode, Positions.ToArray(), UVs.ToArray(), Colors.ToArray(), DarkColors.ToArray(), Indices.ToArray());
    }
}
