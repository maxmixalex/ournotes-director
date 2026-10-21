using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    async Task VerifyNavigationWorkflowAsync(Action<bool, string> check, string first, string second)
    {
        await viewport.PauseForEditingAsync(); viewport.FitCanvas();
        string beforeSelection = document.ToJson();
        // A zero-distance drag injects a real left click. It must transfer keyboard
        // focus from the material inspector without creating a placement edit.
        await viewport.SendDragSmokeAsync(window, first, 0, 0);
        check(selectedActor == first && viewport.SelectedTargetId == first && document.ToJson() == beforeSelection,
            "实际左键选择人物并取得画布焦点，点击本身不改写项目");

        var frame = viewport.LastFrame!;
        int sourceFrame = frame.Frame;
        var firstBefore = frame.Targets.Single(t => t.Id == first).StagePlacement!;
        var secondBefore = frame.Targets.Single(t => t.Id == second).StagePlacement!;
        var viewBefore = viewport.NavigationStateForSmoke;
        string projectBefore = document.ToJson();
        await viewport.SendWheelSmokeAsync(window, second, 120);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while ((document.ToJson() == projectBefore || previewDirty) && DateTime.UtcNow < deadline
            && !(notice.IsOpen && notice.Severity == InfoBarSeverity.Error)) await Task.Delay(30);
        await loadGate.WaitAsync(); loadGate.Release();

        var firstAfter = viewport.LastFrame!.Targets.Single(t => t.Id == first).StagePlacement!;
        var secondAfter = viewport.LastFrame.Targets.Single(t => t.Id == second).StagePlacement!;
        check(document.ToJson() != projectBefore && !previewDirty && selectedActor == first && viewport.SelectedTargetId == first
            && Math.Abs(firstAfter.Position.Z - firstBefore.Position.Z + Math.Log(1.1) * 2) < 1e-5
            && firstAfter.Position.X == firstBefore.Position.X && firstAfter.Position.Y == firstBefore.Position.Y
            && secondAfter.Position == secondBefore.Position && viewport.LastFrame.Frame == sourceFrame,
            "指针悬在另一个人物上滚动，仍只改变已选人物的原作深度，另一个人物及演出时间保持不变");
        check(viewport.NavigationStateForSmoke == viewBefore,
            "选中人物时普通滚轮不改变画布缩放或平移");
        var wheelPixels = viewport.LastFrame.Pixels.Rgba.ToArray();
        await SeekAsync(sourceFrame / 30d);
        check(wheelPixels.Length > 0 && viewport.LastFrame!.Pixels.Rgba.SequenceEqual(wheelPixels),
            "系统滚轮产生的实际原生图像与保存指令重新播放逐像素一致");
        await App.CaptureCheckpointAsync("native-wheel-placement");

        projectBefore = document.ToJson(); viewBefore = viewport.NavigationStateForSmoke;
        var anchor = await viewport.SendWheelSmokeAsync(window, second, 120, control: true);
        var viewAfter = viewport.NavigationStateForSmoke;
        double x = (anchor.X - viewBefore.Canvas.X) / viewBefore.Canvas.Width;
        double y = (anchor.Y - viewBefore.Canvas.Y) / viewBefore.Canvas.Height;
        check(document.ToJson() == projectBefore && viewport.SelectedTargetId == first && selectedActor == first
            && viewAfter.Zoom > viewBefore.Zoom && viewport.LastFrame!.Targets.Single(t => t.Id == first).StagePlacement!.Position == firstAfter.Position,
            "Ctrl+滚轮只放大编辑器视图，保留人物选择且不改写人物位置和导出构图");
        check(Math.Abs(viewAfter.Canvas.X + x * viewAfter.Canvas.Width - anchor.X) < 1
            && Math.Abs(viewAfter.Canvas.Y + y * viewAfter.Canvas.Height - anchor.Y) < 1,
            "实际滚轮缩放以鼠标位置为锚点，鼠标下的画布位置不跳动");

        await viewport.SendViewportKeySmokeAsync(window, VirtualKey.F);
        var fitted = viewport.NavigationStateForSmoke;
        check(fitted.Zoom == 1 && fitted.Pan.X == 0 && fitted.Pan.Y == 0 && viewport.SelectedTargetId == first
            && document.ToJson() == projectBefore,
            "实际 F 键使视图适应窗口并清除平移，保留当前人物和场景内容");
        await viewport.SendBlankClickSmokeAsync(window, outsideCanvas: true);
        check(viewport.SelectedTargetId is null && selectedActor is null && document.ToJson() == projectBefore,
            "点击画布外灰色边缘能清除人物选择，检查器随之回到场景");
        await viewport.SendWheelSmokeAsync(window, second, 120);
        check(viewport.SelectedTargetId is null && selectedActor is null && viewport.NavigationStateForSmoke.Zoom > 1
            && document.ToJson() == projectBefore,
            "未选人物时普通滚轮缩放画布，不会因为悬停在人物上而自动选择或移动它");

        await viewport.SendDragSmokeAsync(window, first, 0, 0);
        viewBefore = viewport.NavigationStateForSmoke;
        await viewport.SendViewportKeySmokeAsync(window, VirtualKey.Escape);
        check(viewport.SelectedTargetId is null && selectedActor is null && viewport.NavigationStateForSmoke == viewBefore
            && document.ToJson() == projectBefore,
            "实际 Esc 键仅取消画布选择，不改变画面或项目");
        await viewport.SendDragSmokeAsync(window, first, 0, 0);
        await viewport.SendViewportKeySmokeAsync(window, VirtualKey.F);
        check(viewport.SelectedTargetId == first && viewport.NavigationStateForSmoke.Zoom == 1 && !notice.IsOpen,
            "导航后可继续选择人物并恢复完整视图，供后续图片与视频导出");
    }
}
