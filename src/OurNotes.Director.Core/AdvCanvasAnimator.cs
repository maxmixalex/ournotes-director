using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

// CanvasAnimator's Behaviour lifetime around the shared native Mecanim simulator.
public sealed class AdvCanvasAnimator
{
    public AdvCanvasNode Node{get;}
    public AdvAnimator Animator{get;}
    public bool Enabled{get;set;}
    public bool KeepStateOnDisable{get;set;}
    public AdvCanvasAnimator(AdvCanvasNode node,JsonNode component,AdvAnimRecords records,Func<AdvAnimClip.Binding,AdvAnimator.Access?> bind)
    {
        Node=node;Enabled=Actor.B(component["m_Enabled"]);KeepStateOnDisable=Actor.B(component["m_KeepAnimatorStateOnDisable"]);
        if(Actor.N(component["m_UpdateMode"])!=0||Actor.N(component["m_CullingMode"])!=0||Actor.B(component["m_ApplyRootMotion"]))throw new NotSupportedException("Canvas Animator settings unsupported by reference");
        Animator=new(component["m_Controller"]!,records,bind);
    }
    public void OnEnable(){if(!KeepStateOnDisable)Animator.Reset();}
    public void Step(float dt){if(Enabled&&Node.ActiveInHierarchy)Animator.Step(dt);}
}
