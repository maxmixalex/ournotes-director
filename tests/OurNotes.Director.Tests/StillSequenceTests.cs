using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class StillSequenceTests
{
    public static int Run(string path)
    {
        var fixture=JsonNode.Parse(File.ReadAllText(path))!;int count=0;float max=0;
        foreach(var test in fixture["cases"]!.AsArray().OfType<JsonNode>()){
            var loop=new AdvLoop();var tweens=new AdvTweens(loop);
            var nodes=new Dictionary<string,AdvCanvasNode>();
            foreach(var record in test["nodes"]!.AsArray().OfType<JsonNode>()){
                string name=record["path"]!.GetValue<string>();int slash=name.LastIndexOf('/');
                nodes[name]=new(record,slash<0?null:nodes[name[..slash]]);
            }
            var target=nodes[test["targetPath"]?.GetValue<string>()??"target"];
            AdvStillSequence? seq=null;AdvStillAnimations? host=null;float speed=test["speed"]!.GetValue<float>();
            int frame=0;var events=new JsonArray();
            if(Actor.B(test["host"])){host=new(nodes,tweens,new(722));if(Actor.B(test["lifecycle"])){nodes["root"].Active=false;host.SyncActivity();}else host.Play("sequence",speed);}
            else seq=new(nodes,nodes["sequence"],test["definition"]!,tweens,speed,invoke:test["events"] is null?null:ev=>events.Add(new JsonObject{["frame"]=frame+1,["event"]=ev["event"]!.DeepClone()}));
            foreach(var expected in test["frames"]!.AsArray().OfType<JsonNode>()){
                foreach(var action in test["actions"]?.AsArray().OfType<JsonNode>().Where(a=>(int)Actor.N(a["frame"])==frame)??[]){
                    if(action["playPath"] is JsonNode playPath){host!.Play(playPath.ToString(),speed);continue;}
                    bool on=Actor.B(action["active"]);if(on){nodes["root"].Active=true;host!.SyncActivity();host.Stop();if(Actor.B(action["play"]))host.Play("root/sequence",speed);}else{if(!Actor.B(action["raw"]))host!.Stop();nodes["root"].Active=false;host!.SyncActivity();}
                }
                if(test["stopFrame"] is not null&&frame==(int)Actor.N(test["stopFrame"]))host?.Stop();
                if(host is not null)host.Step(1f/30);else seq!.Step(1f/30);frame++;count++;
                float error;
                if(test["property"]?.GetValue<string>()=="rotation"){
                    var q=new System.Numerics.Quaternion(expected["x"]!.GetValue<float>(),expected["y"]!.GetValue<float>(),expected["z"]!.GetValue<float>(),expected["w"]!.GetValue<float>());
                    error=(target.Rotation-q).Length();
                }else error=Math.Max(Math.Abs(target.AnchoredPosition.X-expected["x"]!.GetValue<float>()),Math.Abs(target.AnchoredPosition.Y-expected["y"]!.GetValue<float>()));
                max=Math.Max(max,error);
                if(error>.0001f){Console.Error.WriteLine($"Still sequence mismatch sample {count}, frame {frame}: expected {expected}, error {error}");return 1;}
            }
            seq?.Kill();host?.Stop();
            if(test["events"] is JsonArray expectedEvents){
                for(int i=0;i<Math.Max(events.Count,expectedEvents.Count);i++)if(i>=events.Count||i>=expectedEvents.Count||!JsonNode.DeepEquals(events[i],expectedEvents[i])){
                    Console.Error.WriteLine($"Still callback {i}: actual {(i<events.Count?events[i]:null)}, expected {(i<expectedEvents.Count?expectedEvents[i]:null)}");return 1;
                }
            }
        }
        Console.WriteLine($"Passed {count} still sequence reference frames; maximum error {max:G9}.");return 0;
    }
}
