using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    readonly TextBox parameterSearch = Ui.Input("查找参数", placeholder: "名称或参数 ID");
    readonly TextBlock parameterSummary = Ui.Text("载入模型后显示真实参数范围。", 11, true);
    readonly ItemsRepeater parameterRepeater = new()
    {
        Layout = new StackLayout { Spacing = 11 },
        VerticalCacheLength = 1, HorizontalCacheLength = 0
    };
    // Only realized rows are registered here. Native parameter definitions remain
    // in the document; scrolling does not accumulate one control tree per ID.
    readonly Dictionary<string, ParameterControl> parameterControls = new(StringComparer.Ordinal);
    readonly Dictionary<string, Live2DParameter> parameterValues = new(StringComparer.Ordinal);
    Live2DDocument? parameterDocument;
    string[] parameterIds = [];
    int parameterRowsCreated, parameterRowsReused, parameterRowsCleared;

    FrameworkElement CreateParameterInspector()
    {
        // A native DataTemplate owns recycling and parent removal. Its small
        // content host retains the row when recycled, so handlers are created
        // once while each prepared element receives its current parameter ID.
        parameterRepeater.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ContentControl IsTabStop="False" HorizontalContentAlignment="Stretch" />
            </DataTemplate>
            """);
        parameterRepeater.ElementPrepared += (_, args) =>
        {
            var host = (ContentControl)args.Element;
            if (host.Content is not ParameterControl control)
                host.Content = control = new(this, ++parameterRowsCreated);
            else parameterRowsReused++;
            BindParameterControl(control, parameterIds[args.Index]);
        };
        parameterRepeater.ElementClearing += (_, args) =>
        {
            if (((ContentControl)args.Element).Content is not ParameterControl control) return;
            UnbindParameterControl(control); parameterRowsCleared++;
        };
        AutomationProperties.SetName(parameterRepeater, "模型参数列表");
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        panel.Children.Add(parameterSearch);
        var tools = Ui.Stack(4, Ui.Row(4, StudioLayout.Tool("全部恢复自动",
            () => EditLive(current => current.ResetParameters()), "\uE777")), parameterSummary);
        panel.Children.Add(tools); Grid.SetRow(tools, 1);
        // The bounded star row supplies the effective viewport. Repeater is the
        // scroller's direct content, so StackLayout can virtualize offscreen rows.
        parameterScroll = Ui.Scroll(parameterRepeater);
        parameterScroll.ViewChanged += (_, _) => RefreshParameterValues();
        panel.Children.Add(parameterScroll); Grid.SetRow(parameterScroll, 2);
        parameterSearch.TextChanged += (_, _) => BuildParameters();
        return panel;
    }

    void UpdateParameterSnapshot(IReadOnlyList<Live2DParameter> parameters)
    {
        parameterValues.Clear();
        foreach (var parameter in parameters) parameterValues.Add(parameter.Id, parameter);
    }

    void BuildParameters()
    {
        bool previous = refreshing; refreshing = true;
        try
        {
            var parameters = document?.Parameters ?? [];
            UpdateParameterSnapshot(parameters);
            string search = parameterSearch.Text.Trim();
            var ids = parameters.Where(parameter => search.Length == 0
                || parameter.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || parameter.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Select(parameter => parameter.Id).ToArray();
            string summary = document is null ? "载入模型后显示真实参数范围。"
                : $"{ids.Length} / {parameters.Count} 个参数 · 调整数值后固定，恢复自动后由演出驱动。";
            if (parameterSummary.Text != summary) parameterSummary.Text = summary;
            bool replaced = !ReferenceEquals(parameterDocument, document);
            if (replaced || !parameterIds.SequenceEqual(ids, StringComparer.Ordinal))
            {
                foreach (var control in parameterControls.Values.ToArray()) UnbindParameterControl(control);
                parameterRepeater.ItemsSource = null;
                parameterDocument = document; parameterIds = ids;
                parameterRepeater.ItemsSource = parameterIds;
                parameterScroll?.ChangeView(null, 0, null, true);
            }
            // Ordinary edits/undo keep the same items and scroll position. Refresh
            // their current data without discarding or rebuilding native controls.
            var focus = FocusedAncestors();
            foreach (var (id, control) in parameterControls)
                if (parameterValues.TryGetValue(id, out var parameter)) control.Update(parameter, focus, definition: true);
        }
        finally { refreshing = previous; }
    }

    void RefreshParameterValues()
    {
        if (document is null || activeTab != "parameters") return;
        bool previous = refreshing; refreshing = true;
        try
        {
            // Resolve the document's pins once, then touch only realized controls.
            UpdateParameterSnapshot(document.Parameters);
            var focus = FocusedAncestors();
            foreach (var (id, control) in parameterControls)
            {
                if (!ReferenceEquals(control.Document, document) || !parameterValues.TryGetValue(id, out var parameter)) continue;
                if (!ParameterRowIsVisible(control) && !focus.Contains(control.Number) && !focus.Contains(control.Slider)) continue;
                control.Update(parameter, focus, definition: false);
            }
        }
        finally { refreshing = previous; }
    }

    void BindParameterControl(ParameterControl control, string id)
    {
        UnbindParameterControl(control);
        var parameter = parameterValues[id];
        bool previous = refreshing; refreshing = true;
        try
        {
            control.Id = id; control.Document = document; control.Bindings++;
            control.Update(parameter, null, definition: true);
            parameterControls.Add(id, control);
        }
        finally { refreshing = previous; }
    }

    void UnbindParameterControl(ParameterControl control)
    {
        if (control.Id is { } id && parameterControls.TryGetValue(id, out var registered)
            && ReferenceEquals(registered, control)) parameterControls.Remove(id);
        control.Id = null; control.Document = null;
    }

    sealed partial class ParameterControl : UserControl
    {
        readonly Live2DStudioPage owner;
        readonly TextBlock name = Ui.Text("", 12), identifier = Ui.Text("", 10, true), limits = Ui.Text("", 10, true);
        internal readonly Slider Slider = new();
        internal readonly NumberBox Number = new()
        { Width = 86, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        internal readonly Button Reset;
        internal readonly TextBlock Mode = Ui.Text("自动", 10, true);
        internal readonly int Instance;
        internal int Bindings;
        internal string? Id;
        internal Live2DDocument? Document;

        internal ParameterControl(Live2DStudioPage owner, int instance)
        {
            this.owner = owner; Instance = instance;
            IsTabStop = false; HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Reset = StudioLayout.Tool("自动", Release, "\uE777");
            Reset.Padding = new(6, 3, 6, 3);
            ToolTipService.SetToolTip(Reset, "释放数值固定，恢复动作与表情驱动");
            var heading = new Grid { ColumnSpacing = 4 };
            heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            name.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(name); heading.Children.Add(Number); Grid.SetColumn(Number, 1);
            var details = new Grid { ColumnSpacing = 4 };
            details.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            details.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            details.Children.Add(Ui.Stack(0, identifier, limits));
            var automatic = Ui.Row(4, Mode, Reset); details.Children.Add(automatic); Grid.SetColumn(automatic, 1);
            Content = Ui.Stack(0, heading, Slider, details);
            Number.ValueChanged += (_, args) => Change(args.NewValue);
            Slider.ValueChanged += (_, args) => Change(args.NewValue);
        }

        void Change(double value)
        {
            // Handlers are installed once and always use the current binding.
            // Recycled or replaced-document rows cannot edit their former ID.
            if (!owner.refreshing && double.IsFinite(value) && Id is { } id
                && ReferenceEquals(Document, owner.document))
                owner.EditLive(current => current.SetParameter(id, value));
        }
        void Release()
        {
            if (!owner.refreshing && Id is { } id && ReferenceEquals(Document, owner.document))
                owner.EditLive(current => current.ResetParameter(id));
        }

        internal void Update(Live2DParameter parameter, HashSet<DependencyObject>? focus, bool definition)
        {
            if (definition)
            {
                string label = string.IsNullOrWhiteSpace(parameter.Name) ? parameter.Id : parameter.Name;
                if (name.Text != label) name.Text = label;
                if (identifier.Text != parameter.Id)
                {
                    identifier.Text = parameter.Id; ToolTipService.SetToolTip(name, parameter.Id);
                    AutomationProperties.SetName(Slider, "参数滑块 " + parameter.Id);
                    AutomationProperties.SetName(Number, "参数数值 " + parameter.Id);
                    AutomationProperties.SetName(Reset, "参数恢复自动 " + parameter.Id);
                }
                string range = string.Create(CultureInfo.InvariantCulture,
                    $"{parameter.Minimum:0.###} … {parameter.Maximum:0.###} · 默认 {parameter.Default:0.###}");
                if (limits.Text != range) limits.Text = range;
                double span = parameter.Maximum - parameter.Minimum, step = Math.Max(.0001, span / 200);
                // A recycled row can change to a disjoint numeric range. Widen
                // the lower bound first, then set the final bounds and value.
                Slider.Minimum = Math.Min(Slider.Minimum, parameter.Minimum);
                Slider.Maximum = parameter.Maximum; Slider.Minimum = parameter.Minimum; Slider.StepFrequency = step;
                Number.Minimum = Math.Min(Number.Minimum, parameter.Minimum);
                Number.Maximum = parameter.Maximum; Number.Minimum = parameter.Minimum;
                Number.SmallChange = step; Number.LargeChange = span / 10;
                Slider.IsEnabled = Number.IsEnabled = span > 0;
            }
            if (focus?.Contains(Number) != true && Number.Value != parameter.Value) Number.Value = parameter.Value;
            if (focus?.Contains(Slider) != true && Slider.Value != parameter.Value) Slider.Value = parameter.Value;
            string mode = parameter.IsOverridden ? "固定" : "自动";
            if (Mode.Text != mode) Mode.Text = mode;
            if (Reset.IsEnabled != parameter.IsOverridden) Reset.IsEnabled = parameter.IsOverridden;
        }
    }

}
