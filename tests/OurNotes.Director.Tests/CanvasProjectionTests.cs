using OurNotes.Director.Core;
using System.Text.Json.Nodes;

static class CanvasProjectionTests
{
    public static int Tiles(string path)
    {
        int count=0;float max=0;
        foreach(var test in Archive.ReadJson(path).AsArray()){
            var tiles=AdvImageTiles.Build(Actor.N(test!["width"]),Actor.N(test["height"]),Actor.N(test["left"]),Actor.N(test["bottom"]),test["image"]!).ToArray();var expected=test["vertices"]!.AsArray();
            if(tiles.Length*4!=expected.Count){Console.Error.WriteLine("Tile vertex count differs");return 1;}
            for(int i=0;i<tiles.Length;i++){
                var t=tiles[i];float[][] actual=[[t.Left,t.Bottom,t.U0,t.V0],[t.Left,t.Top,t.U0,t.V1],[t.Right,t.Top,t.U1,t.V1],[t.Right,t.Bottom,t.U1,t.V0]];
                for(int v=0;v<4;v++)for(int k=0;k<4;k++){float e=Math.Abs(actual[v][k]-Actor.N(expected[i*4+v]![new[]{"x","y","u","v"}[k]]));max=Math.Max(max,e);if(e>.00001f){Console.Error.WriteLine("Tile geometry differs: "+e);return 1;}}
            }count++;
        }
        using(var gl=new OffscreenGl(64,64)){
            uint texture=gl.Upload(new(2,2,[255,0,0,255,0,255,0,255,0,0,255,255,255,255,0,255]));gl.RepeatTexture(texture);
            void Quad(float x,float y,float size,float uv)=>gl.Draw(texture,[x,y,x+size,y,x+size,y+size,x,y+size],[0,0,uv,0,uv,uv,0,uv],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],[1,1,1,1],0,false,false);
            gl.Begin([0,0,0,1]);Quad(-1,-1,2,2);var repeated=gl.Read(64,64);
            gl.Begin([0,0,0,1]);for(int y=0;y<2;y++)for(int x=0;x<2;x++)Quad(-1+x,-1+y,1,1);
            if(!repeated.Rgba.SequenceEqual(gl.Read(64,64).Rgba)){Console.Error.WriteLine("GPU repeat sampling differs from explicit tiles");return 1;}
        }
        Console.WriteLine($"Passed {count} original tiled image meshes and GPU repeat sampling; maximum error {max:G9}.");return 0;
    }
    public static int Run(string path)
    {
        float maximum=0;int count=0;
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));using var gl=new OffscreenGl(384,216);using var images=new AdvCanvasImages(archive,"intl",archive.Manifest("intl","stories","10020"),gl);uint white=gl.Upload(new(1,1,[255,255,255,255]));
        foreach(var test in Archive.ReadJson(path).AsArray()){
            var record=test!["node"]!.DeepClone();record["components"]=JsonNode.Parse("""[{"class":"Image","m_Enabled":1,"m_Color":{"r":1,"g":0.25,"b":0.5,"a":1}}]""");
            var node=new AdvCanvasNode(record,null);float w=Actor.N(test["width"]),h=Actor.N(test["height"]),d=Actor.N(test["distance"]);node.Layout(w,h);
            var actual=node.ClipQuad(-300,-200,300,200,w,h,d);
            for(int i=0;i<4;i++)for(int k=0;k<4;k++){float error=Math.Abs(actual[i*4+k]-Actor.N(test["points"]![i]![k]));maximum=Math.Max(maximum,error);if(error>.000001f){Console.Error.WriteLine("Canvas perspective projection differs: "+error);return 1;}}
            gl.Begin([0,0,0,1]);images.Image(node,w,h,perspectiveDistance:d);var rendered=gl.Read(384,216);
            gl.Begin([0,0,0,1]);gl.Draw(white,test["points"]!.AsArray().SelectMany(p=>p!.AsArray().Select(v=>Actor.N(v))).ToArray(),[0,1,1,1,1,0,0,0],[0,1,2,0,2,3],1,[1,1,1,1],[0,0,0,0],[1,.25f,.5f,1],0,false,false);var expected=gl.Read(384,216);
            if(!rendered.Rgba.SequenceEqual(expected.Rgba)){Console.Error.WriteLine("Canvas image rasterization differs from original projected geometry");return 1;}
            count++;
        }
        Console.WriteLine($"Passed {count} original canvas perspective projections and GPU images; maximum error {maximum:G9}.");return 0;
    }
}
