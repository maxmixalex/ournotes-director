using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed record ResourceSelection(string Value, IReadOnlyDictionary<string, string?> RelatedValues);

internal static class ResourcePicker
{
    internal static async Task<ResourceSelection?> PickAsync(MainWindow window, CommandField field, IReadOnlyDictionary<string, string?> formValues)
    {
        string command = field.LookupCommandId!;
        var definition = CommandCatalog.Get(command);
        string? queryKey = definition.Fields.Any(input => input.Key == "query") ? "query" : command == "model.list" ? "character" : null;
        bool needsQuery = queryKey is not null && definition.Fields.First(input => input.Key == queryKey).Required;
        var search = new TextBox { PlaceholderText = command == "voice.search" ? "搜索语音台词…" : "搜索名称、角色或编号…",
            Text = queryKey is null ? "" : formValues.GetValueOrDefault(queryKey) ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(search, "搜索" + field.Label);
        var list = new ListView { Height = 320, SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var message = Ui.Text(needsQuery ? "输入台词开始搜索" : "正在读取…", 12, true);
        var busy = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        var rows = new List<ResultRow>();
        var presets = field.Choices.Select(choice => new ResultRow(choice.Value, choice.Label, "", JsonValue.Create(choice.Value)!) { Kind = "choice" }).ToArray();
        var values = definition.Fields.Where(input => formValues.ContainsKey(input.Key))
            .ToDictionary(input => input.Key, input => formValues[input.Key], StringComparer.Ordinal);
        values.Remove("out"); values.Remove("offset"); values.Remove(field.Key);
        if (queryKey is not null) values.Remove(queryKey);
        if (definition.Fields.Any(input => input.Key == "limit")) values["limit"] = "200";
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? request = null;
        int? nextOffset = null;
        int generation = 0;
        bool closed = false, loading = false, failed = false;
        string? loadedQuery = null;
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        var more = new Button { Content = "加载更多", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center };
        var refresh = new Button { Content = new FontIcon { Glyph = "\uE721", FontSize = 14 }, Padding = new(10) };
        AutomationProperties.SetName(refresh, "搜索"); ToolTipService.SetToolTip(refresh, "搜索 · Enter");
        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        searchRow.Children.Add(search); searchRow.Children.Add(refresh); Grid.SetColumn(refresh, 1);
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.Children.Add(busy); footer.Children.Add(message); Grid.SetColumn(message, 1);
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = "选择" + field.Label,
            PrimaryButtonText = "选择", CloseButtonText = "取消", IsPrimaryButtonEnabled = false,
            Content = Ui.Stack(10, searchRow, list, more, footer) };
        bool CurrentResults() => !failed && (queryKey is null || loadedQuery == search.Text.Trim());
        void Selectable() => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem && CurrentResults();
        void Filter()
        {
            string query = search.Text.Trim(); string? previous = (list.SelectedItem as ListViewItem)?.Tag is ResultRow old ? old.Id : null;
            list.Items.Clear();
            // Remote matching may include aliases not literally present in a translated title.
            bool remoteMatched = queryKey is not null && loadedQuery == query;
            foreach (var row in presets.Concat(rows).Where(row => remoteMatched || row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                var name = new TextBlock { Text = row.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
                var detail = new TextBlock { Text = row.Subtitle, FontSize = 11, Opacity = .6, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
                    Visibility = string.IsNullOrEmpty(row.Subtitle) ? Visibility.Collapsed : Visibility.Visible };
                var item = new ListViewItem { Tag = row, Content = Ui.Stack(3, name, detail), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(10, 8, 10, 8) };
                AutomationProperties.SetName(item, row.Title); ToolTipService.SetToolTip(item, row.Subtitle.Length == 0 ? row.Id : row.Title + "\n" + row.Subtitle);
                list.Items.Add(item); if (row.Id == previous) list.SelectedItem = item;
            }
            Selectable();
            if (!loading && !failed) message.Text = needsQuery && query.Length == 0 ? "输入台词开始搜索"
                : list.Items.Count == 0 ? "没有匹配的素材" : $"{list.Items.Count} 项";
        }
        async Task LoadAsync(bool append)
        {
            delay.Stop(); if (closed) return;
            string query = search.Text.Trim(); if (append && query != loadedQuery) append = false;
            request?.Cancel(); request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = request.Token; int version = ++generation;
            if (needsQuery && query.Length == 0)
            { rows.Clear(); loadedQuery = ""; nextOffset = null; loading = failed = false; busy.Visibility = more.Visibility = Visibility.Collapsed; Filter(); return; }
            var parameters = new Dictionary<string, string?>(values, StringComparer.Ordinal);
            if (queryKey is not null && query.Length > 0) parameters[queryKey] = command == "model.list" ? DisplayNames.SearchAlias(query) : query;
            if (append && nextOffset is { } offset) parameters["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            loading = true; failed = false; busy.IsActive = true; busy.Visibility = Visibility.Visible; more.IsEnabled = false;
            message.Text = "正在读取…"; Selectable();
            try
            {
                var result = await window.QueryAsync(command, parameters, token);
                if (closed || token.IsCancellationRequested || version != generation) return;
                if (!result.Success) { failed = true; message.Text = result.Error ?? "读取失败，请重试"; Selectable(); return; }
                if (!append) rows.Clear();
                rows.AddRange(ResultRow.ForPicker(result.Json, command, field.Key, parameters)); loadedQuery = query;
                nextOffset = result.Result is JsonObject page && page["nextOffset"] is JsonValue number && number.TryGetValue<int>(out int nextPage) ? nextPage : null;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closed && version == generation) { failed = true; message.Text = error.Message; } }
            finally
            {
                if (!closed && version == generation)
                {
                    loading = false; busy.IsActive = false; busy.Visibility = Visibility.Collapsed; more.IsEnabled = true;
                    more.Visibility = nextOffset.HasValue ? Visibility.Visible : Visibility.Collapsed; Filter();
                }
            }
        }
        search.TextChanged += (_, _) =>
        {
            Filter(); if (queryKey is null) return;
            request?.Cancel(); delay.Stop(); delay.Start();
        };
        delay.Tick += async (_, _) => await LoadAsync(false);
        refresh.Click += async (_, _) => { if (queryKey is not null || failed) await LoadAsync(false); else Filter(); };
        more.Click += async (_, _) => await LoadAsync(true);
        list.SelectionChanged += (_, _) => Selectable();
        void Accept() { if (dialog.IsPrimaryButtonEnabled) { dialog.Tag = "selected"; dialog.Hide(); } }
        list.DoubleTapped += (_, args) => { Accept(); args.Handled = true; };
        list.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { Accept(); args.Handled = true; } };
        search.KeyDown += async (_, args) => { if (args.Key == VirtualKey.Enter) { args.Handled = true; if (queryKey is not null) await LoadAsync(false); else if (list.Items.Count == 1) { list.SelectedIndex = 0; Accept(); } } };
        dialog.Opened += async (_, _) => { search.Focus(FocusState.Programmatic); await LoadAsync(false); };
        ContentDialogResult outcome;
        try { outcome = await dialog.ShowAsync(); }
        finally { closed = true; delay.Stop(); lifetime.Cancel(); request?.Cancel(); request?.Dispose(); }
        if (outcome != ContentDialogResult.Primary && dialog.Tag?.ToString() != "selected") return null;
        if ((list.SelectedItem as ListViewItem)?.Tag is not ResultRow selected) return null;
        var choice = selected.Selection(field.Key);
        return new(choice.Value, new Dictionary<string, string?>(choice.RelatedValues) { ["displayName"] = selected.Title });
    }
}
