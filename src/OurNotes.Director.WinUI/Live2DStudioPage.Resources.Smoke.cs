namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    internal async Task PrepareResourceOpenSmokeAsync()
    {
        await scrubber.FlushAsync(); Pause(); await FlushEditsAsync();
        if (document is not null) SaveTo(App.SmokeEvidencePath("before-resource-open.live2d.json"));
    }
    internal async Task VerifyResourceOpenSmokeAsync(string model, Action<bool, string> check)
    {
        await WaitForReadyAsync();
        check(window.ActivePage == "live2d" && document?.ModelId == model && viewport.HasCurrentSource && viewport.LastFrame is not null,
            "素材库打开按钮将刚下载的模型交给 Live2D 工作台并呈现真实原生画面");
        await App.CaptureCheckpointAsync("resource-model-opened");
    }
}
