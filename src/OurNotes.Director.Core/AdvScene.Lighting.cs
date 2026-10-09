using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    IEnumerable<AdvWait> EyeBlink(AdvRow row,AdvPlayback player)
    {
        string name=row.Text("TargetName"),action=row.Text("Parameter1");bool stopped;
        if(action=="")stopped=!stoppedEyeBlinks.Contains(name);else if(action.Equals("stop",StringComparison.OrdinalIgnoreCase))stopped=true;else if(action.Equals("resume",StringComparison.OrdinalIgnoreCase))stopped=false;else yield break;
        if(name!=""){if(stopped)stoppedEyeBlinks.Add(name);else stoppedEyeBlinks.Remove(name);}Character(name)?.SetEyeBlinkStopped(stopped,.2f);yield break;
    }
    IEnumerable<AdvWait> RimLight(AdvRow row,AdvPlayback player)
    {
        var actor=Character(row.Text("TargetName"));if(actor is null)yield break;string text=row.Text("Parameter1");float[] color=[1,1,1,1];
        if(text.StartsWith('#')){string hex=text[1..];if(hex.Length is 3 or 4)hex=string.Concat(hex.Select(c=>new string(c,2)));if(hex.Length==6)hex+="ff";if(hex.Length==8&&uint.TryParse(hex,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out uint value))color=[(value>>24)/255f,((value>>16)&255)/255f,((value>>8)&255)/255f,(value&255)/255f];}
        actor.ToggleRim(color,Param(row,2));yield break;
    }
    Dictionary<string,float[]> LightingGlobals(float[] vp)
    {
        float t=loop.Time;var position=Camera.Position+Camera.ShakeOffset;
        var globals=new Dictionary<string,float[]>{["unity_MatrixVP"]=vp,["unity_MatrixV"]=Camera.View(),["glstate_matrix_projection"]=AdvMatrix.Perspective(Camera.Fov,gl.Width/(float)gl.Height,Camera.Near,Camera.Far),["_ProjectionParams"]=[1,Camera.Near,Camera.Far,1/Camera.Far],["_WorldSpaceCameraPos"]=[position.X,position.Y,position.Z],["unity_OrthoParams"]=[0,0,0,0],["_Time"]=[t/20,t,t*2,t*3],["_GlobalMipBias"]=[0,0],["_ScreenParams"]=[gl.Width,gl.Height,1+1f/gl.Width,1+1f/gl.Height],["_MainLightPosition"]=[0,0,1,0],["_MainLightColor"]=[0,0,0,0]};
        globals["_AdditionalLightsPosition"]=new float[64];globals["_AdditionalLightsColor"]=new float[64];globals["_AdditionalLightsAttenuation"]=new float[64];globals["_AdditionalLightsSpotDir"]=new float[64];globals["_AdditionalLightsCount"]=[0,0,0,0];globals["unity_LightData"]=[0,0,0,0];globals["unity_LightIndices"]=new float[8];
        var groups=stage?["_lightGroupCollection"]?["_groups"]?.AsArray();int group=stageLightGroups.GetValueOrDefault(StageName);
        if(groups is null||group<0||group>=groups.Count)return globals;
        var prefab=stagePrefabs[StageName];JsonNode? brightest=null;AdvTransform? transform=null;
        int additional=0;var bounds=new List<float>();
        foreach(var reference in groups[group]!["_lights"]!.AsArray()){
            string path=Archive.S(reference!,"gameObject");var light=prefab.Component(path,"Light");if(!Actor.B(light["m_Enabled"]))continue;
            int type=(int)Actor.N(light["m_Type"]);
            if(type!=1){
                if(type is not (0 or 2))throw new NotSupportedException("Stage light type "+type);
                var m=prefab.Transform(path).WorldMatrix();float range=Actor.N(light["m_Range"]);
                bool outside=false;for(int axis=0;axis<3;axis++)foreach(int sign in new[]{-1,1}){float a=vp[3]+sign*vp[axis],b=vp[7]+sign*vp[4+axis],c=vp[11]+sign*vp[8+axis],d=vp[15]+sign*vp[12+axis];if(a*m[12]+b*m[13]+c*m[14]+d < -range*MathF.Sqrt(a*a+b*b+c*c))outside=true;}if(outside)continue;
                if(additional>=16)throw new NotSupportedException("More than 16 additional visible lights");int offset=additional++*4;float r2=range*range,fade=.64f*r2-r2;var color=light["m_Color"]!;float intensity=Actor.N(light["m_Intensity"]);
                bounds.AddRange([m[12],m[13],m[14],range,unchecked((int)(uint)(light["m_CullingMask"]?["m_Bits"]?.GetValue<long>()??0xffffffff))]);
                new float[]{m[12],m[13],m[14],1}.CopyTo(globals["_AdditionalLightsPosition"],offset);
                new float[]{Actor.N(color["r"])*intensity,Actor.N(color["g"])*intensity,Actor.N(color["b"])*intensity,Actor.N(color["a"])*intensity}.CopyTo(globals["_AdditionalLightsColor"],offset);
                float[] attenuation=[1/Math.Max(.0001f,r2),-r2/fade,0,1],direction=[0,0,1,0];
                if(type==0){float outer=MathF.Cos(Actor.N(light["m_SpotAngle"])*MathF.PI/360),inner=MathF.Cos(Actor.N(light["m_InnerSpotAngle"])*MathF.PI/360),inverse=1/Math.Max(.001f,inner-outer);attenuation[2]=inverse;attenuation[3]=-outer*inverse;direction=[-m[8],-m[9],-m[10],0];}
                attenuation.CopyTo(globals["_AdditionalLightsAttenuation"],offset);direction.CopyTo(globals["_AdditionalLightsSpotDir"],offset);continue;
            }
            if(brightest is null||Actor.N(light["m_Intensity"])>Actor.N(brightest["m_Intensity"])){brightest=light;transform=prefab.Transform(path);}
        }
        if(brightest is not null){var m=transform!.WorldMatrix();var c=brightest["m_Color"]!;float k=Actor.N(brightest["m_Intensity"]);globals["_MainLightPosition"]=[-m[8],-m[9],-m[10],0];globals["_MainLightColor"]=[Actor.N(c["r"])*k,Actor.N(c["g"])*k,Actor.N(c["b"])*k,Actor.N(c["a"])*k];}
        globals["_AdditionalLightsCount"]=[additional,0,0,0];globals["unity_LightData"]=[0,Math.Min(2,additional),0,0];globals["_DirectorLightBounds"]=bounds.ToArray();return globals;
    }
}
