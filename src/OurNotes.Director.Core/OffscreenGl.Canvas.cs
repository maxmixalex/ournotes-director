namespace OurNotes.Director.Core;

public sealed unsafe partial class OffscreenGl
{
    // Presentation resizes use another FBO in this same context. Native UI, captures,
    // model textures and the ADV simulation keep their original render dimensions.
    internal readonly record struct CanvasState(int Width,int Height,uint Main,uint MainTexture,
        uint Mask,uint MaskTexture,uint Layer,uint LayerTexture,uint DrawTarget,GlSurface? Blur);

    internal sealed class DrawingCanvas(OffscreenGl owner,CanvasState state) : IDisposable
    {
        internal CanvasState State=state;
        bool disposed;
        public int Width=>State.Width;
        public int Height=>State.Height;
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            owner.ReleaseSurface(new(State.MainTexture,State.Main,State.Width,State.Height));
            owner.ReleaseSurface(new(State.MaskTexture,State.Mask,State.Width,State.Height));
            if(State.Layer!=0)owner.ReleaseSurface(new(State.LayerTexture,State.Layer,State.Width,State.Height));
            if(State.Blur is {} blur)owner.ReleaseSurface(blur);
        }
    }

    CanvasState CaptureCanvas()=>new(Width,Height,main,mainTexture,mask,maskTexture,layer,layerTexture,drawTarget,blurHalf);
    void RestoreCanvas(CanvasState state)
    {
        Width=state.Width;Height=state.Height;main=state.Main;mainTexture=state.MainTexture;
        mask=state.Mask;maskTexture=state.MaskTexture;layer=state.Layer;layerTexture=state.LayerTexture;
        drawTarget=state.DrawTarget;blurHalf=state.Blur;
        Fn<One>("glUseProgram")(program);Fn<Uniform2>("glUniform2f")(Loc("size"),Width,Height);
        Fn<Bind>("glBindFramebuffer")(0x8D40,drawTarget);glViewport(0,0,Width,Height);
        TextureUnit(1,maskTexture);TextureUnit(0,0);
    }
    internal DrawingCanvas CreateDrawingCanvas(int width,int height)
    {
        glGetIntegerv(0x0D33,out int maximum);
        if(width<=0||height<=0||width>maximum||height>maximum)throw new ArgumentException($"Render target exceeds GPU limit {maximum}");
        var previous=CaptureCanvas();GlSurface? hdr=null,clip=null;
        try
        {
            hdr=FloatSurface(width,height);clip=Surface(width,height);
            return new(this,new(width,height,hdr.Value.Framebuffer,hdr.Value.Texture,
                clip.Value.Framebuffer,clip.Value.Texture,0,0,hdr.Value.Framebuffer,null));
        }
        catch
        {
            if(hdr is {} color)ReleaseSurface(color);if(clip is {} maskSurface)ReleaseSurface(maskSurface);throw;
        }
        finally{RestoreCanvas(previous);}
    }
    internal readonly struct CanvasScope : IDisposable
    {
        readonly OffscreenGl owner;
        readonly DrawingCanvas? canvas;
        readonly CanvasState previous;
        internal CanvasScope(OffscreenGl owner,DrawingCanvas? canvas)
        {
            this.owner=owner;this.canvas=canvas;previous=owner.CaptureCanvas();
            if(canvas is not null)owner.RestoreCanvas(canvas.State);
        }
        public void Dispose()
        {
            if(canvas is null)return;
            canvas.State=owner.CaptureCanvas();owner.RestoreCanvas(previous);
        }
    }
    internal CanvasScope UseDrawingCanvas(DrawingCanvas? canvas)=>new(this,canvas);

    void EnsureSurface(ref GlSurface? surface,int width,int height)
    {
        if(surface is {} existing&&existing.Width==width&&existing.Height==height)return;
        var next=Surface(width,height);
        if(surface is {} old)ReleaseSurface(old);
        surface=next;
    }
    void ReleaseSurface(GlSurface surface)
    {
        uint framebuffer=surface.Framebuffer,texture=surface.Texture;
        if(stencilBuffers.Remove(framebuffer,out uint stencil))Fn<Delete>("glDeleteRenderbuffers")(1,ref stencil);
        Fn<Delete>("glDeleteFramebuffers")(1,ref framebuffer);framebuffers.Remove(surface.Framebuffer);
        glDeleteTextures(1,ref texture);textures.Remove(surface.Texture);
    }
}
