using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class UiGradientTests
{
    public static int Run(string fixture)
    {
        try{
            int count=0;void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
            foreach(var c in Archive.ReadJson(fixture).AsArray().OfType<JsonNode>()){
                static double N(JsonNode? n)=>n!.GetValue<double>();
                var input=c["input"]!.AsArray().Select(v=>new AdvUiGradient.Vertex(N(v!["x"]),N(v["y"]),N(v["u"]),N(v["v"]),v["c"]!.AsArray().Select(N).ToArray())).ToArray();
                var actual=AdvUiGradient.Apply(input,c["descriptor"]!);var expected=c["expected"]!.AsArray();Check(actual.Count==expected.Count,"gradient split count");
                for(int i=0;i<actual.Count;i++){var a=actual[i];var e=expected[i]!;double[] values=[a.X,a.Y,a.U,a.V];for(int j=0;j<4;j++)Check(Math.Abs(values[j]-N(e[new[]{"x","y","u","v"}[j]]))<1e-8,"gradient geometry/UV");for(int j=0;j<4;j++)Check(a.Color[j]==N(e["c"]![j]),$"gradient color32 vertex {i} channel {j}");}
            }
            Console.WriteLine($"Passed {count} UI gradient source comparisons.");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
