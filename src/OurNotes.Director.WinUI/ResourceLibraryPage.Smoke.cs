using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class ResourceLibraryPage
{
    internal async Task RunDownloadSmokeAsync(Action<bool, string> check)
    {
        string origin = Environment.GetEnvironmentVariable("OURNOTES_RESOURCE_FIXTURE_ORIGIN") ?? "";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var address) || !address.IsLoopback)
            throw new InvalidOperationException("Resource download smoke requires the bounded loopback fixture.");
        string target = Path.Combine(DesktopSettings.DataDirectory, "downloaded resources");
        var report = new JsonObject { ["schema"] = "ournotes-director/resource-manager/1", ["ok"] = false, ["archive"] = target };
        try
        {
            downloadPanel.IsExpanded = true; destination.Text = target; band.Text = "millsage";
            downloadSites.SelectedIndex = 0; languages.Text = "zh-Hans"; concurrency.Value = 4;
            UpdateLayout();
            report["beforeDownloadReady"] = DownloadLayout();
            // Expander reveals its content on a later visual-state tick. Layout
            // alone cannot wait for Loaded or make an offscreen action visible.
            await RevealAsync(download, "download form");
            report["downloadReady"] = DownloadLayout();
            check(download.IsLoaded && download.IsEnabled && libraries.IsLoaded && InViewport(download),
                "素材库的本地管理和下载控件完成真实加载，下载按钮位于滚动视口中");
            await App.CaptureCheckpointAsync("resource-library-download-form");
            Invoke(download);
            await WaitUntilAsync(() => downloadTask is not null, "download task creation");
            var cancelled = downloadTask!;
            using var client = new HttpClient();
            int transferred = 0; var transferDeadline = DateTime.UtcNow.AddSeconds(30);
            while (transferred < 12)
            {
                var stats = JsonNode.Parse(await client.GetStringAsync(origin + "/_stats"));
                transferred = (int?)stats?["assets"] ?? 0;
                if (cancelled.Completion.IsCompleted || DateTime.UtcNow > transferDeadline) throw new InvalidOperationException("Download did not remain active through the cancellation checkpoint.");
                if (transferred < 12) await Task.Delay(20);
            }
            report["assetRequestsBeforeCancel"] = transferred;
            await RevealAsync(cancel, "cancel action");
            Invoke(cancel);
            var cancelledResult = await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => download.IsEnabled, "cancelled download UI");
            check(cancelledResult.Cancelled && cancelled.Status == DesktopTaskStatus.Cancelled && transferred >= 12, "停止下载按钮中断已开始传输素材的实际归档子进程，任务保留可重试状态");
            await RevealAsync(download, "retry action");
            Invoke(download);
            await WaitUntilAsync(() => downloadTask is not null && !ReferenceEquals(downloadTask, cancelled), "retry task creation");
            var first = downloadTask!;
            var firstResult = await first.Completion.WaitAsync(TimeSpan.FromSeconds(120));
            report["download"] = firstResult.Result?.DeepClone();
            check(firstResult.Success, "重新开始通过打包的原生引擎实际下载并建立素材索引：" + (firstResult.Error ?? "完成"));
            check((long?)firstResult.Result?["download"]?["reused"] > 0, "中途停止后重新下载实际复用了取消前已经完成的资源文件");
            await WaitUntilAsync(() => SamePath(window.Settings.ArchivePath, target) && download.IsEnabled, "downloaded library activation");
            await RefreshAsync(true);
            check(current is {Stories: > 0, Models: > 0} && rows.Count > 0 && window.Settings.ResourceLibraries.Contains(target),
                "下载成功自动登记并切换到本地素材库，剧情和模型数量来自新索引");
            string storyId = rows[0].Selection("adv").Value;
            search.Text = storyId;
            check(list.Items.Count > 0 && list.Items.OfType<ListViewItem>().All(item => ((ResultRow)item.Tag).SearchText.Contains(storyId, StringComparison.OrdinalIgnoreCase)),
                "本地剧情列表按真实编号搜索并保留来源区服");
            list.SelectedIndex = 0;
            await CheckSelectedAsync();
            check(details.Text.StartsWith("校验通过", StringComparison.Ordinal), "从素材管理界面校验刚下载的剧情、语言和依赖模型内容哈希");
            report["verified"] = details.Text;
            await RevealAsync(list, "downloaded library");
            await App.CaptureCheckpointAsync("resource-library-downloaded");
            PrepareUpdate(); await RevealAsync(download, "update action"); Invoke(download);
            await WaitUntilAsync(() => downloadTask is not null && !ReferenceEquals(downloadTask, first), "incremental update creation");
            var update = downloadTask!; var updateResult = await update.Completion.WaitAsync(TimeSpan.FromSeconds(120));
            report["update"] = updateResult.Result?.DeepClone();
            check(updateResult.Success && (long?)updateResult.Result?["download"]?["reused"] > 0
                && (long?)updateResult.Result?["download"]?["downloaded"] == 0,
                "更新当前库复用已校验的真实资源，不重复下载完整素材");
            await WaitUntilAsync(() => download.IsEnabled, "incremental update UI");
            search.Text = ""; category.SelectedIndex = 1; await RefreshAsync(true);
            check(rows.Count > 0 && rows.All(row => row.Kind == "model"), "模型分类读取新下载素材库中的真实 Live2D 模型");
            list.SelectedIndex = 0; string selectedModel = Selected!.Id;
            await window.Live2D.PrepareResourceOpenSmokeAsync();
            await RevealAsync(open, "open model action");
            var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler opened = (_, _) => clicked.TrySetResult(); open.Click += opened;
            try { Invoke(open); await clicked.Task.WaitAsync(TimeSpan.FromSeconds(15)); await window.Live2D.WaitForReadyAsync(); }
            finally { open.Click -= opened; }
            await window.Live2D.VerifyResourceOpenSmokeAsync(selectedModel, check);
            report["openedModel"] = selectedModel;
            window.ShowResourceManager(); await RefreshAsync(true); downloadPanel.IsExpanded = false; UpdateLayout();
            await RevealAsync(list, "downloaded models");
            await App.CaptureCheckpointAsync("resource-library-models");
            report["ok"] = true;
        }
        finally
        {
            report["finalLayout"] = DownloadLayout();
            DesktopSettings.AtomicWrite(App.SmokeEvidencePath("resource-manager.json"), report.ToJsonString(DesktopSettings.JsonOptions));
        }

        async Task RevealAsync(FrameworkElement element, string phase)
        {
            await WaitUntilAsync(() => IsLoaded && element.IsLoaded && element.XamlRoot == XamlRoot
                && element.ActualWidth > 0 && element.ActualHeight > 0, phase + " layout");
            if (InViewport(element)) return;
            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .5 });
            await WaitUntilAsync(() => InViewport(element), phase + " visibility");
        }
        bool InViewport(FrameworkElement element)
        {
            if (!IsLoaded || !element.IsLoaded || element.XamlRoot != XamlRoot || Content is not ScrollViewer scroll
                || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
            for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, scroll);
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                if (node is UIElement control && control.Visibility != Visibility.Visible) return false;
            var point = element.TransformToVisual(scroll).TransformPoint(new(0, 0));
            return point.Y >= -1 && point.Y + element.ActualHeight <= scroll.ActualHeight + 1
                && point.X + element.ActualWidth > 0 && point.X < scroll.ActualWidth;
        }
        JsonObject DownloadLayout() => new()
        {
            ["pageLoaded"] = IsLoaded, ["librariesLoaded"] = libraries.IsLoaded,
            ["downloadLoaded"] = download.IsLoaded, ["downloadEnabled"] = download.IsEnabled,
            ["downloadWidth"] = download.ActualWidth, ["downloadHeight"] = download.ActualHeight,
            ["sameXamlRoot"] = download.XamlRoot == XamlRoot, ["downloadVisible"] = InViewport(download)
        };

        static void Invoke(Button button)
        {
            if (!button.IsEnabled) throw new InvalidOperationException("Resource action is disabled: " + button.Content);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        static async Task WaitUntilAsync(Func<bool> condition, string phase)
        {
            var end = DateTime.UtcNow.AddSeconds(30);
            while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(phase); await Task.Delay(20); }
        }
    }
}
