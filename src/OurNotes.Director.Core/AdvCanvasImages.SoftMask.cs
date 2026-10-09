using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

public sealed partial class AdvCanvasImages
{
    UnityShader? softShader;
    const string SoftSource="""
        #ifdef VERTEX
        #version 330
        in vec4 in_POSITION0;
        in vec2 in_TEXCOORD0;
        in vec4 in_COLOR0;
        uniform vec2 canvasSize;
        uniform vec3 clipTransform;
        uniform vec4 inverseX,inverseY;
        out vec2 uv,maskPosition;
        out vec4 color;
        void main(){gl_Position=in_POSITION0;uv=in_TEXCOORD0;color=in_COLOR0;
            vec4 p=vec4((in_POSITION0.xy/clipTransform.z-clipTransform.xy+1.0)*canvasSize*0.5,0.0,1.0);
            maskPosition=vec2(dot(inverseX,p),dot(inverseY,p));}
        #endif
        #ifdef FRAGMENT
        #version 330
        uniform sampler2D image,maskImage;
        uniform vec4 maskRect,maskUv,borderRect,borderUv,weights;
        uniform float sliced,invertMask,invertOutsides;
        in vec2 uv,maskPosition;
        in vec4 color;
        out vec4 outputColor;
        float seg(float a,float a1,float a2,float u1,float u2){float w=a2-a1;return mix(u1,u2,w!=0.0?(a-a1)/w:0.0);}
        float axis(float a,float a1,float a2,float a3,float a4,float u1,float u2,float u3,float u4){
            if(a>=a3&&a>=a2)return seg(a,a3,a4,u3,u4);
            if(a>=a2)return seg(a,a2,a3,u2,u3);
            return seg(a,a1,a2,u1,u2);}
        void main(){vec2 p=maskPosition,muv;
            if(sliced>0.5)muv=vec2(axis(p.x,maskRect.x,borderRect.x,borderRect.z,maskRect.z,maskUv.x,borderUv.x,borderUv.z,maskUv.z),
                                  axis(p.y,maskRect.y,borderRect.y,borderRect.w,maskRect.w,maskUv.y,borderUv.y,borderUv.w,maskUv.w));
            else muv=vec2(seg(p.x,maskRect.x,maskRect.z,maskUv.x,maskUv.z),seg(p.y,maskRect.y,maskRect.w,maskUv.y,maskUv.w));
            float m=dot(texture(maskImage,vec2(muv.x,1.0-muv.y))*weights,vec4(1.0));
            float inside=step(maskRect.x,p.x)*step(maskRect.y,p.y)*step(p.x,maskRect.z)*step(p.y,maskRect.w);
            float mask=mix(invertOutsides,invertMask>0.5?1.0-m:m,inside);
            vec4 c=color;c.a=roundEven(c.a*255.0)/255.0;
            c*=texture(image,uv);c.a*=mask;outputColor=vec4(c.rgb*c.a,c.a);}
        #endif
        """;
    void DrawSoft(uint texture,float[] xy,float[] uv,float[] colors,ushort[] indices,AdvCanvasNode mask,float width,float height,System.Numerics.Vector3? clipTransform=null)
    {
        var component=mask.Component("SoftMask")??throw new InvalidDataException("SoftMask component missing");
        if(Actor.N(component["_source"])!=0||Actor.B(component["_separateMask"]))throw new NotSupportedException("SoftMask source outside its own image");
        softShader??=new("Chat SoftMaskable UI",SoftSource);var m=mask.Matrix;float determinant=m[0]*m[5]-m[1]*m[4];
        if(Math.Abs(determinant)<1e-12f)return;
        var clip=clipTransform??new System.Numerics.Vector3(0,0,1);if(clip.Z==0)return;
        float a=m[5]/determinant,b=-m[4]/determinant,c=-m[1]/determinant,d=m[0]/determinant;
        float left=-mask.Pivot.X*mask.Width,bottom=-mask.Pivot.Y*mask.Height,right=left+mask.Width,top=bottom+mask.Height;
        float[] rect=[left,bottom,right,top],uvRect=[0,0,1,1],border=rect,uvBorder=uvRect;bool sliced=false;uint maskTexture=white;
        var image=mask.Image;var sprite=image?["m_Sprite"];
        if(sprite is not null){
            maskTexture=Texture(sprite["texture"]!);float tw=Actor.N(sprite["texture"]!["width"]),th=Actor.N(sprite["texture"]!["height"]);
            var tr=sprite["textureRect"]!;float x=Actor.N(tr["x"]),y=Actor.N(tr["y"]),w=Actor.N(tr["width"]),h=Actor.N(tr["height"]),rw=Actor.N(sprite["rect"]!["width"]),rh=Actor.N(sprite["rect"]!["height"]);
            float ox=Actor.N(sprite["textureRectOffset"]?["x"]),oy=Actor.N(sprite["textureRectOffset"]?["y"]),pr=rw-ox-w,pt=rh-oy-h;
            uvRect=[x/tw,y/th,(x+w)/tw,(y+h)/th];int type=(int)Actor.N(image!["m_Type"]);
            if(type==0){if(Actor.B(image["m_PreserveAspect"]))throw new NotSupportedException("SoftMask preserve aspect");rect=[left+ox/rw*mask.Width,bottom+oy/rh*mask.Height,right-pr/rw*mask.Width,top-pt/rh*mask.Height];border=rect;uvBorder=uvRect;}
            else if(type==1){
                sliced=true;float scale=100/(Actor.N(sprite["pixelsToUnits"],100)*Actor.N(image["m_PixelsPerUnitMultiplier"],1));var bd=sprite["border"]!;
                float bl=Actor.N(bd["x"]),bb=Actor.N(bd["y"]),br=Actor.N(bd["z"]),bt=Actor.N(bd["w"]);
                uvBorder=[(x-ox+bl)/tw,(y-oy+bb)/th,(x-ox+rw-br)/tw,(y-oy+rh-bt)/th];bl*=scale;bb*=scale;br*=scale;bt*=scale;
                if(bl+br>mask.Width&&bl+br!=0){float k=mask.Width/(bl+br);bl*=k;br*=k;}if(bb+bt>mask.Height&&bb+bt!=0){float k=mask.Height/(bb+bt);bb*=k;bt*=k;}
                rect=[left+ox*scale,bottom+oy*scale,right-pr*scale,top-pt*scale];border=[left+bl,bottom+bb,right-br,top-bt];
            }else throw new NotSupportedException("SoftMask image mode "+type);
        }
        var weights=component["_channelWeights"]!;var values=new Dictionary<string,float[]>{{"canvasSize",[width,height]},{"clipTransform",[clip.X,clip.Y,clip.Z]},{"inverseX",[a,b,0,-a*m[12]-b*m[13]]},{"inverseY",[c,d,0,-c*m[12]-d*m[13]]},
            {"maskRect",rect},{"maskUv",uvRect},{"borderRect",border},{"borderUv",uvBorder},{"sliced",[sliced?1:0]},{"weights",new[]{"r","g","b","a"}.Select(k=>Actor.N(weights[k])).ToArray()},
            {"invertMask",[Actor.B(component["_invertMask"])?1:0]},{"invertOutsides",[Actor.B(component["_invertOutsides"])?1:0]}};
        int count=uv.Length/2;var positions=xy.Length==count*4?xy:Enumerable.Range(0,count).SelectMany(i=>new[]{xy[i*2],xy[i*2+1],0f,1f}).ToArray();
        gl.GameMaterial(softShader,values,new Dictionary<string,uint>{{"image",texture},{"maskImage",maskTexture}},new Dictionary<string,float[]>{{"in_POSITION0",positions},{"in_TEXCOORD0",uv},{"in_COLOR0",colors}},indices,1,10,1,10);
    }
}
