using OurNotes.Director.Desktop.Core;
namespace OurNotes.Director.Desktop.Tests;

static class SceneInteractionPreviewTests
{
    public static void Run(Action<bool, string> check)
    {
        check(SceneInteractionPreview.Scale(false, "llvmpipe", 4000, .25) == 1,
            "settled scene preview always restores full quality, including software rendering");
        check(SceneInteractionPreview.Scale(true, "Intel(R) UHD Graphics", 12) == 1,
            "a responsive hardware GPU is not silently treated as a software renderer");
        check(SceneInteractionPreview.Scale(true, "NVIDIA GeForce", 40) == .5,
            "slow hardware scenes receive a transient half-resolution interaction budget");
        check(SceneInteractionPreview.Scale(true, "AMD Radeon", 100) == .25,
            "expensive scene postprocessing receives a bounded quarter-resolution budget");
        check(SceneInteractionPreview.Scale(true, "llvmpipe (LLVM 23)", 1) == .25,
            "software scene dragging uses a bounded drawing cost before the first expensive frame");
        check(SceneInteractionPreview.Scale(true, "GPU", 1, .25) == .25,
            "the drawing budget does not oscillate during one held gesture");
        check(SceneInteractionPreview.Scale(true, "GPU", double.NaN) == 1,
            "missing measurements do not corrupt the preview budget");
        check(SceneInteractionPreview.Scale(true, "GPU", 0, double.NaN) == 1,
            "invalid previous scale cannot escape the supported rendering budgets");
    }
}
