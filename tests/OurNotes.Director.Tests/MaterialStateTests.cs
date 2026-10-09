using OurNotes.Director.Core;

static class MaterialStateTests
{
    public static int Run()
    {
        using var gl=new OffscreenGl(32,32);
        using var shader=new UnityShader("material-state-test","""
        #ifdef VERTEX
        #version 300 es
        in vec3 in_POSITION0;
        void main(){gl_Position=vec4(in_POSITION0,1.0);}
        #endif
        #ifdef FRAGMENT
        #version 300 es
        precision highp float;
        uniform vec4 testColor;
        out vec4 result;
        void main(){result=testColor;}
        #endif
        """);
        int checks=0;
        void Draw(float[] color,float z,OffscreenGl.MaterialState? state,bool blend=false){
            gl.GameMaterial32(shader,new Dictionary<string,float[]>{{"testColor",color}},new Dictionary<string,uint>(),new Dictionary<string,float[]>{{"in_POSITION0",[-1,-1,z,1,3,-1,z,1,-1,3,z,1]}},[0,1,2],1,blend?1:0,1,blend?1:0,state:state);
        }
        float[] Pixel(){var p=gl.Read(32,32).Rgba;return Enumerable.Range(0,4).Select(k=>p[(16*32+16)*4+k]/255f).ToArray();}
        void Check(float[] expected){var actual=Pixel();float alpha=Math.Clamp(expected[3],0,1);for(int k=0;k<4;k++){float target=k==3?alpha:alpha>0?Math.Clamp(expected[k]/alpha,0,1):0;if(Math.Abs(actual[k]-target)>3f/255)throw new Exception($"Material state check {checks}, channel {k}: {actual[k]} != {target}");}checks++;}
        float Op(int op,float s,float d)=>op switch{0=>s+d,1=>s-d,2=>d-s,3=>Math.Min(s,d),4=>Math.Max(s,d),_=>throw new Exception()};
        float[] source=[.2f,.5f,.8f,.3f],destination=[.6f,.4f,.1f,.7f];
        for(int rgb=0;rgb<5;rgb++)for(int alpha=0;alpha<5;alpha++){
            gl.Begin([0,0,0,1]);Draw(destination,0,new());Draw(source,0,new(BlendOp:rgb,AlphaBlendOp:alpha),true);
            Check(Enumerable.Range(0,4).Select(k=>Op(k==3?alpha:rgb,source[k],destination[k])).ToArray());
        }
        for(int mask=0;mask<16;mask++){
            gl.Begin([0,0,0,1]);Draw(destination,0,new());Draw(source,0,new(ColorMask:mask));Check(Enumerable.Range(0,4).Select(k=>(mask&(8>>k))!=0?source[k]:destination[k]).ToArray());
        }
        float[] red=[1,0,0,1],blue=[0,0,1,1];
        for(int comparison=0;comparison<=8;comparison++)foreach(float z in new[]{-.5f,0,.5f}){
            gl.Begin([0,0,0,1]);Draw(red,0,new(DepthTest:4,DepthWrite:true));Draw(blue,z,new(DepthTest:comparison));
            bool pass=comparison switch{0 or 8=>true,1=>false,2=>z<0,3=>z==0,4=>z<=0,5=>z>0,6=>z!=0,7=>z>=0,_=>false};Check(pass?blue:red);
        }
        gl.Begin([0,0,0,1]);Draw(red,-.5f,new(DepthTest:4,DepthWrite:false));Draw(blue,.5f,new(DepthTest:4));Check(blue);
        foreach(float bias in new[]{-32f,32f}){gl.Begin([0,0,0,1]);Draw(red,0,new(DepthTest:4,DepthWrite:true));Draw(blue,0,new(DepthTest:4,OffsetUnits:bias));Check(bias<0?blue:red);}
        gl.Begin([0,0,0,1]);Draw(red,-.5f,new(DepthTest:4,DepthWrite:true));gl.Begin([0,0,0,1]);Draw(blue,.5f,new(DepthTest:4));Check(blue);
        gl.Begin([0,0,0,1]);gl.BeginLayer();Draw(red,-.5f,new(DepthTest:4,DepthWrite:true));gl.EndLayer();gl.BeginLayer();Draw(blue,.5f,new(DepthTest:4));gl.EndLayer();Check(blue);
        // Depth/mask/blend state must not leak into subsequent ordinary draws.
        Draw(red,-.9f,new(BlendOp:1,AlphaBlendOp:2,ColorMask:8,DepthTest:4,DepthWrite:true),true);Draw(blue,.9f,new());Check(blue);
        gl.Begin([0,0,0,1]);gl.WithStencilMask(()=>Draw(red,0,null),()=>{});Check([0,0,0,1]);
        // Stencil compares reference against stored value; its buffers must persist between draws, but not frames.
        for(int comparison=0;comparison<=8;comparison++)foreach(int reference in new[]{4,5,6}){
            gl.Begin([0,0,0,1]);Draw(red,0,new(Stencil:new(Pass:2,Reference:5)));
            Draw(blue,0,new(Stencil:new(Compare:comparison,Reference:reference,WriteMask:0)));
            bool pass=comparison switch{0 or 8=>true,1=>false,2=>reference<5,3=>reference==5,4=>reference<=5,5=>reference>5,6=>reference!=5,7=>reference>=5,_=>false};Check(pass?blue:red);
        }
        foreach(int initial in new[]{0,17,255})for(int op=0;op<8;op++)foreach(int branch in new[]{0,1,2}){
            gl.Begin([0,0,0,1]);Draw(red,0,new(DepthTest:8,DepthWrite:true,Stencil:new(Pass:2,Reference:initial)));
            Draw(red,branch==2?.5f:0,new(ColorMask:0,DepthTest:branch==2?2:0,Stencil:new(Compare:branch==1?1:8,Pass:branch==0?op:0,Fail:branch==1?op:0,DepthFail:branch==2?op:0,Reference:99)));
            int expected=op switch{0=>initial,1=>0,2=>99,3=>Math.Min(initial+1,255),4=>Math.Max(initial-1,0),5=>255-initial,6=>(initial+1)&255,7=>(initial-1)&255,_=>0};
            Draw(blue,0,new(Stencil:new(Compare:3,Reference:expected,WriteMask:0)));Check(blue);
        }
        gl.Begin([0,0,0,1]);Draw(red,0,new(Stencil:new(Pass:2,Reference:0xA5)));Draw(red,0,new(ColorMask:0,Stencil:new(Pass:2,Reference:0x3C,WriteMask:15)));Draw(blue,0,new(Stencil:new(Compare:3,Reference:0xAC)));Check(blue);
        Draw(red,0,new(Stencil:new(Compare:3,Reference:0x0C,ReadMask:15,WriteMask:0)));Check(red);
        gl.Begin([0,0,0,1]);Draw(blue,0,new(Stencil:new(Compare:3,Reference:0)));Check(blue);
        gl.BeginLayer();Draw(red,0,new(Stencil:new(Pass:2,Reference:7)));gl.EndLayer();gl.BeginLayer();Draw(blue,0,new(Stencil:new(Compare:3,Reference:0)));gl.EndLayer();Check(blue);
        Draw(red,0,new(Stencil:new(Compare:1)));Draw(blue,0,null);Check(blue);
        gl.Begin([0,0,0,1]);gl.WithStencilMask(()=>Draw(red,0,null),()=>{Draw(red,0,new(Stencil:new(Compare:1)));Draw(blue,0,null);});Check(blue);
        Console.WriteLine($"Passed {checks} native GPU blend, color-mask, depth, stencil, clearing and state-isolation checks.");return 0;
    }
}
