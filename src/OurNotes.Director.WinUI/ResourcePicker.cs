using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed record ResourceSelection(string Value, IReadOnlyDictionary<string, string?> RelatedValues);

internal static class ResourcePicker
{
    internal static async Task<ResourceSelection?> PickAsync(MainWindow window, CommandField field, IReadOnlyDictionary<string, string?> formValues)
    {
        string id = field.LookupCommandId!;
        var definition = CommandCatalog.Get(id);
        string? queryKey = definition.Fields.Any(input => input.Key == "query") ? "query"
            : id == "model.list" ? "character" : null;
        bool needsQuery = queryKey is not null && definition.Fields.First(input => input.Key == queryKey).Required;
        var search = Ui.Input(id == "voice.search" ? "搜索语音台词" : "搜索归档", queryKey is null ? "" : formValues.GetValueOrDefault(queryKey) ?? "",
            id == "voice.search" ? "输入实际台词关键词后搜索" : "输入标题、路径或角色关键词");
        var filter = Ui.Input("筛选已加载结果", "", "按名称、角色或编号筛选");
        var list = new ListView { Height = 340, SelectionMode = ListViewSelectionMode.Single };
        var message = Ui.Text(needsQuery ? "输入台词关键词，再点击搜索。" : "正在读取归档…", 12, true);
        var busy = new ProgressRing { IsActive = false, Width = 22, Height = 22 };
        var rows = new List<ResultRow>();
        var presets = field.Choices.Select(choice => new ResultRow(choice.Value, choice.Label, "快捷选择", JsonValue.Create(choice.Value)!) { Kind = "choice" }).ToArray();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var input in definition.Fields)
        {
            if (formValues.TryGetValue(input.Key, out var inherited)) values[input.Key] = inherited;
        }
        values.Remove("out"); values.Remove("offset"); values.Remove(field.Key);
        if (queryKey is not null) values.Remove(queryKey);
        if (definition.Fields.Any(input => input.Key == "limit")) values["limit"] = "200";
        using var cancellation = new CancellationTokenSource();
        CancellationTokenSource? activeRequest = null;
        int? nextOffset = null;
        string loadedQuery = "";
        int requestVersion = 0;
        bool closed = false;
        var next = Ui.Button("加载更多", () => { });
        next.Visibility = Visibility.Collapsed;
        var searchButton = Ui.Button("搜索", () => { }, "\uE721");
        var content = Ui.Stack(12);
        if (queryKey is not null) content.Children.Add(Ui.Stack(8, search, searchButton));
        content.Children.Add(filter); content.Children.Add(Ui.Row(8, busy, message)); content.Children.Add(list); content.Children.Add(next);
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = "选择" + field.Label, PrimaryButtonText = "使用所选", CloseButtonText = "返回",
            IsPrimaryButtonEnabled = false, Content = content };
        void Filter()
        {
            string query = filter.Text.Trim();
            list.Items.Clear();
            foreach (var row in presets.Concat(rows.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))))
                list.Items.Add(new ListViewItem { Content = Ui.Stack(3, Ui.Text(row.Title), Ui.Text(row.Subtitle, 12, true)), Tag = row });
        }
        filter.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        async Task LoadAsync(bool append)
        {
            string query = search.Text.Trim();
            if (append && query != loadedQuery) append = false;
            if (needsQuery && query.Length == 0)
            {
                message.Text = "请输入实际台词关键词后搜索，不能用空白搜索语音。";
                return;
            }
            activeRequest?.Cancel(); activeRequest?.Dispose();
            activeRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            var token = activeRequest.Token;
            int version = ++requestVersion;
            var request = new Dictionary<string, string?>(values, StringComparer.Ordinal);
            if (queryKey is not null && query.Length > 0) request[queryKey] = query;
            if (append && nextOffset is { } offset) request["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!append) { rows.Clear(); nextOffset = null; loadedQuery = query; Filter(); }
            busy.IsActive = true; searchButton.IsEnabled = false; next.IsEnabled = false;
            message.Text = "正在查询归档…";
            try
            {
                var result = await window.QueryAsync(id, request, token);
                if (closed || token.IsCancellationRequested || version != requestVersion) return;
                if (!result.Success) { message.Text = result.Error ?? "归档查询未完成。"; return; }
                rows.AddRange(ResultRow.ForPicker(result.Json, id, field.Key, request));
                nextOffset = result.Result is JsonObject page && page["nextOffset"] is JsonValue number && number.TryGetValue<int>(out int nextPage) ? nextPage : null;
                Filter();
                message.Text = rows.Count == 0
                    ? id == "home.animations" ? "选中骨架没有共同的动画名称；留空保留各骨架原动画。" : "没有匹配结果。请调整关键词，或先选择关联主页、模型。"
                    : $"已加载 {rows.Count} 项" + (id == "home.animations" ? "；这些名称存在于全部选中骨架中。" : nextOffset is null ? "；可筛选并选择。" : "；可以继续加载更多。" );
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closed && version == requestVersion) message.Text = error.Message; }
            finally
            {
                if (!closed && version == requestVersion)
                {
                    busy.IsActive = false; searchButton.IsEnabled = true; next.IsEnabled = true;
                    next.Visibility = nextOffset is null ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }
        searchButton.Click += async (_, _) => await LoadAsync(false);
        next.Click += async (_, _) => await LoadAsync(true);
        search.KeyDown += async (_, args) => { if (args.Key == VirtualKey.Enter) { args.Handled = true; await LoadAsync(false); } };
        dialog.Opened += async (_, _) => { Filter(); if (!needsQuery || search.Text.Trim().Length > 0) await LoadAsync(false); };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is not null) { dialog.Tag = "selected"; dialog.Hide(); } };
        var outcome = await dialog.ShowAsync(); closed = true; cancellation.Cancel(); activeRequest?.Cancel(); activeRequest?.Dispose();
        if (outcome != ContentDialogResult.Primary && dialog.Tag?.ToString() != "selected") return null;
        if ((list.SelectedItem as ListViewItem)?.Tag is not ResultRow selected) return null;
        var selection = selected.Selection(field.Key);
        return new ResourceSelection(selection.Value, selection.RelatedValues);
    }
}
