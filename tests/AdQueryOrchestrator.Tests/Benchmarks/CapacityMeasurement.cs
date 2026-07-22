using System.Diagnostics;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// A single retained-memory / working-set sample around a measured action.
/// All figures are bytes. "Retained" is heap still live after a forced collection,
/// which is the figure the P05 capacity equation divides by the active-CSV count.
/// </summary>
internal readonly record struct CapacitySample(
    long AllocatedBytesDelta,
    long RetainedHeapAboveBaseline,
    long PeakWorkingSet,
    long BaselineHeap,
    int Gen0,
    int Gen1,
    int Gen2,
    long ElapsedMs);

/// <summary>
/// Measures allocation, retained managed heap, and peak process working set around
/// a factory that produces a value the caller keeps rooted for the duration of the
/// retained-heap reading. The harness never treats elapsed time as a gate (P05:
/// wall-clock is informational), but records it for context.
/// </summary>
internal static class CapacityMeasurement
{
    /// <summary>
    /// Runs <paramref name="produce"/>, holds its result across a forced collection,
    /// and reports the retained heap attributable to it above a pre-measured baseline.
    /// The result is returned so the caller can decide when to release it.
    /// </summary>
    public static (T Value, CapacitySample Sample) Measure<T>(Func<T> produce)
        where T : class
    {
        Settle();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);
        var stopwatch = Stopwatch.StartNew();

        var value = produce();

        stopwatch.Stop();
        var allocAfter = GC.GetTotalAllocatedBytes(precise: true);

        // Force a full collection while the value is still rooted so surviving heap
        // is attributable to it, then read peak working set for the process.
        var retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var peakWorkingSet = process.PeakWorkingSet64;

        var sample = new CapacitySample(
            AllocatedBytesDelta: allocAfter - allocBefore,
            RetainedHeapAboveBaseline: retained,
            PeakWorkingSet: peakWorkingSet,
            BaselineHeap: baseline,
            Gen0: GC.CollectionCount(0) - g0,
            Gen1: GC.CollectionCount(1) - g1,
            Gen2: GC.CollectionCount(2) - g2,
            ElapsedMs: stopwatch.ElapsedMilliseconds);

        // Keep the value reachable until after every reading above.
        GC.KeepAlive(value);
        return (value, sample);
    }

    /// <summary>
    /// Stabilizes the heap before a baseline reading: two full blocking collections
    /// with finalizers drained between them.
    /// </summary>
    public static void Settle()
    {
        for (var i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }
}
