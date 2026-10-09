using System.Numerics;

namespace OurNotes.Director.Core;

// Deterministic noise field used by the open-source reference; Unity's private field is not exported.
public static class AdvParticleNoise
{
    static readonly int[] p=Permutation();
    static int[] Permutation(){var random=new AdvRandom(0);var a=Enumerable.Range(0,256).ToArray();for(int i=255;i>0;i--){int j=(int)(random.Next()%(i+1));(a[i],a[j])=(a[j],a[i]);}return Enumerable.Range(0,512).Select(i=>a[i&255]).ToArray();}
    static double Fade(double t)=>t*t*t*(t*(t*6-15)+10);
    static double Lerp(double a,double b,double t)=>a+(b-a)*t;
    static double N1(double x){int ix=(int)Math.Floor(x)&255;double f=x-Math.Floor(x);static double G(int h,double d)=>((h&1)!=0?-d:d)*(1+((h>>1)&7))/8;return Math.Clamp(2*Lerp(G(p[ix],f),G(p[ix+1],f-1),Fade(f)),-1,1);}
    static double N2(double x,double y){int ix=(int)Math.Floor(x)&255,iy=(int)Math.Floor(y)&255;double fx=x-Math.Floor(x),fy=y-Math.Floor(y);static double G(int h,double dx,double dy){double a=(h&7)*Math.PI/4;return Math.Cos(a)*dx+Math.Sin(a)*dy;}
        return Math.Clamp(Math.Sqrt(2)*Lerp(Lerp(G(p[p[ix]+iy],fx,fy),G(p[p[ix+1]+iy],fx-1,fy),Fade(fx)),Lerp(G(p[p[ix]+iy+1],fx,fy-1),G(p[p[ix+1]+iy+1],fx-1,fy-1),Fade(fx)),Fade(fy)),-1,1);}
    static double N3(double x,double y,double z){int ix=(int)Math.Floor(x)&255,iy=(int)Math.Floor(y)&255,iz=(int)Math.Floor(z)&255;double fx=x-Math.Floor(x),fy=y-Math.Floor(y),fz=z-Math.Floor(z),u=Fade(fx),v=Fade(fy),w=Fade(fz);
        static double G(int h,double a,double b,double c){h&=15;double x=h<8?a:b,y=h<4?b:h is 12 or 14?a:c;return ((h&1)!=0?-x:x)+((h&2)!=0?-y:y);}
        int a=p[ix]+iy,aa=p[a]+iz,ab=p[a+1]+iz,b=p[ix+1]+iy,ba=p[b]+iz,bb=p[b+1]+iz;
        return Math.Clamp(Lerp(Lerp(Lerp(G(p[aa],fx,fy,fz),G(p[ba],fx-1,fy,fz),u),Lerp(G(p[ab],fx,fy-1,fz),G(p[bb],fx-1,fy-1,fz),u),v),Lerp(Lerp(G(p[aa+1],fx,fy,fz-1),G(p[ba+1],fx-1,fy,fz-1),u),Lerp(G(p[ab+1],fx,fy-1,fz-1),G(p[bb+1],fx-1,fy-1,fz-1),u),v),w),-1,1);
    }
    public static Vector3 Sample(Vector3 q,int quality)=>quality switch{
        0=>new((float)N1(q.X+q.Y+q.Z),(float)N1(q.X+q.Y+q.Z+31.416+101.3),(float)N1(q.X+q.Y+q.Z+57.1+202.6)),
        1=>new((float)N2(q.Y,q.Z),(float)N2(q.Z+31.416,q.X+17.32),(float)N2(q.X+57.1,q.Y+91.7)),
        _=>new((float)N3(q.X,q.Y,q.Z),(float)N3(q.X+31.416,q.Y+17.32,q.Z+5.77),(float)N3(q.X+57.1,q.Y+91.7,q.Z+43.3))};
}
