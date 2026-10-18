using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    async Task<ParameterControl> BringParameterIntoViewSmokeAsync(string id)
    {
        if (activeTab != "parameters") SelectInspector("parameters");
        int index = Array.IndexOf(parameterIds, id);
        if (index < 0) throw new InvalidOperationException("参数列表缺少 " + id);
        UpdateLayout();
        if (parameterControls.TryGetValue(id, out var visible) && ParameterRowIsVisible(visible)) return visible;
        // The native repeater realizes this item, then the normal bring-into-view
        // route scrolls its actual ScrollViewer. No data is filtered out for tests.
        parameterRepeater.GetOrCreateElement(index).StartBringIntoView(new BringIntoViewOptions
        { AnimationDesired = false, VerticalAlignmentRatio = .5 });
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            await Task.Delay(20); UpdateLayout();
            if (parameterControls.TryGetValue(id, out visible) && ParameterRowIsVisible(visible))
            { RefreshParameterValues(); return visible; }
        }
        while (DateTime.UtcNow < deadline);
        throw new InvalidOperationException("参数没有滚入实际视口：" + id);
    }

    async Task<ParameterControl> ScrollParametersToEndSmokeAsync(bool end)
    {
        if (activeTab != "parameters") SelectInspector("parameters");
        UpdateLayout();
        var scroll = parameterScroll ?? throw new InvalidOperationException("参数滚动区域尚未创建。");
        var peer = new ScrollViewerAutomationPeer(scroll);
        var provider = peer.GetPattern(PatternInterface.Scroll) as IScrollProvider
            ?? throw new InvalidOperationException("参数滚动区域未提供真实 UI Automation Scroll 模式。");
        string id = end ? parameterIds[^1] : parameterIds[0];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            provider.SetScrollPercent(-1, end ? 100 : 0);
            await Task.Delay(40); UpdateLayout();
            if (parameterControls.TryGetValue(id, out var control) && ParameterRowIsVisible(control)
                && (!end || scroll.VerticalOffset > scroll.ViewportHeight)) return control;
        }
        while (DateTime.UtcNow < deadline);
        throw new InvalidOperationException("实际参数滚动条未到达" + (end ? "最后" : "第一") + "项。");
    }

    async Task RunParameterVirtualizationSmokeAsync(Action<bool, string> check)
    {
        SelectInspector("parameters"); UpdateLayout(); await ReadParametersAsync();
        var current = document!;
        var parameters = current.Parameters;
        check(parameters.Count > 32 && parameterIds.SequenceEqual(parameters.Select(value => value.Id), StringComparer.Ordinal)
            && parameterRepeater.ItemsSourceView.Count == parameters.Count,
            "虚拟参数列表保留全部原生参数数据，滚动不筛掉任何模型参数");
        int initialCreated = parameterRowsCreated, initialReused = parameterRowsReused;
        var samples = new JsonArray();
        var report = new JsonObject
        { ["parameterCount"] = parameters.Count, ["lastId"] = parameterIds[^1], ["samples"] = samples };
        void WriteReport() => DesktopSettings.AtomicWrite(App.SmokeEvidencePath("live2d-parameter-virtualization.json"),
            report.ToJsonString(DesktopSettings.JsonOptions));
        void Record(string position)
        {
            int visual = VisualTreeHelper.GetChildrenCount(parameterRepeater);
            var realized = new JsonArray();
            foreach (var (id, row) in parameterControls.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                realized.Add(new JsonObject { ["id"] = id, ["instance"] = row.Instance, ["bindings"] = row.Bindings });
            samples.Add(new JsonObject
            {
                ["position"] = position, ["realized"] = parameterControls.Count,
                ["visualChildren"] = visual, ["created"] = parameterRowsCreated - initialCreated,
                ["reused"] = parameterRowsReused - initialReused,
                ["verticalOffset"] = parameterScroll!.VerticalOffset, ["ids"] = realized
            });
            WriteReport();
            check(parameterControls.Count is > 0 and <= 32 && visual is > 0 and <= 32
                && parameterControls.Count <= visual
                && parameterControls.All(pair => pair.Key == pair.Value.Id && ReferenceEquals(pair.Value.Document, current)),
                "参数列表在" + position + "只保留至多32行真实控件，回收行不残留旧参数绑定");
        }

        // This test runs after the existing drag/playback recordings, so the extra
        // scrolling and edits cannot precondition their GC/latency measurements.
        viewport.Focus(FocusState.Programmatic);
        await ScrollParametersToEndSmokeAsync(false); Record("顶部");
        await ScrollParametersToEndSmokeAsync(true); Record("底部");
        check(parameterControls.ContainsKey(parameterIds[^1]) && parameterSearch.Text.Length == 0,
            "真实UIA滚动条到达完整列表最后一个参数，未用搜索或全量控件假装虚拟化");
        await ScrollParametersToEndSmokeAsync(false); Record("编辑前滚回顶部");
        await ScrollParametersToEndSmokeAsync(true); Record("编辑前再次到底部");

        var last = parameters.Last(value => value.Maximum > value.Minimum);
        var control = await BringParameterIntoViewSmokeAsync(last.Id);
        check(control.Bindings > 1 && parameterRowsReused > initialReused,
            "末端参数编辑使用原生模板已回收并重新绑定的同一行实例");
        var before = await viewport.ReadActorParametersAsync(current.ActorId);
        var automatic = before.Single(value => value.Id == last.Id);
        check(!automatic.IsOverridden, "末端参数编辑回归从独立模型的自动参数开始");
        string original = current.ToJson();
        double span = last.Maximum - last.Minimum;
        double value = last.Minimum + span * .37;
        if (Math.Abs(value - automatic.Value) < span * .05) value = last.Minimum + span * .79;
        control.Number.Focus(FocusState.Programmatic);
        control.Number.Value = value;
        await FlushEditsAsync(); await ReadParametersAsync();
        var pinnedValues = await viewport.ReadActorParametersAsync(current.ActorId);
        var pinned = pinnedValues.Single(item => item.Id == last.Id);
        check(pinned.IsOverridden && Math.Abs(pinned.Value - value) < 1e-4
            && control.Id == last.Id && HasFocusWithin(control.Number)
            && AutomationProperties.GetName(control.Number) == "参数数值 " + last.Id
            && pinnedValues.Where(item => item.Id != last.Id).All(item => item.IsOverridden == before.Single(old => old.Id == item.Id).IsOverridden),
            "复用后的末端数值框固定正确原生参数，保持输入焦点且不误改旧ID");
        // Move focus to the reset button as a real click would, allowing the
        // observer to display the released value in the formerly edited NumberBox.
        control.Reset.Focus(FocusState.Programmatic);
        new ButtonAutomationPeer(control.Reset).Invoke();
        await FlushEditsAsync(); await ReadParametersAsync();
        var released = (await viewport.ReadActorParametersAsync(current.ActorId)).Single(item => item.Id == last.Id);
        check(!released.IsOverridden && Math.Abs(released.Value - automatic.Value) < 1e-4
            && control.Mode.Text == "自动" && !control.Reset.IsEnabled,
            "复用行的真实恢复自动按钮释放当前ID，显示同帧原生自动值");

        await App.CaptureCheckpointAsync("live2d-parameters-last");
        viewport.Focus(FocusState.Programmatic);
        await ScrollParametersToEndSmokeAsync(false); Record("滚回顶部");
        await ScrollParametersToEndSmokeAsync(true); Record("再次到底部");
        await ScrollParametersToEndSmokeAsync(false); Record("再次滚回顶部");
        check(parameterRowsReused > initialReused && parameterRowsCleared > 0 && parameterControls.Count < parameters.Count,
            "反复滚动复用同一批参数行，realized字典没有逐步积累全部参数控件");
        // The NumberBox pin and reset are two real undoable edits. Restore both
        // before the independent standalone export and canvas smoke continue.
        Undo(false); Undo(false); await FlushEditsAsync();
        check(current.ToJson() == original, "参数虚拟化回归结束恢复原项目内容与原始参数状态");
        report["editedId"] = last.Id; report["editedInstance"] = control.Instance;
        report["editedRowBindings"] = control.Bindings;
        report["minimum"] = last.Minimum; report["maximum"] = last.Maximum; report["editedValue"] = value;
        report["nativePinnedValue"] = pinned.Value; report["nativeReleasedValue"] = released.Value;
        report["created"] = parameterRowsCreated - initialCreated; report["reused"] = parameterRowsReused - initialReused;
        WriteReport();
    }
}
