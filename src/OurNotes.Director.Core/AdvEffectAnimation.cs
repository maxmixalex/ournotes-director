using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Bind exported effect Animator curves to the native prefab; reuse the normal Mecanim evaluator.
public sealed class AdvEffectAnimation
{
    readonly AdvTransform transform;
    readonly AdvAnimator? animator;
    readonly bool enabled;
    bool active;
    public float Speed { get=>animator?.Speed??1; set { if(animator is not null)animator.Speed=value; } }
    public AdvEffectAnimation(JsonNode component,string path,AdvPrefab prefab,AdvAnimRecords records,
        IReadOnlyDictionary<string,AdvEffectSprite> sprites,IReadOnlyDictionary<string,AdvParticles> particles,IReadOnlySet<string>? meshPaths=null)
    {
        if(Actor.N(component["m_UpdateMode"])!=0||Actor.B(component["m_ApplyRootMotion"])||Actor.B(component["m_KeepAnimatorStateOnDisable"]))
            throw new NotSupportedException("Unsupported effect Animator settings");
        transform=prefab.Transform(path);enabled=Actor.B(component["m_Enabled"]);
        if(component["m_Controller"] is not JsonNode controller)return;
        var eulers=new Dictionary<AdvTransform,Vector3>();
        animator=new(controller,records,b=>{
            string targetPath=path+(b.Path.Length==0?"":"/"+b.Path);var target=prefab.TryTransform(targetPath);if(target is null)return null;
            if(b.Class=="GameObject"&&b.Attribute=="m_IsActive")return new(()=>target.Active?1:0,v=>target.Active=v>.5f);
            if(b.Class=="SpriteRenderer")return sprites.GetValueOrDefault(targetPath)?.Bind(b.Attribute);
            if(b.Class=="MeshRenderer"&&meshPaths?.Contains(targetPath)==true)throw new NotSupportedException("Animated MeshRenderer property unsupported by reference: "+b.Attribute);
            if(b.Class=="ParticleSystem"&&particles.TryGetValue(targetPath,out var system))return b.Attribute=="looping"
                ?new(()=>Actor.B(system.Data["looping"])?1:0,v=>system.Data["looping"]=v>.5f):system.Bind(b.Attribute);
            if(b.Class!="Transform")return null;
            int dot=b.Attribute.LastIndexOf('.');if(dot<0||dot!=b.Attribute.Length-2)return null;
            string property=b.Attribute[..dot];int axis="xyzw".IndexOf(b.Attribute[(dot+1)..],StringComparison.Ordinal);if(axis<0)return null;
            if(property=="m_LocalRotation")return new(()=>target.Rotation[axis],v=>{var q=target.Rotation;q[axis]=v;target.Rotation=q;});
            if(axis>2)return null;
            if(property=="m_LocalPosition")return new(()=>target.Position[axis],v=>{var p=target.Position;p[axis]=v;target.Position=p;});
            if(property=="m_LocalScale")return new(()=>target.Scale[axis],v=>{var s=target.Scale;s[axis]=v;target.Scale=s;});
            if(property is "localEulerAnglesRaw" or "localEulerAngles" or "m_LocalEulerAnglesHint")return new(()=>eulers.GetValueOrDefault(target)[axis],v=>{var e=eulers.GetValueOrDefault(target);e[axis]=v;eulers[target]=e;target.SetEuler(e);});
            return null;
        },b=>{
            string targetPath=path+(b.Path.Length==0?"":"/"+b.Path);
            if(b.Class=="SpriteRenderer"&&b.Attribute=="m_Sprite"&&sprites.TryGetValue(targetPath,out var sprite))return new(()=>sprite.Sprite,v=>sprite.Sprite=v);
            return null;
        });
    }
    public void SyncActive()
    {
        bool next=enabled&&transform.ActiveInHierarchy;
        if(next&&!active)animator?.Reset();active=next;
    }
    public void Step(float dt){SyncActive();if(active)animator?.Step(dt);}
    public bool TryPlay(string state){if(animator?.HasState(state)!=true)return false;animator.Play(state);return true;}
}
