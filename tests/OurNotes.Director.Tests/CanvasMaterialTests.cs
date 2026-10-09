using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class CanvasMaterialTests
{
    public static int Run()
    {
        var archive=new Archive(Path.GetFullPath("ournotes-director/references/effect-samples"));
        using var gl=new OffscreenGl(64,64);using var images=new AdvCanvasImages(archive,"intl",archive.Manifest("intl","stories","10020"),gl);
        var pixels=new byte[4*4*4];for(int y=0;y<4;y++)for(int x=0;x<4;x++){int i=(y*4+x)*4;pixels[i]=(byte)(x*60);pixels[i+1]=(byte)(y*60);pixels[i+2]=80;pixels[i+3]=200;}
        var textures=(Dictionary<string,uint>)typeof(AdvCanvasImages).GetField("textures",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(images)!;
        textures["canvas-material-test"]=gl.Upload(new(4,4,pixels));
        var record=JsonNode.Parse("""{"path":"image","active":true,"rect":{"m_AnchorMin":{"x":0.5,"y":0.5},"m_AnchorMax":{"x":0.5,"y":0.5},"m_Pivot":{"x":0.5,"y":0.5},"m_SizeDelta":{"x":64,"y":64}},"components":[{"class":"Image","m_Enabled":1,"m_Type":0,"m_Color":{"r":0.8,"g":0.7,"b":0.6,"a":0.8},"m_Sprite":{"texture":{"texture":"canvas-material-test","width":4,"height":4},"rect":{"width":4,"height":4},"textureRect":{"x":0,"y":0,"width":4,"height":4}},"m_Material":{"shader":{"shader":"Mobile/Particles/Additive"},"colors":{"_TintColor":{"r":0.5,"g":0.5,"b":0.5,"a":0.5}},"keywords":[]}}]}""")!;
        int checks=0;
        foreach(string shader in new[]{"Mobile/Particles/Additive","Mobile/Particles/Alpha Blended"})foreach(var st in new[]{(.5f,.5f,0f,.5f),(.5f,.25f,.25f,.5f),(1f,1f,0f,0f)}){
            var node=new AdvCanvasNode(record.DeepClone(),null);node.Layout(64,64);var material=node.Image!["m_Material"]!;material["shader"]!["shader"]=shader;
            material["textures"]=new JsonObject{["_MainTex"]=new JsonObject{["scale"]=new JsonObject{["x"]=st.Item1,["y"]=st.Item2},["offset"]=new JsonObject{["x"]=st.Item3,["y"]=st.Item4}}};
            gl.Begin([.1f,.1f,.1f,1]);images.Image(node,64,64);var actual=gl.Read(64,64).Rgba;
            // An independent UV construction: crop the sprite rectangle, with identity material ST.
            material.AsObject().Remove("textures");var sprite=node.Image["m_Sprite"]!;
            sprite["textureRect"]=new JsonObject{["x"]=st.Item3*4,["y"]=st.Item4*4,["width"]=st.Item1*4,["height"]=st.Item2*4};
            sprite["rect"]=new JsonObject{["width"]=st.Item1*4,["height"]=st.Item2*4};
            gl.Begin([.1f,.1f,.1f,1]);images.Image(node,64,64);var expected=gl.Read(64,64).Rgba;
            int max=actual.Zip(expected,(a,b)=>Math.Abs(a-b)).Max();if(max>1)throw new Exception($"Canvas material ST differs from independently cropped image: {shader}, max={max}");checks++;
        }
        foreach(string shader in new[]{"Mobile/Particles/Additive","Mobile/Particles/Alpha Blended"})foreach(float? perspective in new float?[]{null,100}){
            var copy=record.DeepClone();
            copy["gradient"]=JsonNode.Parse("""{"enabled":true,"angleDeg":0,"blendMode":0,"splitAtKeysWhenAxisAligned":true,"gradient":{"m_Mode":0,"m_ColorSpace":0,"m_NumColorKeys":3,"m_NumAlphaKeys":2,"ctime0":0,"ctime1":32768,"ctime2":65535,"atime0":0,"atime1":65535,"key0":{"r":0.6,"g":0.4,"b":0.2,"a":0.8},"key1":{"r":0.6,"g":0.4,"b":0.2,"a":0.8},"key2":{"r":0.6,"g":0.4,"b":0.2,"a":0.8}}}""");
            var node=new AdvCanvasNode(copy,null);node.Layout(64,64);node.Image!["m_Material"]!["shader"]!["shader"]=shader;
            byte[] Render(){gl.Begin([.1f,.1f,.1f,1]);images.Image(node,64,64,perspectiveDistance:perspective);return gl.Read(64,64).Rgba;}
            var gradient=Render();copy["gradient"]!["enabled"]=false;
            node.Image["m_Color"]=JsonNode.Parse("""{"r":0.6,"g":0.4,"b":0.2,"a":0.8}""");
            var flat=Render();if(gradient.Zip(flat,(a,b)=>Math.Abs(a-b)).Max()>1)throw new Exception("Split constant gradient differs from ordinary material color");checks++;
            // A linear gradient at a pixel centre must equal a flat material with that
            // interpolated vertex color, independent of texture sampling and blending.
            var g=copy["gradient"]!["gradient"]!;g["m_NumColorKeys"]=2d;g["ctime1"]=65535d;
            g["key0"]!["r"]=0d;g["key0"]!["g"]=1d;g["key1"]!["r"]=1d;g["key1"]!["g"]=0d;
            copy["gradient"]!["enabled"]=true;gradient=Render();copy["gradient"]!["enabled"]=false;
            foreach(int x in new[]{8,32,55}){
                float t=(x+.5f)/64;node.Image["m_Color"]!["r"]=t;node.Image["m_Color"]!["g"]=1-t;
                flat=Render();int offset=(32*64+x)*4;
                for(int c=0;c<4;c++)if(Math.Abs(gradient[offset+c]-flat[offset+c])>1)throw new Exception($"Gradient material interpolation differs: {shader}, x={x}, channel={c}");checks++;
            }
        }
        foreach(bool gradientEnabled in new[]{false,true})foreach(float? perspective in new float?[]{null,100})foreach(var tr in new[]{new System.Numerics.Vector3(0,0,1),new System.Numerics.Vector3(.15f,-.1f,.75f)}){
            var copy=record.DeepClone();copy["components"]![0]!["m_Material"]=null;
            copy["components"]![0]!["m_Color"]=JsonNode.Parse("""{"r":1,"g":1,"b":1,"a":1}""");
            copy["gradient"]=JsonNode.Parse("""{"enabled":true,"angleDeg":0,"blendMode":0,"splitAtKeysWhenAxisAligned":true,"gradient":{"m_Mode":0,"m_ColorSpace":0,"m_NumColorKeys":3,"m_NumAlphaKeys":2,"ctime0":0,"ctime1":32768,"ctime2":65535,"atime0":0,"atime1":65535,"key0":{"r":1,"g":0,"b":0,"a":1},"key1":{"r":0,"g":1,"b":0,"a":1},"key2":{"r":0,"g":0,"b":1,"a":1}}}""");
            copy["gradient"]!["enabled"]=gradientEnabled;
            var node=new AdvCanvasNode(copy,null);node.LocalZ=20;node.Layout(64,64);
            var maskCopy=record.DeepClone();maskCopy["rect"]!["m_SizeDelta"]=JsonNode.Parse("""{"x":32,"y":40}""");
            maskCopy["components"]=JsonNode.Parse("""[{"class":"SoftMask","_source":0,"_channelWeights":{"r":0,"g":0,"b":0,"a":1}}]""");
            var maskNode=new AdvCanvasNode(maskCopy,null);maskNode.Layout(64,64);
            gl.Begin([0,0,0,0]);images.Image(node,64,64,clipTransform:tr,perspectiveDistance:perspective);var unmasked=gl.Read(64,64).Rgba;
            gl.Begin([0,0,0,0]);images.Image(node,64,64,softMask:maskNode,clipTransform:tr,perspectiveDistance:perspective);var masked=gl.Read(64,64).Rgba;
            float w=perspective is float d?1+20/d*tr.Z:1;int visible=0;
            for(int y=0;y<64;y++)for(int x=0;x<64;x++){
                float cx=(((2*(x+.5f)/64-1)*w/tr.Z-tr.X)+1)*32;
                float cy=(((1-2*(y+.5f)/64)*w/tr.Z-tr.Y)+1)*32;
                // Ignore raster edges; compare every interior pixel to independently
                // clipping the unmasked GPU image in original canvas coordinates.
                if(Math.Abs(Math.Abs(cx-32)-16)<1||Math.Abs(Math.Abs(cy-32)-20)<1)continue;
                bool inside=Math.Abs(cx-32)<16&&Math.Abs(cy-32)<20;int offset=(y*64+x)*4;
                if(inside&&masked[offset+3]>0)visible++;
                for(int c=0;c<4;c++)if(Math.Abs(masked[offset+c]-(inside?unmasked[offset+c]:0))>1)throw new Exception($"Soft mask canvas coordinates differ: gradient={gradientEnabled}, perspective={perspective}, pixel={x},{y}, channel={c}");
            }
            if(visible<100)throw new Exception("Soft mask check produced no useful image");checks++;
        }
        Console.WriteLine($"Passed {checks} GPU canvas material UV, gradient and soft-mask coordinate checks.");return 0;
    }
}
