using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class RenderLanguageTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ournotes-render-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "index"));
        Directory.CreateDirectory(Path.Combine(root, "sites", "intl", "assets"));
        File.WriteAllText(Path.Combine(root, "index", "stats.json"), "{}");
        try
        {
            var archive = new Archive(root);
            JsonObject Asset(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var path = "assets/" + Archive.Hash(bytes) + ".json";
                File.WriteAllBytes(archive.SitePath("intl", path), bytes);
                return new JsonObject { ["asset"] = path, ["size"] = bytes.Length };
            }
            // Match a --languages ja download: all languages remain in the raw
            // manifest, but only the selected language's UI asset is present.
            var manifest = new JsonObject
            {
                ["files"] = new JsonObject
                {
                    ["shaders/shaders.json"] = Asset("""
                    [
                      {"name":"Hidden/Universal Render Pipeline/LutBuilderLdr","variants":[{"pass":0,"subShader":0,"keywords":[],"file":"test.glsl"}]},
                      {"name":"Hidden/Universal Render Pipeline/UberPost","variants":[{"pass":0,"subShader":0,"keywords":[],"file":"test.glsl"}]}
                    ]
                    """),
                    ["shaders/test.glsl"] = Asset("""
                    #ifdef VERTEX
                    #version 300 es
                    in vec4 in_POSITION0;
                    void main(){gl_Position=in_POSITION0;}
                    #endif
                    #ifdef FRAGMENT
                    #version 300 es
                    precision highp float;
                    out vec4 result;
                    void main(){result=vec4(0.0,1.0,0.0,1.0);}
                    #endif
                    """)
                },
                ["languages"] = new JsonObject
                {
                    ["ja"] = new JsonObject { ["files"] = new JsonObject { ["ui/ui.json"] = Asset("""
                    {
                      "nodes":[{"path":"UIAdvWidget/StillCanvas","canvas":{"m_PlaneDistance":10}}],
                      "videoAndStillCamera":{"camera":{"field of view":60,"near clip plane":0.1,"far clip plane":100}}
                    }
                    """) } },
                    ["zh-Hans"] = new JsonObject { ["files"] = new JsonObject
                    {
                        ["ui/ui.json"] = new JsonObject { ["asset"] = "assets/" + new string('a', 64) + ".json" }
                    } }
                }
            };
            var definition = JsonNode.Parse("""{"ui":"ui/ui.json"}""")!;
            var scene = JsonNode.Parse("""
            {
              "settings":{"playerSettings":{}},
              "globalVolume":{"nodes":[]},
              "cameraManager":{"nodes":[{"path":"CameraManager/MainCamera","components":[{
                "type":"Camera","field of view":60,"near clip plane":0.1,"far clip plane":100,
                "m_BackGroundColor":{"r":0,"g":0,"b":0,"a":1}
              }]}]},
              "characterField":{"nodes":[{"path":"field","components":[{
                "class":"AdvCharacterField","_field":{"transform":"field"},"_characterStages":[],"_focusAnchors":[]
              }]}]},
              "backgroundField":{"nodes":[
                {"path":"background","components":[{"class":"AdvBackgroundField","_field":{"transform":"background"},"_renderer":{"gameObject":"background/Sprite"}}]},
                {"path":"background/BaseRenderer","components":[{"type":"MeshRenderer"},{"type":"MeshFilter","m_Mesh":{"vertices":[[0,0,0],[1,0,0],[0,1,0]],"uv0":[[0,0],[1,0],[0,1]],"submeshes":[[0,1,2]]}}]},
                {"path":"background/Sprite","components":[{"type":"SpriteRenderer"}]}
              ]},
              "stages":{}
            }
            """)!;
            var loop = new AdvLoop();
            var tweens = new AdvTweens(loop);
            using var gl = new OffscreenGl(32, 32);
            using var stills = new AdvStills(archive, "intl", manifest, definition, "ja", loop, tweens, gl, []);
            if (stills.Visible) throw new Exception("No-Still scene unexpectedly became visible.");
            using var drawing = new AdvScene(archive, "intl", "", manifest, definition, scene, "ja", gl, loop, tweens);
            gl.Begin([0, 0, 0, 0]);
            bool drewMedia = false;
            drawing.DrawMedia(() => drewMedia = true, true);
            var pixel = gl.Read(32, 32).Rgba;
            int center = (16 * 32 + 16) * 4;
            if (!drewMedia || pixel[center + 1] != 255 || pixel[center + 3] != 255)
                throw new Exception("Selected-language media camera did not finish its render pass.");
            Console.WriteLine("Passed 2 Japanese-only archive Still initialization and media-camera rendering checks.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
