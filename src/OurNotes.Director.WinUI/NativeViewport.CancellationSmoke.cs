using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal async Task RunPresentationCancellationSmokeAsync(Action<bool, string> check)
    {
        if (!HasCurrentSource || showingEmptyCanvas) throw new InvalidOperationException("取消检查需要已载入的原生画面。");
        await PauseForEditingAsync();
        var before = LastFrame ?? throw new InvalidOperationException("取消检查需要已呈现的原生画面。");
        int originalWaiters = WaiterCount();
        bool wasEmpty = showingEmptyCanvas;
        var limit = TimeSpan.FromSeconds(5);
        using var waitingCancellation = new CancellationTokenSource();
        using var renderedCancellation = new CancellationTokenSource();
        var operations = new List<Task>();
        long cancelledSequence = 0;

        int WaiterCount() { lock (frameGate) return waiters.Count; }
        void DiscardOldPresentation()
        {
            // Reproduce the presentation side of SetEmptyCanvas while keeping the
            // small loaded fixture available for a seek that has just finished drawing.
            lock (frameGate) { showingEmptyCanvas = true; pendingFrame = null; Volatile.Write(ref lastFrame, null); }
        }
        void CancelAfterNativeRender(object? sender, PreviewFrame frame)
        {
            if (frame.Sequence <= before.Sequence) return;
            Interlocked.Exchange(ref cancelledSequence, frame.Sequence);
            renderedCancellation.Cancel();
        }
        static async Task<bool> IsCancelledAsync(Task operation, TimeSpan limit)
        {
            try { await operation.WaitAsync(limit); return false; }
            catch (OperationCanceledException) { return true; }
            catch (TimeoutException) { return false; }
        }

        try
        {
            using var alreadyCancelled = new CancellationTokenSource();
            alreadyCancelled.Cancel();
            check(await IsCancelledAsync(WaitForFrameAsync(before.Sequence - 1, cancellationToken: alreadyCancelled.Token), limit)
                && WaiterCount() == originalWaiters, "已取消的呈现请求不会返回旧帧，也不留下等待者");

            DiscardOldPresentation();
            var waiting = AwaitPresentationAsync(waitingCancellation.Token); operations.Add(waiting);
            check(!waiting.IsCompleted && WaiterCount() == originalWaiters + 1,
                "原生帧已完成但画布已清空时，定位仍等待实际呈现");
            waitingCancellation.Cancel();
            check(await IsCancelledAsync(waiting, limit) && WaiterCount() == originalWaiters,
                "切换项目取消已注册的呈现等待后立即退出，并移除等待者");

            var normal = AwaitPresentationAsync(); operations.Add(normal);
            check(!normal.IsCompleted, "未取消的定位不会仅因原生渲染完成就提前返回");
            lock (frameGate) showingEmptyCanvas = false;
            OnFrameReady(session, session.LastFrame!);
            await normal.WaitAsync(limit);
            check(LastFrame?.Sequence == session.LastFrame?.Sequence && WaiterCount() == originalWaiters,
                "正常定位在最后一帧实际呈现后完成，并清理等待者");

            DiscardOldPresentation();
            session.FrameReady += CancelAfterNativeRender;
            var lateSeek = SeekAsync(before.Time, renderedCancellation.Token); operations.Add(lateSeek);
            check(await IsCancelledAsync(lateSeek, limit)
                && Interlocked.Read(ref cancelledSequence) > before.Sequence
                && LastFrame is null && WaiterCount() == originalWaiters,
                "原生绘制刚结束才取消的旧定位不会等待已被清掉的帧或触发30秒超时");
        }
        finally
        {
            session.FrameReady -= CancelAfterNativeRender;
            waitingCancellation.Cancel(); renderedCancellation.Cancel();
            lock (frameGate) showingEmptyCanvas = wasEmpty;
            OnFrameReady(session, session.LastFrame ?? before);
            await AwaitPresentationAsync();
            foreach (var operation in operations)
                try { await operation.WaitAsync(limit); } catch (OperationCanceledException) { }
        }
    }
}
