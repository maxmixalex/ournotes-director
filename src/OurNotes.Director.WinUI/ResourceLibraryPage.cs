using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

/// <summary>Browse original local archives and download through the persistent task queue.</summary>
internal sealed partial class ResourceLibraryPage : UserControl
{
    readonly MainWindow window;
    readonly ComboBox libraries = new() { Header = "当前素材库", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock path = Ui.Text("", 12, true, true), summary = Ui.Text("", 12, true), count = Ui.Text("", 12, true);
    readonly ComboBox category = Ui.Choice("分类", [("stories", "剧情"), ("models", "Live2D 模型"), ("homes", "主页")], "stories");
    readonly ComboBox site = Ui.Choice("区服", [("", "全部区服"), ("intl", "国际服"), ("jp", "日服")]);
    readonly TextBox search = Ui.Input("搜索本地资源", placeholder: "名称、角色或编号");
    readonly ListView list = new() { Height = 260, SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock details = Ui.Text("选择资源后，可以直接打开或校验本地文件。", 12, true, true);
    readonly InfoBar notice = new() { IsClosable = true };
    readonly Button open, verify, more, remove, folder;
    readonly ComboBox band = Ui.Choice("乐队", [("millsage", "millsage"), ("mygo", "MyGO!!!!!"), ("mujica", "Ave Mujica"), ("yumemita", "梦限大 MewType"), ("kadan", "kadan")], "millsage");
    readonly ComboBox downloadSites = Ui.Choice("下载区服", [("intl", "国际服"), ("jp", "日服"), ("intl,jp", "国际服与日服")], "intl");
    readonly TextBox destination = Ui.Input("保存文件夹"), languages = Ui.Input("语言", "zh-Hans", "留空下载所有语言；多个语言用逗号分隔");
    readonly NumberBox concurrency = new() { Header = "同时下载", Value = 8, Minimum = 1, Maximum = 32, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    readonly CheckBox deepScan = new() { Content = "同时查找客串剧情", IsChecked = true };
    readonly Expander downloadPanel = new() { Header = "下载新资源 / 更新素材库", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ProgressBar downloadProgress = new() { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    readonly TextBlock downloadMessage = Ui.Text("下载可在后台继续，关闭此面板不会停止任务。", 12, true);
    readonly Button download, cancel;
    IReadOnlyList<ResultRow> rows = [];
    ResourceLibrarySummary? current;
    DesktopTask? downloadTask;
    CancellationTokenSource? reading, checking;
    int generation, limit = 100;
    bool changing;
    string loadedPath = "", loadedCategory = "";

    internal ResourceLibraryPage(MainWindow window)
    {
        this.window = window;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        band.IsEditable = true;
        languages.Text = window.Settings.Language;
        destination.Text = DefaultDestination("millsage");
        open = Ui.AsyncButton("在工作台打开", OpenSelectedAsync, "\uE8A7", true); open.IsEnabled = false;
        verify = Ui.AsyncButton("校验文件", CheckSelectedAsync, "\uE73E"); verify.IsEnabled = false;
        more = Ui.Button("显示更多", () => { limit += 100; Filter(); }); more.Visibility = Visibility.Collapsed;
        folder = StudioLayout.Tool("打开文件夹", () => MainWindow.OpenPath(window.Settings.ArchivePath), "\uE8B7");
        remove = StudioLayout.Tool("从列表移除", ForgetCurrent, "\uE711");
        ToolTipService.SetToolTip(remove, "仅移除素材库登记，保留文件夹中的全部文件。");
        download = Ui.AsyncButton("开始下载", DownloadAsync, "\uE896", true);
        cancel = Ui.Button("停止下载", () => { if (downloadTask is {} task) window.Jobs.Cancel(task); }); cancel.Visibility = Visibility.Collapsed;
        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new() { Width = new(140) }); filters.ColumnDefinitions.Add(new() { Width = new(116) }); filters.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        filters.Children.Add(category); filters.Children.Add(site); Grid.SetColumn(site, 1); filters.Children.Add(search); Grid.SetColumn(search, 2);
        var downloadOptions = new Grid { ColumnSpacing = 10 };
        downloadOptions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); downloadOptions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); downloadOptions.ColumnDefinitions.Add(new() { Width = new(120) });
        downloadOptions.Children.Add(band); downloadOptions.Children.Add(downloadSites); Grid.SetColumn(downloadSites, 1); downloadOptions.Children.Add(concurrency); Grid.SetColumn(concurrency, 2);
        downloadPanel.Content = Ui.Stack(12, downloadOptions, languages, destination,
            Ui.Row(6, StudioLayout.AsyncTool("选择保存文件夹", async () => { if (await window.PickFolderAsync() is {} selected) destination.Text = selected; }),
                StudioLayout.Tool("更新当前库", PrepareUpdate), StudioLayout.Tool("新建独立素材库", PrepareNew)),
            deepScan, Ui.Row(8, download, cancel, StudioLayout.Tool("查看任务", () => window.Navigate("tasks"))), downloadProgress, downloadMessage);
        Content = Ui.Scroll(Ui.Stack(12, notice, libraries, path,
            Ui.Row(5, StudioLayout.AsyncTool("添加本地素材库", AttachAsync, "\uE710"), StudioLayout.AsyncTool("刷新", () => RefreshAsync(true), "\uE72C"), folder, remove),
            summary, filters, count, list, more, details, Ui.Row(8, open, verify), downloadPanel,
            new Expander { Header = "高级素材工具", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.Stack(6,
                StudioLayout.Tool("剧情、对白与语音", () => window.Navigate("stories")), StudioLayout.Tool("模型、动作与表情", () => window.Navigate("models")),
                StudioLayout.Tool("舞台、特效与其他文件", () => window.Navigate("assets")), StudioLayout.Tool("诊断", () => window.Navigate("diagnostics"))) }));
        libraries.SelectionChanged += async (_, _) =>
        {
            if (changing || (libraries.SelectedItem as ComboBoxItem)?.Tag is not string selected) return;
            window.Settings.ArchivePath = selected; window.SaveSettings(); window.ApplySettings(); await RefreshAsync(true);
        };
        category.SelectionChanged += async (_, _) => { if (!changing) await RefreshAsync(); };
        search.TextChanged += (_, _) => { limit = 100; Filter(); };
        site.SelectionChanged += (_, _) => { limit = 100; Filter(); };
        list.SelectionChanged += (_, _) => SelectionChanged();
        list.DoubleTapped += async (_, _) => { if (Selected is not null) await SafeAsync(OpenSelectedAsync); };
        band.SelectionChanged += (_, _) =>
        {
            if (!changing && !DownloadBusy && !IsCurrentDestination()) destination.Text = DefaultDestination(BandValue);
        };
        Unloaded += (_, _) => { reading?.Cancel(); checking?.Cancel(); };
    }

    ResultRow? Selected => (list.SelectedItem as ListViewItem)?.Tag as ResultRow;
    bool DownloadBusy => downloadTask?.Status is DesktopTaskStatus.Queued or DesktopTaskStatus.Running;
    string BandValue => string.IsNullOrWhiteSpace(band.Text) || (band.SelectedItem as ComboBoxItem)?.Content?.ToString() == band.Text ? Ui.Selected(band) : band.Text.Trim();
    static string DefaultDestination(string name)
    {
        string safe = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OurNotes Director", "Resources", safe.Length == 0 ? "new-library" : safe);
    }
    static bool SamePath(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    bool IsCurrentDestination() => current is not null && SamePath(destination.Text, current.Directory);
    void Notice(string message, bool error = false) { notice.Message = message; notice.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational; notice.IsOpen = true; }
    async Task SafeAsync(Func<Task> action) { try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { Notice(error.Message, true); } }

    internal async Task RefreshAsync(bool force = false)
    {
        string directory = window.Settings.ArchivePath, kind = Ui.Selected(category);
        changing = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(directory)) window.Settings.RememberResourceLibrary(directory);
            libraries.Items.Clear();
            foreach (string registered in window.Settings.ResourceLibraries)
            {
                var item = new ComboBoxItem { Content = Path.GetFileName(registered.TrimEnd(Path.DirectorySeparatorChar)) + " · " + registered, Tag = registered };
                libraries.Items.Add(item); if (SamePath(registered, directory)) libraries.SelectedItem = item;
            }
        }
        finally { changing = false; }
        path.Text = directory;
        folder.IsEnabled = Directory.Exists(directory); remove.IsEnabled = !string.IsNullOrWhiteSpace(directory);
        if (!force && loadedPath == directory && loadedCategory == kind) return;
        reading?.Cancel(); reading?.Dispose(); reading = new(); checking?.Cancel();
        var token = reading.Token; int version = ++generation;
        loadedPath = loadedCategory = "";
        rows = []; Filter(); current = null; summary.Text = "正在读取本地目录…";
        if (string.IsNullOrWhiteSpace(directory)) { summary.Text = "添加已有素材库，或在下方下载需要的资源。"; return; }
        try
        {
            var snapshot = await Task.Run(() => (Summary: ResourceLibrary.ReadSummary(directory), Items: ResourceLibrary.ReadItems(directory, kind, window.Settings.Language, token)), token);
            if (version != generation || token.IsCancellationRequested) return;
            current = snapshot.Summary; rows = snapshot.Items; loadedPath = directory; loadedCategory = kind; limit = 100;
            summary.Text = $"{current.Stories} 部剧情 · {current.Models} 个模型 · " + string.Join(" / ", current.Sites.Select(s => s == "jp" ? "日服" : "国际服"));
            Filter();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == generation) { summary.Text = "此文件夹暂时无法读取。"; Notice(error.Message, true); } }
    }

    void Filter()
    {
        string query = search.Text.Trim(), selectedSite = Ui.Selected(site);
        var matches = rows.Where(row => (selectedSite.Length == 0 || row.Context.GetValueOrDefault("site") == selectedSite)
            && (query.Length == 0 || row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var previous = Selected; list.Items.Clear();
        foreach (var row in matches.Take(limit))
        {
            var item = new ListViewItem { Tag = row, Content = Ui.Stack(3, Ui.Text(row.Title, 14), Ui.Text(row.Subtitle, 11, true)), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            list.Items.Add(item);
            if (previous is not null && row.Id == previous.Id && row.Kind == previous.Kind && row.Context.GetValueOrDefault("site") == previous.Context.GetValueOrDefault("site")) list.SelectedItem = item;
        }
        count.Text = matches.Length == 0 ? "没有匹配的本地资源。" : $"{matches.Length} 项" + (matches.Length > limit ? $" · 已显示前 {limit} 项" : "");
        more.Visibility = matches.Length > limit ? Visibility.Visible : Visibility.Collapsed;
        SelectionChanged();
    }
    void SelectionChanged()
    {
        open.IsEnabled = Selected is not null; verify.IsEnabled = Selected is not null && checking is null;
        details.Text = Selected is {} selected ? selected.Title + "\n" + selected.Id + " · " + (selected.Context.GetValueOrDefault("site") == "jp" ? "日服" : "国际服") : "选择资源后，可以直接打开或校验本地文件。";
    }
    async Task AttachAsync()
    {
        if (await window.PickFolderAsync() is not {} selected) return;
        await Task.Run(() => ResourceLibrary.ReadSummary(selected));
        window.Settings.ArchivePath = selected; window.Settings.RememberResourceLibrary(selected); window.SaveSettings(); window.ApplySettings(); await RefreshAsync(true);
    }
    void ForgetCurrent()
    {
        string forgotten = window.Settings.ArchivePath;
        window.Settings.ResourceLibraries.RemoveAll(value => SamePath(value, forgotten));
        window.Settings.ArchivePath = window.Settings.ResourceLibraries.FirstOrDefault() ?? "";
        window.SaveSettings(); window.ApplySettings(); Notice("已从素材库列表移除，文件仍保留在原文件夹。"); _ = RefreshAsync(true);
    }
    async Task OpenSelectedAsync()
    {
        if (Selected is not {} selected) return;
        string selectedSite = selected.Context.GetValueOrDefault("site") ?? window.Settings.Site;
        switch (selected.Kind)
        {
            case "model": window.Navigate("live2d"); await window.Live2D.OpenModelAsync(selected.Id, selected.Title, site: selectedSite); break;
            case "story": window.Navigate("scenes"); await window.Scene.SetSourceAsync(selected.Selection("adv").Value, selectedSite); break;
            case "home.spot": window.Navigate("home"); await window.Home.OpenSourceAsync(long.Parse(selected.Id, CultureInfo.InvariantCulture), selectedSite); break;
        }
    }
    async Task CheckSelectedAsync()
    {
        if (Selected is not {} selected || checking is not null) return;
        using var cancellation = new CancellationTokenSource(); checking = cancellation; verify.IsEnabled = false;
        int version = generation; string directory = window.Settings.ArchivePath;
        try
        {
            var result = await Task.Run(() => ResourceLibrary.CheckFiles(directory, selected, (done, total) =>
            { if (done == total || done % 20 == 0) DispatcherQueue.TryEnqueue(() => { if (version == generation && ReferenceEquals(Selected, selected)) details.Text = $"校验 {selected.Title} · {done} / {total}"; }); }, cancellation.Token), cancellation.Token);
            if (version != generation || cancellation.IsCancellationRequested || !ReferenceEquals(Selected, selected)) return;
            details.Text = result.Success ? $"校验通过 · {result.Files} 个资源条目 · {ResourceLibrary.FormatBytes(result.StoredBytes)}" : "发现缺失或损坏的文件：\n" + string.Join("\n", result.Problems.Take(5)) + (result.Problems.Count > 5 ? $"\n另有 {result.Problems.Count - 5} 项。" : "");
            if (!result.Success) Notice("可以使用“更新当前库”重新下载缺失或损坏的资源。", true);
        }
        catch (OperationCanceledException) { }
        finally { checking = null; verify.IsEnabled = Selected is not null; }
    }
    void PrepareNew() { if (DownloadBusy) return; destination.Text = DefaultDestination(BandValue); downloadPanel.IsExpanded = true; }
    void PrepareUpdate()
    {
        if (DownloadBusy) return;
        if (current is null) { Notice("请先选择可用的本地素材库。", true); return; }
        changing = true;
        try
        {
            if (current.Band is {Length: > 0} selectedBand) { band.Text = selectedBand; foreach (ComboBoxItem item in band.Items) if (item.Tag?.ToString() == selectedBand) band.SelectedItem = item; }
            foreach (ComboBoxItem item in downloadSites.Items) if (item.Tag?.ToString() == string.Join(',', current.Sites)) downloadSites.SelectedItem = item;
            languages.Text = current.Languages is null ? "" : string.Join(',', current.Languages); destination.Text = current.Directory;
            downloadPanel.IsExpanded = true;
        }
        finally { changing = false; }
    }
    async Task DownloadAsync()
    {
        if (DownloadBusy) return;
        if (string.IsNullOrWhiteSpace(destination.Text)) throw new ArgumentException("请选择素材保存文件夹。");
        if (!double.IsFinite(concurrency.Value)) throw new ArgumentException("请输入同时下载的数量。");
        string selectedBand = BandValue, target = Path.GetFullPath(destination.Text);
        if (File.Exists(Path.Combine(target, "index", "stats.json")))
        {
            var existing = await Task.Run(() => ResourceLibrary.ReadSummary(target));
            if (existing.Band is {Length: > 0} existingBand && !string.Equals(existingBand, selectedBand, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"该文件夹属于 {existingBand}。下载 {selectedBand} 时请选择独立文件夹，已有素材库仍可在上方切换。");
        }
        var request = ResourceLibrary.DownloadRequest(selectedBand, target, Ui.Selected(downloadSites), languages.Text,
            (int)concurrency.Value, deepScan.IsChecked == true, Environment.CurrentDirectory);
        downloadTask = window.Jobs.Enqueue(request); RefreshProgress(downloadTask);
        var result = await downloadTask.Completion; RefreshProgress(downloadTask);
        if (!result.Success) return;
        window.Settings.ArchivePath = target; window.Settings.RememberResourceLibrary(target); window.SaveSettings(); window.ApplySettings();
        await RefreshAsync(true); Notice("素材下载完成，已切换到该素材库。现在可以搜索资源并在工作台打开。");
    }
    internal void RefreshProgress(DesktopTask task)
    {
        if (!ReferenceEquals(task, downloadTask)) return;
        bool busy = DownloadBusy; download.IsEnabled = !busy; cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var field in new Control[] { band, downloadSites, destination, languages, concurrency, deepScan }) field.IsEnabled = !busy;
        downloadProgress.Visibility = Visibility.Visible; downloadProgress.IsIndeterminate = busy && task.Fraction is null; downloadProgress.Value = task.Fraction ?? 0;
        downloadMessage.Text = task.StatusLabel + " · " + (busy ? DownloadProgressText(task.Message) : task.Message)
            + (task.Status is DesktopTaskStatus.Cancelled or DesktopTaskStatus.Failed ? "\n重新开始会复用已经下载并校验通过的文件。" : "");
    }
    static string DownloadProgressText(string value)
    {
        foreach (var (prefix, label) in new[] { ("[discovery", "正在查找关联剧情…"), ("[masterdata", "正在获取最新资源目录…"),
            ("[story-index", "正在读取剧情列表…"), ("[deep-scan", "正在查找客串剧情…"), ("[prefetch", "正在获取资源清单…"),
            ("[publishing", "正在更新本地资源…"), ("[complete", "资源已下载，正在准备索引…") })
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return label;
        return value;
    }
}
