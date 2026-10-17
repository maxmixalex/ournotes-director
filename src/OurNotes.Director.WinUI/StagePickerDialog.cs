using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OurNotes.Director.Core;
using Windows.Storage.Streams;

namespace OurNotes.Director.WinUI;

/// <summary>A visual resource picker; previewing and cancelling cannot modify the scene.</summary>
internal sealed class StagePickerDialog : IAsyncDisposable
{
    readonly ContentDialog dialog;
    readonly Microsoft.UI.Xaml.Controls.StackPanel panel;
    readonly Dictionary<string, Image> thumbnails = [];
    readonly GridView grid = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 380,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, IsItemClickEnabled = true };
    readonly TextBox search = new() { PlaceholderText = "搜索舞台…" };
    readonly TextBlock summary = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .75 };
    readonly StagePreviewSession previews;
    readonly Dictionary<string, GridViewItem> cards = [];
    bool closed;

    internal StagePickerDialog(XamlRoot root, Archive archive, string site, string source, string language,
        IReadOnlyList<NativeStageOption> stages, string active)
    {
        previews = new(archive, site, source, language);
        panel = Ui.Stack(10, search, grid, summary);
        panel.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        dialog = new() { XamlRoot = root, Title = "选择舞台", PrimaryButtonText = "使用舞台", CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false, Content = panel };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        foreach (var stage in stages)
        {
            var image = new Image { Width = 192, Height = 108, Stretch = Stretch.UniformToFill };
            var caption = new TextBlock { Text = stage.Label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false, Margin = new(5, 4, 5, 5) };
            var loading = new TextBlock { Text = "预览载入中…", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Opacity = .7 };
            var picture = new Grid { Width = 192, Height = 108 }; picture.Children.Add(image); picture.Children.Add(loading);
            var item = new GridViewItem { Tag = stage, Padding = new(3), Content = Ui.Stack(0, picture, caption) };
            ToolTipService.SetToolTip(item, stage.Key);
            image.Loaded += async (_, _) =>
            {
                if (image.Source is not null || closed) return;
                try
                {
                    byte[] png = await previews.GetAsync(stage.Key);
                    if (closed || image.Source is not null) return;
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(png); await writer.StoreAsync(); }
                    stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                    if (!closed) { image.Source = bitmap; loading.Visibility = Visibility.Collapsed; }
                }
                catch (Exception error)
                {
                    if (closed) return;
                    loading.Text = "预览不可用"; ToolTipService.SetToolTip(loading, error.Message);
                }
            };
            thumbnails.Add(stage.Key, image); cards.Add(stage.Key, item); grid.Items.Add(item);
            if (stage.Key == active) grid.SelectedItem = item;
        }
        grid.SelectionChanged += (_, _) =>
        {
            var selected = grid.SelectedItem as GridViewItem;
            dialog.IsPrimaryButtonEnabled = selected is not null;
            summary.Text = selected?.Tag is NativeStageOption stage ? $"{stage.Label}　·　{stage.FocusDistances.Count} 个可用景别" : "";
        };
        dialog.IsPrimaryButtonEnabled = grid.SelectedItem is not null;
        search.TextChanged += (_, _) =>
        {
            string query = search.Text.Trim(); grid.Items.Clear();
            foreach (var (key, item) in cards)
                if ((key + " " + ((NativeStageOption)item.Tag).Label).Contains(query, StringComparison.OrdinalIgnoreCase)) grid.Items.Add(item);
        };
    }
    internal async Task<NativeStageOption?> ShowAsync() => await dialog.ShowAsync() == ContentDialogResult.Primary
        ? (grid.SelectedItem as GridViewItem)?.Tag as NativeStageOption : null;
    internal async Task VerifyAndCancelAsync(Action<bool, string> check)
    {
        Exception? error = null;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                grid.SelectedIndex = 0;
                var selected = (NativeStageOption)((GridViewItem)grid.SelectedItem).Tag;
                await previews.GetAsync(selected.Key);
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (thumbnails[selected.Key].Source is null && DateTime.UtcNow < deadline) await Task.Delay(25);
                check(thumbnails[selected.Key].Source is not null && dialog.IsPrimaryButtonEnabled,
                    "实际舞台选择窗口显示原生渲染缩略图，可选中后取消而不改项目");
                await App.CapturePickerCheckpointAsync(panel);
            }
            catch (Exception failure) { error = failure; }
            finally { dialog.Hide(); }
        };
        await ShowAsync();
        if (error is not null) throw error;
    }
    public async ValueTask DisposeAsync() { closed = true; await previews.DisposeAsync(); }
}
