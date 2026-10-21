using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>One foreground native construction, including reused model pixels.
/// Retention measures the bounded PNG cache, not total process or GPU memory.</summary>
public sealed record PreviewInitializationDiagnostics(
    double ConstructorMilliseconds,double SourceMilliseconds,
    IReadOnlyDictionary<string,double> NativeInitialization,
    long TextureCacheHits,long TextureDecodes,long TextureSourceBytes,
    long DecodedTextureBytes,double TextureDecodeMilliseconds,
    long RetainedTextureBytes,int RetainedTextures,long TextureCacheLimitBytes)
{
    public JsonObject ToJson()
    {
        var native=new JsonObject();
        foreach(var (key,value) in NativeInitialization)native[key]=value;
        return new JsonObject
        {
            ["constructorMs"]=ConstructorMilliseconds,["sourceMs"]=SourceMilliseconds,["native"]=native,
            ["modelTextures"]=new JsonObject
            {
                ["cacheHits"]=TextureCacheHits,["decodes"]=TextureDecodes,["sourceBytes"]=TextureSourceBytes,
                ["decodedBytes"]=DecodedTextureBytes,["decodeMs"]=TextureDecodeMilliseconds,
                ["retainedBytes"]=RetainedTextureBytes,["retainedTextures"]=RetainedTextures,["limitBytes"]=TextureCacheLimitBytes
            }
        };
    }
}
