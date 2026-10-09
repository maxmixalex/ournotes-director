using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class AnimatorSpriteTests
{
    public static int Run(string fixture)
    {
        int checks=0;
        foreach(var test in Archive.ReadJson(fixture).AsArray().OfType<JsonNode>()){
            var clip=new AdvAnimClip(test["clip"]!);
            foreach(var sample in test["samples"]!.AsArray().OfType<JsonNode>()){
                float time=Actor.N(sample["time"]);
                if(!JsonNode.DeepEquals(clip.SampleObject(0,time),sample["values"]![0])||clip.Sample(time)[1]!=Actor.N(sample["values"]![1]))throw new Exception($"Sprite curve boundary differs at {time}");checks++;
            }
            JsonNode? sprite=JsonNode.Parse("""{"sprite":"default"}""");float alpha=1;
            var controller=test["controller"]!;
            var animator=new AdvAnimator(controller,new(controller),_=>new(()=>alpha,v=>alpha=v),_=>new(()=>sprite,v=>sprite=v));
            foreach(var frame in test["frames"]!.AsArray().OfType<JsonNode>()){
                if(frame["play"] is {} name)animator.Play(name.ToString());animator.Step(1f/30);
                if(!JsonNode.DeepEquals(sprite,frame["sprite"])||alpha!=Actor.N(frame["alpha"]))throw new Exception($"Sprite animator frame {checks} differs");checks++;
            }
            var bad=test["clip"]!.DeepClone();bad["pptrCurveMapping"]=new JsonArray();
            try{new AdvAnimClip(bad).SampleObject(0,0);throw new Exception("Invalid sprite reference accepted");}catch(InvalidDataException){}
            var fade=controller.DeepClone();fade["stateMachines"]![0]!["states"]![0]!["transitions"]!.AsArray().Add(JsonNode.Parse("""{"destination":1,"duration":1,"fixedDuration":true,"hasExitTime":true,"exitTime":0.1,"conditions":[]}"""));
            var fading=new AdvAnimator(fade,new(fade),_=>null,_=>new(()=>sprite,v=>sprite=v));
            try{fading.Step(.2f);throw new Exception("Unsupported PPtr cross-fade silently accepted");}catch(NotSupportedException){}
        }
        EffectBinding();
        EffectSpriteTests.SpriteSwapGpu();
        Console.WriteLine($"Passed {checks} source sprite samples/Animator frames, invalid mapping and cross-fade checks, native effect binding and GPU mesh/texture/hide/restore.");return 0;
    }
    static void EffectBinding()
    {
        var data=JsonNode.Parse("""{"nodes":[{"path":"root","components":[]}]}""")!;
        var prefab=new AdvPrefab(data);
        var original=JsonNode.Parse("""{"sprite":"A","texture":{"width":4,"height":4},"vertices":[[0,0],[1,0],[0,1]],"uv":[[0,0],[1,0],[0,1]],"indices":[0,1,2]}""")!;
        var replacement=original.DeepClone();replacement["sprite"]="B";replacement["vertices"]![1]![0]=2;replacement["texture"]!["width"]=8;
        var comp=JsonNode.Parse("""{"m_Enabled":1,"m_DrawMode":0,"m_Materials":[{}],"m_Color":{"r":1,"g":1,"b":1,"a":1}}""")!;comp["m_Sprite"]=original;
        var sprite=new AdvEffectSprite(comp,prefab.Root);var mesh=sprite.Geometry();
        var controller=Archive.ReadJson("ournotes-director/tests/OurNotes.Director.Tests/Fixtures/animator-sprites.json")[0]!["controller"]!.DeepClone();
        controller["clips"]![0]!["pptrCurveMapping"]=new JsonArray(original.DeepClone(),replacement);
        var animatorComp=JsonNode.Parse("""{"m_Enabled":1}""")!;animatorComp["m_Controller"]=controller;
        var animation=new AdvEffectAnimation(animatorComp,"root",prefab,new(controller),new Dictionary<string,AdvEffectSprite>{{"root",sprite}},new Dictionary<string,AdvParticles>());
        animation.Step(.25f);
        if(!sprite.Visible||sprite.Geometry().Positions[3]!=2||Actor.N(sprite.Texture["width"])!=8||ReferenceEquals(mesh,sprite.Geometry()))throw new Exception("Animated sprite mesh/texture was not replaced");
        animation.Step(.25f);if(sprite.Visible||sprite.Geometry().Indices.Length!=0)throw new Exception("Null sprite remained visible");
        animation.Step(.25f);if(!sprite.Visible||sprite.Geometry().Positions[3]!=1)throw new Exception("Sprite failed to reappear");
        animation.TryPlay("reset");animation.Step(0);if(!ReferenceEquals(sprite.Sprite,original))throw new Exception("Effect sprite default not restored");
    }
}
