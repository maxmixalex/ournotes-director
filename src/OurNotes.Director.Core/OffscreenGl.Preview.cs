namespace OurNotes.Director.Core;

public sealed unsafe partial class OffscreenGl
{
    bool preferGpu, gpuAttempted;
    GpuPreviewSurface? gpuSurface;
    GlSurface? gpuComposite;
    UnityShader? gpuCompositeShader;
    public bool PreferGpuPresentation { get => preferGpu; set => preferGpu = value; }
    public GpuPreviewSurface? GpuSurface => preferGpu ? gpuSurface : null;
    public string? GpuFallbackReason { get; private set; }

    public void DisableGpuPresentation(string reason)
    {
        preferGpu = false; gpuAttempted = true; GpuFallbackReason = reason;
        gpuSurface?.Dispose(); gpuSurface = null;
    }
    bool TryGpuPresentation(GlSurface source, int width, int height, int scale)
    {
        if (!preferGpu) return false;
        if (gpuSurface is not null && (gpuSurface.Width != width || gpuSurface.Height != height))
        {
            gpuSurface.Dispose(); gpuSurface = null; gpuAttempted = false;
        }
        if (!gpuAttempted)
        {
            gpuAttempted = true;
            GpuPreviewSurface.TryCreate(width, height, out gpuSurface, out var reason);
            GpuFallbackReason = reason;
        }
        if (gpuSurface is null) return false;
        try
        {
            EnsureSurface(ref gpuComposite, width, height);
            gpuCompositeShader ??= new("Opaque editor checkerboard", PreviewCompositeSource);
            Blit(gpuCompositeShader, gpuComposite!.Value,
                new Dictionary<string, float[]> { ["scale"] = [scale], ["outputHeight"] = [height] },
                new Dictionary<string, uint> { ["sourceImage"] = source.Texture });
            if (gpuSurface.Present(gpuComposite.Value.Framebuffer, width, height, out var failure)) return true;
            DisableGpuPresentation(failure ?? "GPU 显示不可用");
        }
        catch (Exception failure) when (failure is InvalidOperationException or NotSupportedException or InvalidDataException or System.Runtime.InteropServices.COMException)
        { DisableGpuPresentation("GPU 显示未能初始化：" + failure.Message); }
        return false;
    }
    // Preview-only composition. It never modifies the exported RGBA framebuffer.
    // The shared texture is opaque because WinUI SwapChainPanel cannot preserve alpha.
    internal const string PreviewCompositeSource = """
        #ifdef VERTEX
        #version 330
        in vec4 in_POSITION0;
        void main(){gl_Position=in_POSITION0;}
        #endif
        #ifdef FRAGMENT
        #version 330
        uniform sampler2D sourceImage;
        uniform int scale;
        uniform int outputHeight;
        out vec4 outputColor;
        void main(){
            ivec2 pixel=ivec2(gl_FragCoord.xy);
            ivec2 origin=pixel*scale;
            vec4 color=vec4(0);
            for(int y=0;y<scale;y++)for(int x=0;x<scale;x++)
                color+=floor(clamp(texelFetch(sourceImage,origin+ivec2(x,y),0),0.0,1.0)*255.0+0.5)/255.0;
            color/=float(scale*scale);
            int tile=(pixel.x/24+(outputHeight-1-pixel.y)/24)%2;
            vec3 checker=tile==0?vec3(57,63,75)/255.0:vec3(43,49,60)/255.0;
            outputColor=vec4(color.rgb+checker*(1.0-color.a),1.0);
        }
        #endif
        """;
}
