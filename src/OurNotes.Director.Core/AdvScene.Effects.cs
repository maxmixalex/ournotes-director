using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    readonly Dictionary<string,AdvEffectInstance> effects=[];
    readonly Dictionary<string,AdvEffectGroups> stageEffects=[];
    readonly AdvRandom effectRandom=new(0x41504546);
    AdvEffectMaterials? effectMaterials;
    public IReadOnlyDictionary<string,AdvEffectInstance> Effects=>effects;
    public IReadOnlyDictionary<string,AdvEffectGroups> StageEffectGroups=>stageEffects;
    IEnumerable<AdvEffectInstance> AllEffects()=>effects.Values.Concat(stageEffects.Values.SelectMany(g=>g.Instances));
    void PreloadEffects(IEnumerable<AdvRow> rows)
    {
        PrepareFocusMaterials(rows);
        var names=rows.Where(r=>!r.Ignored&&r.Command=="Effect").Select(r=>r.Text("TargetName")).Where(n=>n!="").Distinct().ToArray();if(names.Length==0)return;
        string file=Archive.S(definition,"effects");if(file=="")throw new InvalidDataException("Story has no effects.json");
        var document=JsonNode.Parse(archive.Logical(site,manifest,file))!;var records=new AdvAnimRecords(document);
        effectMaterials??=new(archive,site,manifest,gl,shaders);
        foreach(string name in names){if(effects.ContainsKey(name))continue;string asset=Archive.S(document["instances"]!,name);if(asset=="")continue;
            var exported=document["effects"]?[asset]??throw new InvalidDataException("Effect prefab missing: "+asset);var effect=new AdvEffectInstance(exported,effectRandom,records);effect.Initialize();effects.Add(name,effect);
        }
    }
    IEnumerable<AdvWait> Effect(AdvRow row,AdvPlayback player)
    {
        var effect=effects.GetValueOrDefault(row.Text("TargetName"))??throw new InvalidDataException("Effect instance not loaded: "+row.Text("TargetName"));
        string state=row.Text("Parameter2");bool blank=string.IsNullOrWhiteSpace(state);
        if(effect.Playing&&blank){effect.Stop(row.Text("Parameter1").Equals("atonce",StringComparison.OrdinalIgnoreCase),loop);yield break;}
        if(!effect.Playing){var layers=row.Data["CanvasLayers"]?.AsArray();int layer=layers is {Count:>0}?(int)Actor.N(layers[0]):2;
            if(!PlaceEffect(effect,layer,(int)row.Number("PositionType")))yield break;effect.Play(1);
        }
        if(!blank)effect.TryPlayState(state);
        yield break;
    }
    bool PlaceEffect(AdvEffectInstance effect,int canvasLayer,int position)
    {
        effect.PositionType=0;
        if(canvasLayer is >=4 and <=8)throw new NotSupportedException("UI-layer Effect placement is not connected yet");
        if(stage is null)return false;
        if(canvasLayer is 0 or 1){
            var world=AdvMatrix.Trs(Camera.Position+Camera.ShakeOffset,Camera.Euler,Vector3.One);var forward=Vector3.Normalize(new(world[8],world[9],world[10]));
            effect.Root.Parent=null;effect.Root.Position=Camera.Position+Camera.ShakeOffset+forward*Actor.N(stage["_backgroundFieldPosition"]?["z"]);effect.Root.SetEuler(Camera.Euler);
            effect.SetOrder(-50);effect.Layer=12;return true;
        }
        if(position is 1 or 3 or 5 or 7 or 9){
            var parent=Slot(position);effect.Root.Parent=parent;effect.Root.Position=Vector3.Zero;var rotation=parent.Rotation;for(var p=parent.Parent;p is not null;p=p.Parent)rotation=p.Rotation*rotation;
            effect.Root.Rotation=Quaternion.Conjugate(rotation);effect.PositionType=position;effect.Layer=6+(position-1)/2;effect.RestoreOrder();return true;
        }
        effect.Root.Parent=null;effect.Root.Position=Vector3.Zero;effect.Root.Rotation=Quaternion.Identity;effect.Layer=13;effect.SetOrder(0);return true;
    }
    void AnimateEffects(){foreach(var effect in AllEffects())effect.Animate((float)loop.DeltaTime);foreach(var effect in AllEffects())effect.Simulate((float)loop.DeltaTime);}
    IEnumerable<AdvRenderItem> EffectItems(IReadOnlyDictionary<string,float[]> globals,Func<AdvEffectInstance,AdvEffectInstance.Renderer,bool> include)
    {
        if(effectMaterials is null)yield break;
        var camera=AdvMatrix.Trs(Camera.Position+Camera.ShakeOffset,Camera.Euler,Vector3.One);var projection=AdvMatrix.Perspective(Camera.Fov,gl.Width/(float)gl.Height,Camera.Near,Camera.Far);
        var items=AllEffects().Where(e=>e.Visible).SelectMany(e=>e.Renderers.Where(r=>include(e,r)))
            .Where(r=>r.Sprite is not null?r.Sprite.Visible:r.Mesh is not null?r.Mesh.Visible:r.Particles!.Visible);
        foreach(var renderer in items)yield return new(renderer.Order,Vector3.DistanceSquared(renderer.Sprite?.WorldCenter()??renderer.Mesh?.WorldCenter()??renderer.Particles!.WorldCenter(),Camera.Position+Camera.ShakeOffset),()=>{if(renderer.Sprite is not null)effectMaterials.Draw(renderer.Sprite,globals);else if(renderer.Mesh is not null)renderer.Mesh.Draw(effectMaterials,globals);else effectMaterials.Draw(renderer.Particles!,globals,camera,projection);});
    }
}
