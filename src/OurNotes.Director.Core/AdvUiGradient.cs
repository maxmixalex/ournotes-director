using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

// Fwk.UI.UIGradientImage: split in local space before applying RectTransform and inherited alpha.
public static class AdvUiGradient
{
    public sealed record Vertex(double X,double Y,double U,double V,double[] InitialColor)
    {public double[] Color{get;set;}=InitialColor;}
    public static double Color32(double value)=>Math.Round((float)(Math.Clamp(value,0,1)*255),MidpointRounding.ToEven);
    static double D(JsonNode? node)=>node?.GetValue<double>()??0;
    public static List<Vertex> Apply(IReadOnlyList<Vertex> stream,JsonNode descriptor)
    {
        if(stream.Count%3!=0)throw new ArgumentException("Gradient requires a triangle stream",nameof(stream));
        if(stream.Count==0)return [];
        var g=descriptor["gradient"]!;int mode=(int)D(descriptor["blendMode"]);
        if(mode is not (0 or 1)||D(g["m_Mode"])!=0||D(g["m_ColorSpace"]) is not (0 or -1))throw new NotSupportedException("UI gradient mode or color space");
        double angle=D(descriptor["angleDeg"])*Math.PI/180,dx=Math.Cos(angle),dy=Math.Sin(angle);
        double Dot(Vertex v)=>v.X*dx+v.Y*dy;
        double lo=stream.Min(Dot),hi=stream.Max(Dot);
        var times=new[]{("ctime","m_NumColorKeys"),("atime","m_NumAlphaKeys")}.Select(pair=>Enumerable.Range(0,(int)D(g[pair.Item2])).Select(i=>(double)(float)(D(g[pair.Item1+i])/65535)).ToArray()).ToArray();
        var tri=stream.Select(v=>v with{}).ToList();
        if(Actor.B(descriptor["splitAtKeysWhenAxisAligned"])&&(Math.Abs(dx)<1e-5||Math.Abs(dy)<1e-5)&&hi>lo)
            foreach(double t in times.SelectMany(t=>t))tri=Split(tri,Dot,lo+(hi-lo)*t);
        double Sample(int channel,double t)
        {
            var ts=times[channel==3?1:0];if(ts.Length==0)throw new InvalidDataException("UI gradient without keys");
            string name=new[]{"r","g","b","a"}[channel];double Value(int i)=>D(g["key"+i]?[name]);
            if(t<=ts[0])return Value(0);if(t>=ts[^1])return Value(ts.Length-1);
            int j=1;while(ts[j]<t)j++;double u=ts[j]>ts[j-1]?(t-ts[j-1])/(ts[j]-ts[j-1]):0;
            return Value(j-1)+(Value(j)-Value(j-1))*u;
        }
        // The reference shares cut vertices between triangles. Its multiply modifier revisits those
        // objects in stream order, including repeated quantization; retain that behavior for parity.
        foreach(var v in tri){double t=hi>lo?Math.Clamp((Dot(v)-lo)/(hi-lo),0,1):0;
            v.Color=Enumerable.Range(0,4).Select(c=>Color32(Sample(c,t)*(mode==1?v.Color[c]/255:1))).ToArray();}
        return tri;
    }
    static List<Vertex> Split(List<Vertex> tri,Func<Vertex,double> dot,double cut)
    {
        const double eps=1e-6;var result=new List<Vertex>();
        static Vertex Lerp(Vertex a,Vertex b,double t)=>new(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t,a.U+(b.U-a.U)*t,a.V+(b.V-a.V)*t,a.Color.Select((c,i)=>c+(b.Color[i]-c)*t).ToArray());
        for(int i=0;i<tri.Count;i+=3){Vertex[] p=[tri[i],tri[i+1],tri[i+2]];var d=p.Select(v=>dot(v)-cut).ToArray();
            if(d.All(v=>v>=-eps)||d.All(v=>v<=eps)){result.AddRange(p);continue;}
            int j=0;for(;j<3;j++)if(Math.Abs(d[j])>eps&&Math.Sign(d[j])!=Math.Sign(d[(j+1)%3])&&Math.Sign(d[j])!=Math.Sign(d[(j+2)%3]))break;
            if(j==3){result.AddRange(p);continue;}
            var a=p[j];var b=p[(j+1)%3];var c=p[(j+2)%3];double da=d[j],db=d[(j+1)%3],dc=d[(j+2)%3];
            var ab=Math.Abs(db)<=eps?b with{}:Lerp(a,b,da/(da-db));var ac=Math.Abs(dc)<=eps?c with{}:Lerp(a,c,da/(da-dc));result.AddRange([a,ab,ac,ab,b,c,ab,c,ac]);
        }return result;
    }
}
