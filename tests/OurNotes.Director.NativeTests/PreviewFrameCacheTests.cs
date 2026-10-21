using System.Numerics;
using OurNotes.Director.Core;

static class PreviewFrameCacheTests
{
    public static void Run(Action<bool,string> check)
    {
        var rgba=Enumerable.Range(0,32*24*4).Select(i=>(byte)(i/31)).ToArray();
        var matrix=AdvMatrix.Identity();
        var targets=new[]{new PreviewTarget("actor","Actor",.1,.2,.3,.4,new(5,Vector3.Zero,Vector3.One,matrix))};
        var frame=new PreviewFrameCache.Frame(33,new(32,24,rgba),1,targets,
            [new("actor","native",true,.375f,null)],[new("actor","model","motion","expression",.125)]);
        var cache=new PreviewFrameCache(16*1024);
        cache.Store(frame);
        rgba[0]=199;matrix[0]=123;targets[0]=targets[0] with{Label="Changed"};
        check(cache.TryGet(33,.25,out var restored)&&restored!.Number==33&&restored.Scale==1
            &&restored.Pixels.Rgba[0]==0&&restored.Targets[0].Label=="Actor"&&restored.Targets[0].StagePlacement!.LocalToClip[0]==1,
            "Exact preview history owns its RGBA, collection and stage projection snapshots");
        restored!.Pixels.Rgba[0]=200;restored.Targets[0].StagePlacement!.LocalToClip[0]=321;
        check(cache.TryGet(33,1,out var again)&&again!.Pixels.Rgba[0]==0&&again.Targets[0].StagePlacement!.LocalToClip[0]==1
            &&again.LipSync.SequenceEqual(frame.LipSync)&&again.Animations.SequenceEqual(frame.Animations),
            "Mutating a returned preview frame cannot poison later cache hits or exact animation and lip metadata");
        check(!cache.TryGet(32,.25,out _),"Preview history never substitutes a neighboring frame for a missing target");
        var quarter=frame with{Number=34,Scale=.25};cache.Store(quarter);
        check(!cache.TryGet(34,.5,out _)&&cache.TryGet(34,.25,out _),
            "A quarter-quality frame cannot satisfy a half-quality or full-quality request");
        cache.Store(quarter with{Scale=1});cache.Store(quarter);
        check(cache.TryGet(34,1,out var upgraded)&&upgraded!.Scale==1,
            "A higher-quality exact frame upgrades its entry and cannot be replaced by a lower-quality draw");
        cache.Store(frame with{Number=35,Pixels=new(32,24,[])});
        check(!cache.Contains(35,.25),"A GPU surface without an actual CPU snapshot is never retained as a cached image");
        var small=frame with{Pixels=new(8,8,new byte[8*8*4])};
        var sizing=new PreviewFrameCache(16384);sizing.Store(small);long one=sizing.RetainedBytes;
        var bounded=new PreviewFrameCache(one*2+32);
        bounded.Store(small with{Number=1});bounded.Store(small with{Number=2});
        bounded.TryGet(1,1,out _);bounded.Store(small with{Number=3});
        check(bounded.Contains(1,1)&&bounded.Contains(3,1)&&!bounded.Contains(2,1)&&bounded.RetainedBytes<=bounded.LimitBytes,
            "Least-recently-used exact frames are evicted within the byte budget, including geometry metadata");
        bounded.Clear();
        check(bounded.Count==0&&bounded.RetainedBytes==0&&!bounded.TryGet(1,.25,out _),
            "Project, edit and resize invalidation releases every retained frame");
        var disabled=new PreviewFrameCache(0);disabled.Store(frame);
        check(disabled.Count==0&&disabled.RetainedBytes==0,"A zero history budget performs no retention");
        var large=frame with{Pixels=new(128,128,new byte[128*128*4])};bounded.Store(large);
        check(bounded.Count==0,"An oversized image is rejected before allocating compression working buffers");
        var many=new PreviewFrameCache();
        for(int i=0;i<=1024;i++)many.Store(new(i,new(1,1,[(byte)i,0,0,255]),1,[],[],[]));
        check(many.Count==1024&&!many.Contains(0,1)&&many.Contains(1024,1)&&many.RetainedBytes<=many.LimitBytes,
            "The entry-count limit also bounds metadata for tiny highly compressible images");
        check(many.Ranges().SequenceEqual(new[]{new PreviewCachedFrameRange(1,1024)}),
            "Retained ranges describe actual contiguous frames after eviction");
        check(cache.TryGet(33,1,out var pooled)&&pooled!.Pixels.Rgba[0]==0&&pooled.Targets[0].StagePlacement!.LocalToClip[0]==1,
            "Repeated pooled compression-buffer reuse leaves earlier retained bytes and geometry independent");
    }
}
