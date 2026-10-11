using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>SpotCameraController's resting pose and the original room/character transforms.</summary>
public sealed class HomeCamera
{
    public Vector3 Position { get; }
    public float FieldOfView { get; }
    public float Near { get; }
    public float Far { get; }
    public float OrthographicSize { get; }
    public float[] View { get; }
    public float[] Root { get; }
    public float[] Room { get; }
    public float[] RoomInverse { get; }
    public float[] ClearColor { get; }

    public HomeCamera(JsonNode home, JsonNode spot)
    {
        var settings = spot["situationSettings"] ?? throw new InvalidDataException("Home scene has no situation settings.");
        var camera = home["sceneRoot"]?["sceneCamera"]?["camera"] ?? throw new InvalidDataException("Home scene camera is absent; update the archive.");
        if (Actor.B(camera["orthographic"])) throw new NotSupportedException("An orthographic home camera is unsupported.");
        Near = Number(camera, "near clip plane", .3f); Far = Number(camera, "far clip plane", 1000);
        OrthographicSize = Number(camera, "orthographic size", 1);
        FieldOfView = Math.Clamp(Number(settings, "fieldOfView", 19), Number(settings, "zoomMinFov", 1), Number(settings, "zoomMaxFov", 179));
        var target = Vector(settings["originalOffset"]);
        var initial = Vector(settings["defaultPositionOffset"]);
        var direction = target - initial;
        if (direction.LengthSquared() < 1e-10f) throw new InvalidDataException("Home camera position equals its look target.");
        var forward = Vector3.Normalize(direction);
        var right = Vector3.Cross(Vector3.UnitY, forward);
        if (right.LengthSquared() < 1e-10f) throw new InvalidDataException("Home camera looks parallel to its up axis.");
        right = Vector3.Normalize(right); var up = Vector3.Cross(forward, right);
        Position = initial - Number(settings, "orbitRatio", 0) * forward;
        float[] world = [right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, Position.X, Position.Y, Position.Z, 1];
        var flip = AdvMatrix.Identity(); flip[10] = -1;
        View = AdvMatrix.Multiply(flip, AdvMatrix.InverseRigid(world));
        _ = Projection(1);
        var c = camera["m_BackGroundColor"];
        ClearColor = [Number(c, "r", 0), Number(c, "g", 0), Number(c, "b", 0), Number(c, "a", 0)];

        var root = home["sceneRoot"]?["objRoot"];
        var parentRotation = Rotation(root?["rotation"]);
        Root = root is null ? AdvMatrix.Identity() : Trs(Vector(root["position"]), parentRotation, Vector(root["scale"], Vector3.One));
        var eulerMatrix = AdvMatrix.Trs(Vector3.Zero, Vector(settings["backgroundRotation"]), Vector3.One);
        var backgroundRotation = Quaternion.Normalize(Quaternion.Conjugate(parentRotation) * Quaternion.CreateFromRotationMatrix(Numerics(eulerMatrix)));
        var background = AdvMatrix.Multiply(Root, Trs(Vector(settings["backgroundPosition"]), backgroundRotation, Vector(settings["backgroundScale"], Vector3.One)));
        var roomRoot = home["roomRoot"];
        Room = roomRoot is null ? background : AdvMatrix.Multiply(background, Inverse(Trs(Vector(roomRoot["localPosition"]), Rotation(roomRoot["localRotation"]), Vector(roomRoot["localScale"], Vector3.One))));
        RoomInverse = Inverse(Room);
    }

    public float[] Projection(float aspect) => AdvMatrix.Perspective(FieldOfView, aspect, Near, Far);
    public float[] ViewProjection(float aspect) => AdvMatrix.Multiply(Projection(aspect), View);
    public static Vector3 Vector(JsonNode? value, Vector3 fallback = default) => value is null ? fallback : new(Number(value, "x", fallback.X), Number(value, "y", fallback.Y), Number(value, "z", fallback.Z));
    public static float Number(JsonNode? node, string key, float fallback) => Actor.N(node?[key], fallback);
    static Quaternion Rotation(JsonNode? value)
    {
        var q = value is null ? Quaternion.Identity : new Quaternion(Number(value, "x", 0), Number(value, "y", 0), Number(value, "z", 0), Number(value, "w", 1));
        if (!float.IsFinite(q.LengthSquared()) || q.LengthSquared() < 1e-10f) throw new InvalidDataException("Invalid home transform rotation.");
        return Quaternion.Normalize(q);
    }
    public static float[] Trs(Vector3 position, Quaternion rotation, Vector3 scale) => Array(Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));
    public static float[] Inverse(float[] matrix) => Matrix4x4.Invert(Numerics(matrix), out var inverse) ? Array(inverse) : throw new InvalidDataException("Singular home transform.");
    public static float[] Matrix(JsonNode? value)
    {
        if (value is not JsonArray a || a.Count != 16) throw new InvalidDataException("Home transform must be a 4x4 column-major matrix.");
        var m = a.Select(v => v!.GetValue<float>()).ToArray();
        if (m.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite home transform.");
        return m;
    }
    static Matrix4x4 Numerics(float[] m) => new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
    static float[] Array(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
}
