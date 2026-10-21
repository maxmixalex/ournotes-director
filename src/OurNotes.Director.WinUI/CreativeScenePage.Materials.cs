using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    void BuildMotionExpressionInspector(JsonObject clip)
    {
        string id = S(clip, "id"); long version = inspectorVersion;
        bool queued = N(clip["data"]!.AsObject(), "MotionWait") > 0;
        var keep = new CheckBox { Content = "动作切换不重置表情", IsChecked = CreativeMaterialEditing.KeepsExpression(clip), IsEnabled = !queued };
        ToolTipService.SetToolTip(keep, queued ? "这段原作动作延后执行，保留其原始表情设置。"
            : "开启时，动作沿用表情轨道；关闭时，完全保留 Motion 指令中的表情设置。原剧情导入默认关闭。");
        void Apply(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
        {
            if (version == inspectorVersion)
                _ = Safe(() => EditAsync(() => CreativeMaterialEditing.SetKeepsExpression(document, id, keep.IsChecked == true)));
        }
        keep.Checked += Apply; keep.Unchecked += Apply;
        inspector.Children.Add(keep);
    }
    async Task VerifyMaterialAuthoringAsync(Action<bool, string> check, string actor, string motion, string expression, string defaultExpression)
    {
        selectedActor = actor; selectedClip = null; replaceAssetClip = null;
        assetKind.SelectedIndex = 0; ShowProjectAssets(); await LoadAssetsAsync();
        assets.SelectedItem = assets.Items.OfType<ListViewItem>().First(item => item.Tag is OurNotes.Director.Desktop.Core.ResultRow row && row.Id == motion);
        await SeekAsync(1); UpdateLayout();
        check(ReferenceEquals(projectItems.Content, assets) && assets.ActualHeight >= 45,
            "项目素材的动作分页显示真实资源列表，四面板布局下仍有可操作高度");
        string beforeInsert = document.ToJson();
        await viewport.SendControlKeySmokeAsync(window, assets, Windows.System.VirtualKey.Enter);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((document.ToJson() == beforeInsert || previewDirty) && DateTime.UtcNow < deadline) await Task.Delay(30);
        await loadGate.WaitAsync(); loadGate.Release();
        check(selectedClip is not null && document.ToJson() != beforeInsert && !previewDirty,
            "素材列表实际Enter键将选定动作插入播放头，通过普通编辑流程提交到原生预览");
        string id = selectedClip!;
        await Task.Delay(40);
        check(timeline.IsSelectedLaneVisible, "插入动作后，选中的新动作轨道自动滚入视野，不需要手动寻找");
        await SeekAsync(2.5); BuildInspector();
        check(viewport.LastFrame?.ActorAnimations?.Any(a => a.TargetId == actor && a.Motion == motion && a.Expression == expression) == true,
            "从素材栏在已有表情后插入动作，实际原生人物继续保持表情轨道，而不是恢复默认脸");
        async Task ToggleAsync(bool enabled)
        {
            inspector.Children.OfType<CheckBox>().Single(b => b.Content?.ToString() == "动作切换不重置表情").IsChecked = enabled;
            await loadGate.WaitAsync(); loadGate.Release(); await Task.Delay(30);
        }
        await ToggleAsync(false);
        check(viewport.LastFrame?.ActorAnimations?.Any(a => a.TargetId == actor && a.Expression == defaultExpression) == true
            && !CreativeMaterialEditing.KeepsExpression(document.Clip(id)),
            "关闭实际表情控件后恢复原指令的默认表情行为，不修改 Motion 执行器");
        await ToggleAsync(true);
        check(viewport.LastFrame?.ActorAnimations?.Any(a => a.TargetId == actor && a.Expression == expression) == true,
            "重新开启实际控件后恢复表情，编排规则与原生绘制保持对应");
        await App.CaptureCheckpointAsync("motion-expression-authoring");
    }

}
