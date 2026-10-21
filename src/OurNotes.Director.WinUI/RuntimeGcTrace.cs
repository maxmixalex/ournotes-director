using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

// Created only by the real-window smoke workflow. No static listener, GC setting,
// forced collection, per-event formatting or per-event file write is involved.
internal sealed class RuntimeGcTrace : EventListener
{
    const string ProviderName = "Microsoft-Windows-DotNETRuntime";
    const int EventCapacity = 2048, AllocationCapacity = 256, MarkerCapacity = 32;
    readonly ConcurrentQueue<CapturedEvent> events = new();
    readonly ConcurrentQueue<CapturedEvent> allocations = new();
    readonly ConcurrentQueue<Marker> markers = new();
    readonly string path;
    readonly bool includeAllocationTicks;
    readonly DateTime createdUtc;
    readonly long createdTimestamp;
    readonly uint createdOsThreadId;
    readonly JsonObject initialMemory;
    EventSource? runtime;
    string? subscriptionError;
    int ready, stopped, eventCount, allocationCount, markerCount;
    bool subscriptionRequested;

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    readonly record struct CapturedEvent(EventWrittenEventArgs Data, long ReceivedTimestamp, int ReceiverManagedThreadId);
    readonly record struct Marker(string Name, DateTime Utc, long Timestamp, int ManagedThreadId);

    // Informational + GCKeyword collects collection reason/type and heap statistics.
    // Allocation ticks are sampled, cost additional runtime payload decoding, and
    // should be enabled only for a separate, short diagnostic capture if needed.
    internal RuntimeGcTrace(string path, bool includeAllocationTicks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        this.includeAllocationTicks = includeAllocationTicks;
        createdTimestamp = Stopwatch.GetTimestamp();
        createdUtc = DateTime.UtcNow;
        createdOsThreadId = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
        initialMemory = MemoryInfo();
        Volatile.Write(ref ready, 1);
        runtime ??= EventSource.GetSources().FirstOrDefault(source => source.Name == ProviderName);
        if (runtime is not null) Subscribe(runtime);
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != ProviderName) return;
        // EventListener's base constructor can call this before our fields are
        // initialized. Enabling is deferred until the derived constructor is ready.
        runtime = source;
        if (Volatile.Read(ref ready) != 0 && Volatile.Read(ref stopped) == 0) Subscribe(source);
    }

    void Subscribe(EventSource source)
    {
        try
        {
            EnableEvents(source, includeAllocationTicks ? EventLevel.Verbose : EventLevel.Informational, (EventKeywords)1);
            subscriptionRequested = true;
        }
        catch (Exception error) { subscriptionError = error.GetType().Name + ": " + error.Message; }
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (Volatile.Read(ref stopped) != 0 || data.EventSource.Name != ProviderName) return;
        if (data.EventId == 10)
        {
            if (!includeAllocationTicks) return;
            if (Interlocked.Increment(ref allocationCount) <= AllocationCapacity)
                allocations.Enqueue(new(data, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));
            return;
        }
        // Start/end, EE pause boundaries, heap stats, trigger and memory pressure.
        // Event 35 is verbose-only; event 0 preserves an EventSource diagnostic.
        if (data.EventId is not (0 or 1 or 2 or 3 or 4 or 7 or 8 or 9 or 35 or 200 or 201)) return;
        if (Interlocked.Increment(ref eventCount) <= EventCapacity)
            events.Enqueue(new(data, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));
    }

    internal void Mark(string name)
    {
        if (Volatile.Read(ref stopped) != 0 || Interlocked.Increment(ref markerCount) > MarkerCapacity) return;
        markers.Enqueue(new(name, DateTime.UtcNow, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        DateTime stoppedUtc = DateTime.UtcNow;
        long stoppedTimestamp = Stopwatch.GetTimestamp();
        try { if (runtime is not null && subscriptionRequested) DisableEvents(runtime); }
        catch (Exception error) { subscriptionError ??= error.GetType().Name + ": " + error.Message; }
        finally { base.Dispose(); }
        var finalMemory = MemoryInfo();

        // JSON and memory snapshots are produced after the listener is disabled,
        // outside the measured interaction and even when the smoke body throws.
        var rows = new JsonArray();
        foreach (var captured in events.OrderBy(value => value.Data.TimeStamp).ThenBy(value => value.ReceivedTimestamp))
            rows.Add(EventJson(captured));
        var samples = new JsonArray();
        foreach (var captured in allocations.OrderBy(value => value.Data.TimeStamp).ThenBy(value => value.ReceivedTimestamp))
            samples.Add(EventJson(captured));
        var phases = new JsonArray();
        foreach (var marker in markers)
            phases.Add(new JsonObject { ["name"] = marker.Name, ["utc"] = marker.Utc.ToString("O", CultureInfo.InvariantCulture),
                ["elapsedMs"] = Stopwatch.GetElapsedTime(createdTimestamp, marker.Timestamp).TotalMilliseconds,
                ["stopwatchTimestamp"] = marker.Timestamp, ["managedThreadId"] = marker.ManagedThreadId });
        var report = new JsonObject
        {
            ["schema"] = "ournotes-director/runtime-gc-events/1", ["provider"] = ProviderName,
            ["providerSeen"] = runtime is not null, ["subscriptionRequested"] = subscriptionRequested,
            ["subscriptionError"] = subscriptionError, ["runtime"] = Environment.Version.ToString(),
            ["serverGC"] = GCSettings.IsServerGC, ["latencyMode"] = GCSettings.LatencyMode.ToString(),
            ["keywords"] = "0x1", ["level"] = includeAllocationTicks ? 5 : 4,
            ["createdUtc"] = createdUtc.ToString("O", CultureInfo.InvariantCulture),
            ["createdOsThreadId"] = createdOsThreadId,
            ["stoppedUtc"] = stoppedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["createdStopwatchTimestamp"] = createdTimestamp, ["stopwatchFrequency"] = Stopwatch.Frequency,
            ["durationMs"] = Stopwatch.GetElapsedTime(createdTimestamp, stoppedTimestamp).TotalMilliseconds,
            ["eventTimeSource"] = "Native EventPipe timestamp, not managed callback time",
            ["delivery"] = "Asynchronous; EventListener has no public flush or native lost-event counter",
            ["retention"] = "First bounded events; counts include later records omitted at capacity",
            ["eventCapacity"] = EventCapacity, ["eventsObserved"] = Volatile.Read(ref eventCount),
            ["eventsOmittedAtCapacity"] = Math.Max(0, Volatile.Read(ref eventCount) - rows.Count),
            ["allocationTicksEnabled"] = includeAllocationTicks, ["allocationCapacity"] = AllocationCapacity,
            ["allocationTicksObserved"] = Volatile.Read(ref allocationCount),
            ["allocationTicksOmittedAtCapacity"] = Math.Max(0, Volatile.Read(ref allocationCount) - samples.Count),
            ["initialMemory"] = initialMemory, ["finalMemory"] = finalMemory,
            ["markers"] = phases, ["events"] = rows, ["allocationTicks"] = samples
        };
        DesktopSettings.AtomicWrite(path, report.ToJsonString(DesktopSettings.JsonOptions));
    }

    JsonObject EventJson(CapturedEvent captured)
    {
        var data = captured.Data;
        var payload = new JsonObject();
        for (int i = 0; i < data.Payload?.Count; i++)
        {
            string name = data.PayloadNames is { } names && i < names.Count ? names[i] : "field" + i;
            payload[name] = PayloadValue(data.Payload[i]);
        }
        var row = new JsonObject
        {
            ["eventId"] = data.EventId, ["version"] = data.Version, ["name"] = data.EventName,
            ["utc"] = data.TimeStamp.ToString("O", CultureInfo.InvariantCulture),
            ["eventElapsedMs"] = (data.TimeStamp - createdUtc).TotalMilliseconds,
            ["osThreadId"] = data.OSThreadId,
            ["receivedElapsedMs"] = Stopwatch.GetElapsedTime(createdTimestamp, captured.ReceivedTimestamp).TotalMilliseconds,
            ["receiverManagedThreadId"] = captured.ReceiverManagedThreadId, ["payload"] = payload
        };
        if (data.EventId is 1 or 35 && payload["Reason"] is JsonValue reason && reason.TryGetValue<uint>(out uint code))
            row["reasonName"] = ReasonName(code);
        if (data.EventId == 1 && payload["Type"] is JsonValue type && type.TryGetValue<uint>(out uint kind))
            row["collectionType"] = kind switch { 0 => "Blocking", 1 => "Background", 2 => "ForegroundDuringBackground", _ => "Unknown" };
        return row;
    }

    static JsonNode? PayloadValue(object? value) => value switch
    {
        null => null, string text => JsonValue.Create(text), bool flag => JsonValue.Create(flag),
        byte number => JsonValue.Create(number), sbyte number => JsonValue.Create(number),
        short number => JsonValue.Create(number), ushort number => JsonValue.Create(number),
        int number => JsonValue.Create(number), uint number => JsonValue.Create(number),
        long number => JsonValue.Create(number), ulong number => JsonValue.Create(number),
        float number => JsonValue.Create(number), double number => JsonValue.Create(number),
        IntPtr pointer => JsonValue.Create("0x" + pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture)),
        UIntPtr pointer => JsonValue.Create("0x" + pointer.ToUInt64().ToString("X", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.GetType().FullName)
    };

    static string ReasonName(uint reason) => reason switch
    {
        0 => "AllocSmall", 1 => "Induced", 2 => "LowMemory", 3 => "Empty", 4 => "AllocLarge",
        5 => "OutOfSpaceSmall", 6 => "OutOfSpaceLarge", 7 => "InducedNotForced", 8 => "Stress",
        9 => "LowMemoryBlocking", 10 => "InducedCompacting", 11 => "LowMemoryHost",
        12 => "ProvisionalModeFullGC", 13 => "LowMemoryHostBlocking", 14 => "BackgroundTuningSmall",
        15 => "BackgroundTuningLarge", 16 => "BackgroundStepping", 17 => "InducedAggressive", _ => "Unknown"
    };

    static JsonObject MemoryInfo()
    {
        var info = GC.GetGCMemoryInfo();
        var generations = new JsonArray();
        foreach (var generation in info.GenerationInfo)
            generations.Add(new JsonObject { ["sizeBeforeBytes"] = generation.SizeBeforeBytes,
                ["sizeAfterBytes"] = generation.SizeAfterBytes,
                ["fragmentationBeforeBytes"] = generation.FragmentationBeforeBytes,
                ["fragmentationAfterBytes"] = generation.FragmentationAfterBytes });
        var pauses = new JsonArray();
        foreach (var pause in info.PauseDurations) pauses.Add((JsonNode?)JsonValue.Create(pause.TotalMilliseconds));
        return new JsonObject
        {
            ["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["workingSetBytes"] = Environment.WorkingSet, ["managedMemoryBytes"] = GC.GetTotalMemory(false),
            ["gcIndex"] = info.Index, ["generation"] = info.Generation, ["compacted"] = info.Compacted,
            ["concurrent"] = info.Concurrent, ["heapSizeBytes"] = info.HeapSizeBytes,
            ["fragmentedBytes"] = info.FragmentedBytes, ["committedBytes"] = info.TotalCommittedBytes,
            ["memoryLoadBytes"] = info.MemoryLoadBytes, ["highMemoryLoadThresholdBytes"] = info.HighMemoryLoadThresholdBytes,
            ["availableMemoryBytes"] = info.TotalAvailableMemoryBytes, ["promotedBytes"] = info.PromotedBytes,
            ["pinnedObjects"] = info.PinnedObjectsCount, ["finalizationPending"] = info.FinalizationPendingCount,
            ["gen0Collections"] = GC.CollectionCount(0), ["gen1Collections"] = GC.CollectionCount(1),
            ["gen2Collections"] = GC.CollectionCount(2), ["totalAllocatedBytes"] = GC.GetTotalAllocatedBytes(false),
            ["totalPauseMs"] = GC.GetTotalPauseDuration().TotalMilliseconds,
            ["generationInfo"] = generations, ["lastGcPauseMs"] = pauses
        };
    }
}
