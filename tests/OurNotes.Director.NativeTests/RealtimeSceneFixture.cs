using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>A tiny valid ADV archive for the real interpreter, GL scene, UI and grade passes.</summary>
static class RealtimeSceneFixture
{
    public static string Write(string root)
    {
        Directory.CreateDirectory(Path.Combine(root,"index"));
        if(!File.Exists(Path.Combine(root,"index","stats.json")))File.WriteAllText(Path.Combine(root,"index","stats.json"),"{}");
        var files=new JsonObject();
        void Put(string logical,byte[] bytes)
        {
            string asset="assets/"+Archive.Hash(bytes)+Path.GetExtension(logical);
            string path=Path.Combine(root,"sites","intl",asset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);
            files[logical]=new JsonObject{["asset"]=asset,["size"]=bytes.Length};
        }
        void Text(string logical,string text)=>Put(logical,Encoding.UTF8.GetBytes(text));
        Text("story.json","""{"episode":"episode.json","scene":"scene.json","ui":"ui/ui.json","models":{}}""");
        Text("episode.json","""{"commands":[],"text":{},"master":{"_playbackMode":0}}""");
        Text("scene.json","""
        {
          "settings":{"playerSettings":{"_initializeEpisodes":[],"_finalizeEpisodes":[],"_waitCommandLingeringTimeOnAutoPlay":0}},
          "globalVolume":{"nodes":[]},
          "cameraManager":{"nodes":[{"path":"CameraManager/MainCamera","components":[{
            "type":"Camera","field of view":60,"near clip plane":0.1,"far clip plane":100,
            "m_BackGroundColor":{"r":0,"g":0,"b":0,"a":0}
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
        """);
        Text("ui/ui.json","""
        {
          "nodes":[
            {"path":"UIAdvWidget","name":"UIAdvWidget"},
            {"path":"UIAdvWidget/UIDefaultTalkWindow","name":"UIDefaultTalkWindow","talkWindow":{}},
            {"path":"UIAdvWidget/FrameCanvas","canvasScaler":{"m_ReferenceResolution":{"x":1920,"y":1080},"m_ScreenMatchMode":1}},
            {"path":"UIAdvWidget/StillCanvas","canvas":{"m_PlaneDistance":10}}
          ],
          "videoAndStillCamera":{"camera":{"field of view":60,"near clip plane":0.1,"far clip plane":100}}
        }
        """);
        Text("ui/fonts.json","""{"textures":{},"texts":{},"fonts":{}}""");
        // The transparent Director composition uses the real renderer's built-in
        // scene/grade shaders and has no external room or game post-effect program.
        Text("shaders/shaders.json","[]");
        var rgba=new byte[64*48*4];
        for(int y=0;y<48;y++)for(int x=0;x<64;x++)
        {
            int i=(y*64+x)*4;
            rgba[i]=(byte)(30+x*2);rgba[i+1]=(byte)(25+y*3);rgba[i+2]=(byte)((x/8+y/8)%2==0?70:180);
            rgba[i+3]=(byte)(x<4||y<4||x>=60||y>=44?0:192);
        }
        Put("studio/pattern.png",Png.Encode(new(64,48,rgba)));
        string stories=Path.Combine(root,"sites","intl","stories");Directory.CreateDirectory(stories);
        File.WriteAllText(Path.Combine(stories,"43.json"),new JsonObject{["advId"]=43,["files"]=files}.ToJsonString());
        string indexPath=Path.Combine(root,"sites","intl","stories.json");
        var index=File.Exists(indexPath)?Archive.ReadJson(indexPath).AsObject():new JsonObject{["stories"]=new JsonArray()};
        var entries=index["stories"]!.AsArray();
        if(!entries.Any(entry=>entry?["advId"]?.ToString()=="43"))entries.Add((JsonNode)new JsonObject{["advId"]=43,["manifest"]="stories/43.json",["groups"]=new JsonArray()});
        File.WriteAllText(indexPath,index.ToJsonString());
        string project=Path.Combine(root,"studio-scene.json");
        File.WriteAllText(project,Document().ToJsonString());return project;
    }

    public static JsonObject Document()=>JsonNode.Parse("""
    {
      "format":"ournotes-director/project","version":1,
      "source":{"adv":"43","site":"intl","language":"zh-Hans"},
      "output":{"width":128,"height":96,"supersample":1,"clean":true,"letterbox":"none","codec":"cineform"},
      "initialize":[
        {"cmd":"Director.Scene","Values":{"background":[0,0,0,0],"backgroundImage":{"adv":"43","logicalPath":"studio/pattern.png"}}},
        {"cmd":"Director.Camera","Values":{"framing":{"x":0,"y":0,"zoom":1,"roll":0}}}
      ],
      "commands":[{"cmd":"Director.Camera","Duration":1,"Ease":"Linear","Values":{"framing":{"x":0.3}}}],
      "finalize":[]
    }
    """)!.AsObject();
}
