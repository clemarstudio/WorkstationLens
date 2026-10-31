using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace WorkstationLens.App;

internal sealed class CollectorService(MetricsStore store, ILogger<CollectorService> logger) : BackgroundService
{
    // A tiny in-memory ring makes 15-second points available to every newly
    // opened dashboard, including before the FIRST full minute is persisted.
    // It is NOT a second database and never retains process names/details.
    private readonly object _recentGate = new();
    private readonly Queue<MachinePoint> _recent = new();
    public MachinePoint[] RecentMachine(long afterUnixSeconds = 0)
    {
        lock (_recentGate) return _recent.Where(x => x.Time > afterUnixSeconds).ToArray();
    }

    public Snapshot? Latest { get; private set; }
    public string GpuStatus { get; private set; } = "initialising";
    public string? LastError { get; private set; }
    public long Samples { get; private set; }
    public long StoredMinutes { get; private set; }
    public int EnumeratedProcesses { get; private set; }
    public int SkippedProcesses { get; private set; }
    public double LastSampleMs { get; private set; }
    private readonly DateTime _started = DateTime.UtcNow;
    public DateTime Started => _started;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var sampler = new ProcessSampler();
        GpuStatus = sampler.GpuStatus;
        MinuteBucket? minute = null;
        DateTime lastRetention = DateTime.MinValue;
        try
        {
            // PDH rate counters can return a bogus 100% total CPU reading on
            // their first formatted result after process startup. Prime one
            // complete sampler observation, discard it, then allow a meaningful
            // rate interval before the first persisted snapshot.
            sampler.Sample();
            // PDH needs a short interval between its priming call and its
            // first trustworthy CPU-rate measurement. Do not show the
            // unreliable priming CPU as a real zero-second reading.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            do
            {
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    var sample = sampler.Sample();
                    Latest = sample;
                    lock (_recentGate)
                    {
                        _recent.Enqueue(sample.Machine);
                        if (_recent.Count > 40) _recent.Dequeue(); // 10 min × 15s
                    }
                    EnumeratedProcesses = sampler.Enumerated;
                    SkippedProcesses = sampler.Skipped;
                    Samples++;
                    long next = sample.Machine.Time / 60 * 60;
                    if (minute != null && minute.Time != next)
                    {
                        store.Save(minute);
                        StoredMinutes++;
                        minute = null;
                    }
                    minute ??= new MinuteBucket(next);
                    minute.Add(sample);
                    LastError = null;
                    if ((DateTime.UtcNow - lastRetention).TotalHours > 24)
                    {
                        store.Retain();
                        lastRetention = DateTime.UtcNow;
                    }
                    LastSampleMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    logger.LogError(ex,"Collector sample or persistence error");
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (minute?.Samples > 0)
                try { store.Save(minute); } catch (Exception ex) { logger.LogError(ex,"Could not flush final minute"); }
        }
    }
}
