using System.Numerics;
using Spine;

namespace OurNotes.Director.Core;

// Draws official runtime geometry into the shared premultiplied framebuffer.
// Projection remains in homogeneous clip space, so Spine layers share the room camera and depth buffer.
public sealed class SpineDrawing : IDisposable
{
    readonly OffscreenGl gl;
    readonly UnityShader shader;
    readonly Dictionary<string, uint> textures = [];
    public SpineDrawing(OffscreenGl gl)
    {
        this.gl = gl;
        shader = new("Spine 4.2 PMA two-color", Shader);
    }

    const string Shader = """
        #ifdef VERTEX
        #version 330
        in vec4 in_POSITION0;
        in vec2 in_TEXCOORD0;
        in vec4 in_COLOR0;
        in vec3 in_TEXCOORD1;
        out vec2 uv;
        out vec4 lightColor;
        out vec3 darkColor;
        void main() {
            gl_Position = in_POSITION0;
            uv = in_TEXCOORD0;
            lightColor = in_COLOR0;
            darkColor = in_TEXCOORD1;
        }
        #endif
        #ifdef FRAGMENT
        #version 330
        uniform sampler2D atlas;
        uniform float straightAlphaInput;
        in vec2 uv;
        in vec4 lightColor;
        in vec3 darkColor;
        out vec4 outputColor;
        void main() {
            vec4 texel = texture(atlas, uv);
            if (straightAlphaInput > 0.5) texel.rgb *= texel.a;
            outputColor = vec4(texel.rgb * lightColor.rgb + (texel.a - texel.rgb) * darkColor,
                               texel.a * lightColor.a);
        }
        #endif
        """;

    public void Draw(SpineActor actor, float[] localToClip, bool depthTest = true, bool depthWrite = false, bool? straightAlphaInput = null)
    {
        if (localToClip.Length != 16 || localToClip.Any(v => !float.IsFinite(v))) throw new ArgumentException("Spine projection must be a finite 4x4 column-major matrix");
        foreach (var batch in actor.Mesh.Batches)
        {
            int count = batch.Positions.Length / 3;
            var positions = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
                Vector4 p = AdvMatrix.Transform(localToClip, new(batch.Positions[i * 3], batch.Positions[i * 3 + 1], batch.Positions[i * 3 + 2], 1));
                positions[i * 4] = p.X; positions[i * 4 + 1] = p.Y; positions[i * 4 + 2] = p.Z; positions[i * 4 + 3] = p.W;
            }
            var vertices = new Dictionary<string, float[]>
            {
                ["in_POSITION0"] = positions,
                ["in_TEXCOORD0"] = batch.UVs,
                ["in_COLOR0"] = batch.Colors,
                ["in_TEXCOORD1"] = batch.DarkColors
            };
            var values = new Dictionary<string, float[]> { ["straightAlphaInput"] = [straightAlphaInput ?? !batch.Texture.PremultipliedAlpha ? 1 : 0] };
            var images = new Dictionary<string, uint> { ["atlas"] = Texture(batch.Texture) };
            var blend = (BlendMode)batch.BlendMode;
            int src = blend == BlendMode.Multiply ? 2 : 1;
            int dst = blend switch { BlendMode.Normal => 10, BlendMode.Additive => 1, BlendMode.Multiply => 10, BlendMode.Screen => 6, _ => throw new NotSupportedException("Unknown Spine blend mode") };
            gl.GameMaterial32(shader, values, images, vertices, batch.Indices, src, dst, 1, 10,
                state: new(DepthTest: depthTest ? 4 : 0, DepthWrite: depthWrite));
        }
    }

    uint Texture(SpineTexture texture)
    {
        if (textures.TryGetValue(texture.Key, out uint existing)) return existing;
        var page = texture.Page;
        uint id = gl.UploadUnity(Png.Decode(texture.Bytes), 1, 1, 1, 1);
        int Filter(TextureFilter filter) => filter switch
        {
            TextureFilter.Nearest => 0x2600, TextureFilter.Linear => 0x2601,
            TextureFilter.MipMapNearestNearest => 0x2700, TextureFilter.MipMapLinearNearest => 0x2701,
            TextureFilter.MipMapNearestLinear => 0x2702, TextureFilter.MipMapLinearLinear or TextureFilter.MipMap => 0x2703,
            _ => throw new NotSupportedException("Unsupported Spine texture filter")
        };
        int Wrap(TextureWrap wrap) => wrap switch
        {
            TextureWrap.ClampToEdge => 0x812F, TextureWrap.Repeat => 0x2901, TextureWrap.MirroredRepeat => 0x8370,
            _ => throw new NotSupportedException("Unsupported Spine texture wrap")
        };
        if (page.magFilter is not (TextureFilter.Nearest or TextureFilter.Linear)) throw new InvalidDataException($"Spine magnification filter cannot use mipmaps: {texture.File}");
        gl.SetTextureSampler(id, Filter(page.minFilter), Filter(page.magFilter), Wrap(page.uWrap), Wrap(page.vWrap),
            page.minFilter is not (TextureFilter.Nearest or TextureFilter.Linear));
        textures[texture.Key] = id;
        return id;
    }

    public void Dispose() => shader.Dispose(); // Textures belong to the shared OffscreenGl context.
}
