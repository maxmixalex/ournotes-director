using OurNotes.Director.Desktop.Core;

static class PreviewResolutionTests
{
    public static void Run(Action<bool, string> check)
    {
        check(PreviewResolution.Choose(1920,1080,3840,2160,fullscreen:true,supersample:1)==(3840,2160,1),
            "Fullscreen renders the native 4K display grid without the editor-pane pixel limit");
        check(PreviewResolution.Choose(1920,1080,2560,1440,1.5,fullscreen:true)==(3840,2160,2),
            "Fullscreen native dimensions include the monitor rasterization scale");
        check(PreviewResolution.Choose(1000,2000,1920,1080,fullscreen:true,supersample:1)==(540,1080,1),
            "Portrait fullscreen preserves the project aspect and its display letterboxing");
        check(PreviewResolution.Choose(1920,1080,1920,1080,zoom:2,fullscreen:true,supersample:1)==(3840,2160,1),
            "Fullscreen rendering covers display zoom instead of stretching a smaller image");
        var pane=PreviewResolution.Choose(1920,1080,3840,2160);
        check((long)pane.Width*pane.Height<=2_101_000&&pane.Supersample==2,
            "Ordinary editor panes retain their bounded adaptive rendering budget");
        check(PreviewResolution.Choose(1920,1080,1000.2,800,1.25,fullscreen:true,supersample:1)==(1251,704,1),
            "Fractional display pixels are covered by the native render target");
    }
}
