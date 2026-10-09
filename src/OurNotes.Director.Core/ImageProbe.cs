namespace OurNotes.Director.Core;

public static class ImageProbe
{
    public static object Inspect(string path)
    {
        var p=Png.Decode(File.ReadAllBytes(path));int minX=p.Width,minY=p.Height,maxX=-1,maxY=-1,transparent=0,partial=0;
        for(int y=0;y<p.Height;y++)for(int x=0;x<p.Width;x++){int a=p.Rgba[(y*p.Width+x)*4+3];if(a==0)transparent++;else{minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);if(a<255)partial++;}}
        return JsonOutput.Object(("Width", p.Width), ("Height", p.Height), ("pixelSha256", Archive.Hash(p.Rgba)), ("transparentPixels", transparent), ("partialAlphaPixels", partial), ("visiblePixels", p.Width*p.Height-transparent), ("bounds", maxX<0?null:JsonOutput.Object(("left", minX), ("top", minY), ("right", maxX+1), ("bottom", maxY+1))));
    }
    public static object Compare(string first,string second)
    {
        var a=Png.Decode(File.ReadAllBytes(first));var b=Png.Decode(File.ReadAllBytes(second));if(a.Width!=b.Width||a.Height!=b.Height)throw new ArgumentException("Image sizes differ");int different=0;float maximum=0,alphaMax=0;double squares=0;
        for(int i=0;i<a.Rgba.Length;i+=4){bool diff=false;for(int c=0;c<4;c++){if(a.Rgba[i+c]!=b.Rgba[i+c])diff=true;float delta=c==3?Math.Abs(a.Rgba[i+3]-b.Rgba[i+3]):Math.Abs(a.Rgba[i+c]*a.Rgba[i+3]/255f-b.Rgba[i+c]*b.Rgba[i+3]/255f);maximum=Math.Max(maximum,delta);squares+=delta*delta;}alphaMax=Math.Max(alphaMax,Math.Abs(a.Rgba[i+3]-b.Rgba[i+3]));if(diff)different++;}
        return JsonOutput.Object(("exact", different==0), ("differentPixels", different), ("totalPixels", a.Width*a.Height), ("maxPremultipliedError", maximum), ("maxAlphaError", alphaMax), ("rms", Math.Sqrt(squares/a.Rgba.Length)), ("withinOneLevel", maximum<=1.01f));
    }
}
