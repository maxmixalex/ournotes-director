using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
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

    async Task<ParameterControl> ScrollParametersToEndSmokeAsync(bool end, Action<JsonObject>? record = null)
    {
        if (activeTab != "parameters") SelectInspector("parameters");
        UpdateLayout();
        var scroll = parameterScroll ?? throw new InvalidOperationException("参数滚动区域尚未创建。");
        var peer = new ScrollViewerAutomationPeer(scroll);
        var provider = peer.GetPattern(PatternInterface.Scroll) as IScrollProvider
            ?? throw new InvalidOperationException("参数滚动区域未提供真实 UI Automation Scroll 模式。");
        string id = end ? parameterIds[^1] : parameterIds[0];
        var attempts = new JsonArray();
        var trace = new JsonObject { ["target"] = id, ["end"] = end, ["attempts"] = attempts };
        int attempt = 0;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            provider.SetScrollPercent(-1, end ? 100 : 0);
            await Task.Delay(40); UpdateLayout();
            // Keep the first and the most recent attempts, including the last
            // actual native geometry if scrolling times out.
            if (attempts.Count == 16) attempts.RemoveAt(1);
            var state = CaptureParameterScrollSmokeState(scroll);
            state["attempt"] = ++attempt; attempts.Add(state);
            if (parameterControls.TryGetValue(id, out var control) && ParameterRowIsVisible(control)
                && (!end || scroll.VerticalOffset > scroll.ViewportHeight))
            { trace["completed"] = true; record?.Invoke(trace); return control; }
        }
        while (DateTime.UtcNow < deadline);
        trace["completed"] = false; record?.Invoke(trace);
        await App.CaptureCheckpointAsync("live2d-parameters-scroll-failed");
        throw new InvalidOperationException("实际参数滚动条未到达" + (end ? "最后" : "第一") + "项。");
    }

    JsonObject CaptureParameterScrollSmokeState(ScrollViewer scroll)
    {
        static double? Finite(double value) => double.IsFinite(value) ? value : null;
        var rows = new JsonArray();
        int nativeChildren = VisualTreeHelper.GetChildrenCount(parameterRepeater);
        for (int index = 0; index < Math.Min(32, nativeChildren); index++)
        {
            if (VisualTreeHelper.GetChild(parameterRepeater, index) is not ContentControl host) continue;
            var row = host.Content as ParameterControl;
            double? y = null;
            string? positionError = null;
            try
            {
                if (host.IsLoaded) y = Finite(host.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y);
            }
            catch (Exception error) { positionError = error.Message; }
            rows.Add(new JsonObject
            {
                ["index"] = parameterRepeater.GetElementIndex(host), ["id"] = row?.Id,
                ["instance"] = row?.Instance, ["bindings"] = row?.Bindings,
                ["hostLoaded"] = host.IsLoaded, ["rowLoaded"] = row?.IsLoaded,
                ["height"] = Finite(host.ActualHeight), ["y"] = y, ["positionError"] = positionError
            });
        }
        return new JsonObject
        {
            ["verticalOffset"] = Finite(scroll.VerticalOffset), ["scrollableHeight"] = Finite(scroll.ScrollableHeight),
            ["extentHeight"] = Finite(scroll.ExtentHeight), ["viewportHeight"] = Finite(scroll.ViewportHeight),
            ["scrollLoaded"] = scroll.IsLoaded, ["repeaterLoaded"] = parameterRepeater.IsLoaded,
            ["repeaterHeight"] = Finite(parameterRepeater.ActualHeight), ["realized"] = parameterControls.Count,
            ["nativeChildren"] = nativeChildren, ["rows"] = rows
        };
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
        var scrolls = new JsonArray();
        var report = new JsonObject
        { ["parameterCount"] = parameters.Count, ["lastId"] = parameterIds[^1], ["samples"] = samples, ["scrolls"] = scrolls };
        void WriteReport() => DesktopSettings.AtomicWrite(App.SmokeEvidencePath("live2d-parameter-virtualization.json"),
            report.ToJsonString(DesktopSettings.JsonOptions));
        void RecordScroll(JsonObject trace) { scrolls.Add(trace); WriteReport(); }
        void Record(string position)
        {
            var realized = new JsonArray();
            int checkedValues = 0;
            bool valuesMatch = true;
            var focus = FocusedAncestors();
            foreach (var binding in ParameterBindings().OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                var row = binding.Control;
                var detail = new JsonObject { ["id"] = binding.Id, ["instance"] = row.Instance, ["bindings"] = binding.Version };
                realized.Add(detail);
                if (!IsCurrentParameterBinding(binding) || !ParameterRowIsVisible(row)
                    || focus.Contains(row.Number) || focus.Contains(row.Slider)) continue;
                if (!IsCurrentParameterBinding(binding) || !parameterValues.TryGetValue(binding.Id, out var expected)) continue;
                double numberMinimum = row.Number.Minimum, numberMaximum = row.Number.Maximum, numberValue = row.Number.Value;
                double sliderMinimum = row.Slider.Minimum, sliderMaximum = row.Slider.Maximum, sliderValue = row.Slider.Value;
                double step = row.Slider.StepFrequency;
                if (!IsCurrentParameterBinding(binding)) continue;
                double epsilon = Math.Max(1e-6, Math.Abs(expected.Maximum - expected.Minimum) * 1e-7);
                bool matches = numberMinimum == expected.Minimum && numberMaximum == expected.Maximum
                    && sliderMinimum == expected.Minimum && sliderMaximum == expected.Maximum
                    && step == Math.Max(.0001, (expected.Maximum - expected.Minimum) / 200)
                    && Math.Abs(numberValue - expected.Value) <= epsilon
                    && Math.Abs(sliderValue - expected.Value) <= Math.Abs(step) / 2 + epsilon;
                checkedValues++; valuesMatch &= matches; detail["valuesMatch"] = matches;
            }
            int visual = VisualTreeHelper.GetChildrenCount(parameterRepeater);
            samples.Add(new JsonObject
            {
                ["position"] = position, ["realized"] = parameterControls.Count,
                ["visualChildren"] = visual, ["created"] = parameterRowsCreated - initialCreated,
                ["reused"] = parameterRowsReused - initialReused,
                ["verticalOffset"] = parameterScroll!.VerticalOffset, ["ids"] = realized,
                ["visibleValuesChecked"] = checkedValues, ["visibleValuesMatch"] = valuesMatch
            });
            WriteReport();
            check(parameterControls.Count is > 0 and <= 32 && visual is > 0 and <= 32
                && parameterControls.Count <= visual
                && parameterControls.All(pair => pair.Key == pair.Value.Id && ReferenceEquals(pair.Value.Document, current)),
                "参数列表在" + position + "只保留至多32行真实控件，回收行不残留旧参数绑定");
            check(checkedValues > 0 && valuesMatch,
                "参数列表在" + position + "的可见非聚焦行保持正确原生参数范围与数值，重绑不留下旧定义");
        }

        // This test runs after the existing drag/playback recordings, so the extra
        // scrolling and edits cannot precondition their GC/latency measurements.
        viewport.Focus(FocusState.Programmatic);
        await ScrollParametersToEndSmokeAsync(false, RecordScroll); Record("顶部");
        await ScrollParametersToEndSmokeAsync(true, RecordScroll); Record("底部");
        check(parameterControls.ContainsKey(parameterIds[^1]) && parameterSearch.Text.Length == 0,
            "真实UIA滚动条到达完整列表最后一个参数，未用搜索或全量控件假装虚拟化");
        await ScrollParametersToEndSmokeAsync(false, RecordScroll); Record("编辑前滚回顶部");
        await ScrollParametersToEndSmokeAsync(true, RecordScroll); Record("编辑前再次到底部");

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
        await ScrollParametersToEndSmokeAsync(false, RecordScroll); Record("滚回顶部");
        await ScrollParametersToEndSmokeAsync(true, RecordScroll); Record("再次到底部");
        await ScrollParametersToEndSmokeAsync(false, RecordScroll); Record("再次滚回顶部");
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
