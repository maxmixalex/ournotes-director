using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    sealed record NumberControl(Slider Slider, NumberBox Number);
    readonly Dictionary<string, NumberControl> transformControls = new(StringComparer.Ordinal);
    readonly CheckBox visibleBox = new() { Content = "显示模型", IsChecked = true };
    readonly CheckBox breathBox = new() { Content = "呼吸", IsChecked = true }, blinkBox = new() { Content = "自动眨眼", IsChecked = true };
    readonly CheckBox physicsBox = new() { Content = "物理", IsChecked = true }, loopBox = new() { Content = "循环播放", IsChecked = true };
    readonly NumberBox durationBox = new() { Header = "动画时长（秒）", Minimum = 1d / 30, Maximum = 36000, SmallChange = 1d / 30, LargeChange = 1, Value = 3 };
    readonly NumberBox widthBox = new() { Header = "画布宽度", Minimum = 16, Maximum = 16384, SmallChange = 16, Value = 512 };
    readonly NumberBox heightBox = new() { Header = "画布高度", Minimum = 16, Maximum = 16384, SmallChange = 8, Value = 768 };
    readonly TextBox motionSearch = Ui.Input("查找动作", placeholder: "名称或编号"), expressionSearch = Ui.Input("查找表情", placeholder: "名称或编号");
    readonly ListView motionList = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly ListView expressionList = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock motionState = Ui.Text("", 11, true), expressionState = Ui.Text("", 11, true);
    JsonNode? nativeDescription;

    FrameworkElement CreateControlInspector()
    {
        var behavior = Ui.Stack(2, StudioLayout.Section("显示与运动"), visibleBox,
            Ui.Row(12, breathBox, blinkBox), Ui.Row(12, physicsBox, loopBox));
        visibleBox.Checked += (_, _) => ChangeVisibility(true); visibleBox.Unchecked += (_, _) => ChangeVisibility(false);
        void Controller(CheckBox checkbox, Action<Live2DDocument, bool> change)
        {
            checkbox.Checked += (_, _) => EditLive(current => change(current, true));
            checkbox.Unchecked += (_, _) => EditLive(current => change(current, false));
        }
        Controller(breathBox, (current, value) => current.SetControllers(breath: value));
        Controller(blinkBox, (current, value) => current.SetControllers(blink: value));
        Controller(physicsBox, (current, value) => current.SetControllers(physics: value));
        Controller(loopBox, (current, value) => current.SetControllers(loop: value));
        ToolTipService.SetToolTip(loopBox, "循环当前动作；播放到区间末尾时从入点继续。");
        var transform = Ui.Stack(6, StudioLayout.Section("变换"));
        transform.Children.Add(TransformField("x", "位置 X（%）", -200, 300, 50, value => document!.Transform with { X = value / 100 }));
        transform.Children.Add(TransformField("y", "位置 Y（%）", -200, 300, 50, value => document!.Transform with { Y = value / 100 }));
        transform.Children.Add(TransformField("scale", "缩放（%）", 1, 1000, 100, value => document!.Transform with { Scale = value / 100 }));
        transform.Children.Add(TransformField("rotation", "旋转（°）", -180, 180, 0, value => document!.Transform with { Rotation = value }));
        transform.Children.Add(TransformField("opacity", "不透明度（%）", 0, 100, 100, value => document!.Transform with { Opacity = value / 100 }));
        transform.Children.Add(StudioLayout.Tool("重置位置与缩放", ResetTransform, "\uE777"));
        var output = Ui.Stack(6, StudioLayout.Section("动画与画布"), durationBox,
            StudioLayout.Tool("使用所选动作时长", MatchMotionDuration), CreateActorCanvasControls(), widthBox, heightBox);
        StudioLayout.PairFields(output, widthBox, heightBox);
        durationBox.ValueChanged += (_, args) =>
        { if (!refreshing && double.IsFinite(args.NewValue)) { Edit(current => current.SetDuration(Math.Max(0, args.NewValue - 1d / 30))); RefreshHeader(); } };
        void Output()
        {
            if (refreshing || !double.IsFinite(widthBox.Value) || !double.IsFinite(heightBox.Value)) return;
            Edit(current => current.SetOutput((int)Math.Round(widthBox.Value), (int)Math.Round(heightBox.Value)));
        }
        widthBox.ValueChanged += (_, _) => Output(); heightBox.ValueChanged += (_, _) => Output();
        return Ui.Scroll(Ui.Stack(14, behavior, transform, output));
    }
    FrameworkElement TransformField(string key, string label, double minimum, double maximum, double initial, Func<double, Live2DTransform> value)
    {
        var number = new NumberBox { Value = initial, Minimum = minimum, Maximum = maximum, SmallChange = 1, LargeChange = 10, Width = 88,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, HorizontalAlignment = HorizontalAlignment.Right };
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = initial, StepFrequency = 1 };
        AutomationProperties.SetName(number, label); AutomationProperties.SetName(slider, label + "滑块");
        transformControls.Add(key, new(slider, number));
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = Ui.Text(label, 12); text.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(text); header.Children.Add(number); Grid.SetColumn(number, 1);
        void Changed(double next)
        {
            if (refreshing || document is null || !double.IsFinite(next)) return;
            EditTransform(current => current.SetTransform(value(next))); RefreshTransformControls();
        }
        number.ValueChanged += (_, args) => Changed(args.NewValue); slider.ValueChanged += (_, args) => Changed(args.NewValue);
        return Ui.Stack(0, header, slider);
    }
    void ChangeVisibility(bool value)
    {
        EditTransform(current => current.SetTransform(current.Transform with { Opacity = value ? 1 : 0 })); RefreshTransformControls();
    }
    void ResetTransform()
    {
        EditTransform(current => current.SetTransform(new())); RefreshTransformControls();
    }
    void MatchMotionDuration()
    {
        if (document is null) return;
        string? motion = document.Motion ?? document.Metadata?["defaultMotion"]?.ToString();
        var row = nativeDescription?["motions"]?.AsArray().OfType<JsonObject>().FirstOrDefault(item => item["id"]?.ToString() == motion);
        if (row is null || !double.TryParse(row["duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double length) || length <= 0)
        { Notice("选择一个带有时长的动作后，可以让输出区间匹配这段动画。", false); return; }
        Edit(current => current.SetDuration(Math.Max(0, length - 1d / 30))); RefreshControls();
    }
    FrameworkElement CreateAnimationInspector(bool expressions)
    {
        var search = expressions ? expressionSearch : motionSearch;
        var list = expressions ? expressionList : motionList;
        var state = expressions ? expressionState : motionState;
        var panel = new Grid { RowSpacing = 6 };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        panel.Children.Add(search); panel.Children.Add(state); Grid.SetRow(state, 1); panel.Children.Add(list); Grid.SetRow(list, 2);
        search.TextChanged += (_, _) => RefreshAnimationList(expressions);
        list.SelectionChanged += (_, _) =>
        {
            if (refreshing || document is null || list.SelectedItem is not ListViewItem item) return;
            string? id = item.Tag?.ToString(); if (string.IsNullOrEmpty(id)) id = null;
            Edit(current => { if (expressions) current.SetExpression(id); else current.SetMotion(id); });
            RefreshAnimationState();
        };
        AutomationProperties.SetName(list, expressions ? "模型表情" : "模型动作");
        return panel;
    }
    void RefreshAnimationList(bool expressions)
    {
        bool previous = refreshing; refreshing = true;
        try
        {
            var list = expressions ? expressionList : motionList; list.Items.Clear();
            if (document is null) return;
            string search = (expressions ? expressionSearch : motionSearch).Text.Trim();
            string? current = expressions ? document.Expression : document.Motion;
            var automatic = new ListViewItem { Content = Ui.Stack(2, Ui.Text(expressions ? "恢复默认表情" : "恢复默认动作", 13)), Tag = "" };
            list.Items.Add(automatic); if (current is null) list.SelectedItem = automatic;
            foreach (string id in document.Metadata?[expressions ? "expressions" : "motions"]?.AsArray().Select(item => item?.ToString()).OfType<string>() ?? [])
            {
                string label = DisplayNames.Animation(id);
                if (search.Length > 0 && !id.Contains(search, StringComparison.OrdinalIgnoreCase) && !label.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                string suffix = id;
                if (!expressions && nativeDescription?["motions"]?.AsArray().OfType<JsonObject>().FirstOrDefault(row => row["id"]?.ToString() == id)?["duration"] is { } duration)
                    suffix += " · " + duration + " 秒";
                var item = new ListViewItem { Content = Ui.Stack(2, Ui.Text(label, 13), Ui.Text(suffix, 10, true)), Tag = id };
                list.Items.Add(item); if (current == id) list.SelectedItem = item;
            }
        }
        finally { refreshing = previous; }
        RefreshAnimationState();
    }
    void RefreshAnimationState()
    {
        var actual = viewport.LastFrame?.ActorAnimations?.FirstOrDefault(animation => animation.TargetId == document?.ActorId);
        motionState.Text = "当前：" + DisplayNames.Animation(actual?.Motion ?? document?.Motion ?? "原演出");
        expressionState.Text = "当前：" + DisplayNames.Animation(actual?.Expression ?? document?.Expression ?? "原演出");
    }
    void RefreshTimeDependentControls()
    {
        if (document is null) return;
        bool previous = refreshing; refreshing = true;
        try
        {
            if (activeTab == "controls")
            {
                breathBox.IsChecked = document.Breath; blinkBox.IsChecked = document.Blink;
                physicsBox.IsChecked = document.Physics; loopBox.IsChecked = document.Loop;
            }
            else if (activeTab is "motion" or "expression")
            {
                var list = activeTab == "motion" ? motionList : expressionList;
                string? selected = activeTab == "motion" ? document.Motion : document.Expression;
                list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag?.ToString() == selected);
                RefreshAnimationState();
            }
        }
        finally { refreshing = previous; }
    }
    void RefreshControls()
    {
        bool previous = refreshing; refreshing = true;
        try
        {
            RefreshHeader();
            breathBox.IsChecked = document?.Breath ?? true; blinkBox.IsChecked = document?.Blink ?? true;
            physicsBox.IsChecked = document?.Physics ?? true; loopBox.IsChecked = document?.Loop ?? true;
            physicsBox.IsEnabled = document is not null && nativeDescription?["physics"]?.GetValue<bool>() != false;
            foreach (var item in new Control[] { visibleBox, breathBox, blinkBox, loopBox, durationBox, widthBox, heightBox }) item.IsEnabled = document is not null;
            durationBox.Value = TimelineRange.Full(document?.Duration ?? 3, 30).Duration;
            widthBox.Value = document?.Width ?? 512; heightBox.Value = document?.Height ?? 768;
            RefreshTransformControls(); RefreshAnimationList(false); RefreshAnimationList(true); BuildParameters();
            RefreshActorCanvasControls();
        }
        finally { refreshing = previous; }
    }
    void RefreshTransformControls(Live2DTransform? previewTransform = null)
    {
        bool previous = refreshing; refreshing = true;
        try
        {
            var transform = previewTransform ?? modelDrag?.Value ?? document?.Transform ?? new();
            if (visibleBox.IsChecked != (transform.Opacity > 0)) visibleBox.IsChecked = transform.Opacity > 0;
            foreach (var (key, value) in new[] { ("x", transform.X * 100), ("y", transform.Y * 100), ("scale", transform.Scale * 100), ("rotation", transform.Rotation), ("opacity", transform.Opacity * 100) })
            {
                var control = transformControls[key];
                if (control.Number.Value != value) control.Number.Value = value;
                if (control.Slider.Value != value) control.Slider.Value = value;
                if (control.Number.IsEnabled != (document is not null)) control.Number.IsEnabled = document is not null;
                if (control.Slider.IsEnabled != (document is not null)) control.Slider.IsEnabled = document is not null;
            }
        }
        finally { refreshing = previous; }
    }
    bool HasFocusWithin(DependencyObject control)
    {
        if (XamlRoot is null) return false;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (ReferenceEquals(element, control)) return true;
        return false;
    }
}
