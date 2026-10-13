using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Shapes;

namespace OurNotes.Director.WinUI;

internal sealed partial class SceneStudioPage
{
    private void RunTimelineVisibilitySmoke(Action<bool, string> check)
    {
        var original = _document;
        double? time = original?.Time;
        string? json = original?.ToJson();
        _timeline.RunVisibilitySmoke(check, visible =>
        {
            if (visible) _timelineTab!.IsChecked = true;
            else _commandsTab!.IsChecked = true;
            UpdateLayout();
        });
        check(ReferenceEquals(original, _document) && _document?.Time == time && _document?.ToJson() == json,
            "时间轴折叠与展开检查保持原场景和播放位置");
    }
}

internal sealed partial class StudioTimeline
{
    internal void RunVisibilitySmoke(Action<bool, string> check, Action<bool> setVisible)
    {
        var tracks = _tracks;
        string? selection = _selectedMarker;
        double duration = Duration, position = Position;
        bool visible = Visibility == Visibility.Visible;
        bool FiniteLines() => _canvas.Children.OfType<Line>().Append(_playhead)
            .All(line => double.IsFinite(line.X1) && double.IsFinite(line.X2) && double.IsFinite(line.Y1) && double.IsFinite(line.Y2));
        try
        {
            setVisible(false);
            Duration = 12;
            SetTracks([new("layout", "布局检查", [new("layout.first", "layout", 1.25, "首次标记")])]);
            Position = 2.5;
            check(Visibility == Visibility.Collapsed && Duration == 12 && Position == 2.5 && FiniteLines(),
                "折叠且未布局的时间轴接收数据和播放位置时不产生非有限坐标");
            setVisible(true);
            check(_canvas.Children.Contains(_playhead) && _canvas.ActualWidth > 0 && FiniteLines()
                && Math.Abs(_playhead.X1 - At(2.5)) < .001 && _canvas.Height == ContentHeight,
                "时间轴首次展开绘制有限坐标的标尺和播放头");
            setVisible(false);
            SetTracks([new("layout", "布局检查", [new("layout.changed", "layout", 6.25, "更新标记")])]);
            Position = 7.5;
            setVisible(true);
            check(!_drawPending && FiniteLines() && Math.Abs(_playhead.X1 - At(7.5)) < .001
                && _canvas.Children.OfType<FrameworkElement>().Any(element => element.Tag is StudioTimelineMarker { Id: "layout.changed" })
                && !_canvas.Children.OfType<FrameworkElement>().Any(element => element.Tag is StudioTimelineMarker { Id: "layout.first" }),
                "折叠期间的标记和时间更新在再次展开时正确刷新");
        }
        finally
        {
            _tracks = tracks; _selectedMarker = selection;
            Duration = duration; Position = position;
            setVisible(visible); Draw();
        }
    }
}
