using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WorkstationLens.App;

public sealed record ProcessPoint(int Pid, long Started, string Name, double Cpu, long WorkingBytes,
    long PrivateBytes, double Gpu, long DedicatedBytes, long SharedBytes, double ReadBps, double WriteBps);
public sealed record MachinePoint(long Time, double Cpu, double BusiestCore, double GpuEngine,
    long GpuMemoryUsed, long GpuMemoryTotal,
    long RamUsed, long RamTotal, double DiskBytesPerSecond, int Processes,
    long CommittedBytes, long CommitLimit, double PageReadsPerSec, double PagesInputPerSec,
    double DiskReadLatencyMs, double DiskWriteLatencyMs, double DiskReadOpsPerSec,
    double DiskWriteOpsPerSec);
public sealed record Snapshot(MachinePoint Machine, List<ProcessPoint> Processes);

// Windows Performance Data Helper: wildcard counters are queried together in a single PDH pass.
// This avoids spawning a PowerShell/Get-Counter subprocess every 15 seconds.
internal sealed partial class PdhGpuSampler : IDisposable
{
    private const uint FmtDouble = 0x200;
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();
    public string Status { get; private set; } = "initialising";
    public long TotalGpuMemory { get; private set; }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint OpenQuery(string? source, nint userData, out nint query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint AddCounter(nint query, string path, nint userData, out nint counter);
    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    private static partial uint Collect(nint query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint GetArray(nint counter, uint format, ref uint bytes, ref uint count, nint buffer);
    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    private static partial uint Close(nint query);

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public uint Status; public double Value; }

    private static readonly Regex PidRegex = new(@"(?:^|_)pid_(\d+)(?:_|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PdhGpuSampler()
    {
        if (OpenQuery(null, 0, out _query) != 0) { Status = "PDH unavailable"; return; }
        Add("engine", @"\GPU Engine(*)\Utilization Percentage");
        Add("dedicated", @"\GPU Process Memory(*)\Dedicated Usage");
        Add("shared", @"\GPU Process Memory(*)\Shared Usage");
        Add("adapter", @"\GPU Adapter Memory(*)\Dedicated Usage");
        Add("cpu", @"\Processor Information(*)\% Processor Time");
        Add("disk", @"\PhysicalDisk(_Total)\Disk Bytes/sec");
        Add("commit", @"\Memory\Committed Bytes");
        Add("limit", @"\Memory\Commit Limit");
        Add("pageReads", @"\Memory\Page Reads/sec");
        Add("pagesInput", @"\Memory\Pages Input/sec");
        Add("diskReadLatency", @"\PhysicalDisk(_Total)\Avg. Disk sec/Read");
        Add("diskWriteLatency", @"\PhysicalDisk(_Total)\Avg. Disk sec/Write");
        Add("diskReads", @"\PhysicalDisk(_Total)\Disk Reads/sec");
        Add("diskWrites", @"\PhysicalDisk(_Total)\Disk Writes/sec");
        Status = _counters.ContainsKey("engine") ? "Windows WDDM counters available" : "GPU counters unavailable";
        TotalGpuMemory = ReadNvidiaMemoryTotal();
        Collect(_query); // prime rate counters; subsequent samples have calculated values
    }

    private static long ReadNvidiaMemoryTotal()
    {
        // One short call at startup; repeated 15-second sampling itself uses native PDH.
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo("nvidia-smi.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits"
            };
            if (!p.Start()) return 0;
            if (!p.WaitForExit(2500)) { p.Kill(); return 0; }
            string result = p.StandardOutput.ReadToEnd().Split('\n')[0].Trim();
            return long.TryParse(result, out var mib) && mib > 0 ? mib * 1024 * 1024 : 0;
        }
        catch { return 0; }
    }

    private void Add(string key, string path)
    {
        if (_query != 0 && AddCounter(_query, path, 0, out var counter) == 0)
            _counters[key] = counter;
    }

    private List<(string Name, double Value)> Read(string key)
    {
        if (!_counters.TryGetValue(key, out var c)) return [];
        uint bytes = 0, count = 0;
        GetArray(c, FmtDouble, ref bytes, ref count, 0);
        if (bytes == 0 || bytes > 16_000_000) return [];
        var mem = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (GetArray(c, FmtDouble, ref bytes, ref count, mem) != 0) return [];
            int stride = Marshal.SizeOf<CounterItem>();
            var output = new List<(string, double)>((int)count);
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(mem, i * stride));
                if (item.Status <= 1 && double.IsFinite(item.Value))
                    output.Add((Marshal.PtrToStringUni(item.Name) ?? "", Math.Max(0, item.Value)));
            }
            return output;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    private double ReadTotal(string key) => Read(key).FirstOrDefault().Value;

    public (Dictionary<int, (double Gpu, long Dedicated, long Shared)> Processes,
        double Cpu, double BusiestCore, double Gpu, double Disk, long GpuMemory,
        long Commit, long Limit, double PageReads, double PagesInput,
        double DiskReadLatencyMs, double DiskWriteLatencyMs, double DiskReads, double DiskWrites) Sample()
    {
        var values = new Dictionary<int, (double Gpu, long Dedicated, long Shared)>();
        if (_query == 0 || Collect(_query) != 0) return (values, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        double busy = 0;
        foreach (var (name, value) in Read("engine"))
        {
            // Do not add separate 3D/Copy/Compute engines as though they were one GPU.
            busy = Math.Max(busy, value);
            var match = PidRegex.Match(name);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int pid)) continue;
            values.TryGetValue(pid, out var v);
            values[pid] = (Math.Min(100, Math.Max(v.Gpu, value)), v.Dedicated, v.Shared);
        }
        foreach (var key in new[] { "dedicated", "shared" })
        {
            foreach (var (name, value) in Read(key))
            {
                var match = PidRegex.Match(name);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int pid)) continue;
                values.TryGetValue(pid, out var v);
                long bytes = (long)Math.Min(long.MaxValue, value);
                // Different LUIDs may refer to multiple adapters. Sum their reported allocations.
                values[pid] = key == "dedicated" ? (v.Gpu, v.Dedicated + bytes, v.Shared)
                    : (v.Gpu, v.Dedicated, v.Shared + bytes);
            }
        }
        double totalCpu = 0, coreMax = 0;
        foreach (var (name, value) in Read("cpu"))
        {
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) totalCpu = value;
            else if (Regex.IsMatch(name, @"^\d+,\d+$|^\d+$")) coreMax = Math.Max(coreMax, value);
        }
        return (values, Math.Min(100, totalCpu), Math.Min(100, coreMax), Math.Min(100, busy),
            ReadTotal("disk"),
            (long)Read("adapter").Select(x=>x.Value).DefaultIfEmpty(0).Max(),
            (long)ReadTotal("commit"), (long)ReadTotal("limit"), ReadTotal("pageReads"),
            ReadTotal("pagesInput"), ReadTotal("diskReadLatency")*1000,
            ReadTotal("diskWriteLatency")*1000, ReadTotal("diskReads"), ReadTotal("diskWrites"));
    }

    public void Dispose() { if (_query != 0) { Close(_query); _query = 0; } }
}

internal sealed partial class ProcessSampler : IDisposable
{
    private readonly PdhGpuSampler _gpu = new();
    private readonly Dictionary<(int Pid, long Start), (TimeSpan Cpu, DateTime Time)> _previous = new();
    private readonly Dictionary<(int Pid, long Start), (ulong Read, ulong Write, DateTime Time)> _previousIo = new();
    public string GpuStatus => _gpu.Status;
    public int Enumerated { get; private set; }
    public int Skipped { get; private set; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length; public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatus(ref MemoryStatus state);

    // The Win32 I/O counters include file, device and network I/O. A delta
    // provides useful attributed demand without process names or file paths.
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }
    [LibraryImport("kernel32.dll", EntryPoint="GetProcessIoCounters")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint handle, out IoCounters counters);

    public Snapshot Sample()
    {
        var now = DateTime.UtcNow;
        var gpu = _gpu.Sample();
        var points = new List<ProcessPoint>(500);
        var active = new HashSet<(int, long)>();
        var running = Process.GetProcesses();
        Enumerated = running.Length;
        Skipped = 0;
        foreach (var p in running)
        {
            using (p)
            {
                try
                {
                    int pid = p.Id;
                    string name = p.ProcessName;
                    long start;
                    try { start = p.StartTime.ToUniversalTime().Ticks; }
                    catch { start = 0; } // inaccessible protected process
                    var key = (pid, start);
                    TimeSpan? cpuTicks = null;
                    try { cpuTicks = p.TotalProcessorTime; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    double cpu = 0;
                    if (cpuTicks.HasValue && _previous.TryGetValue(key, out var last))
                    {
                        double duration = (now - last.Time).TotalSeconds;
                        if (duration > 0 && cpuTicks.Value >= last.Cpu)
                            cpu = Math.Clamp((cpuTicks.Value - last.Cpu).TotalSeconds / duration * 100 / Environment.ProcessorCount, 0, 100);
                    }
                    if (cpuTicks.HasValue) _previous[key] = (cpuTicks.Value, now);
                    active.Add(key);
                    gpu.Processes.TryGetValue(pid, out var g);
                    double readBps = 0, writeBps = 0;
                    try
                    {
                        if (GetProcessIoCounters(p.Handle, out var io))
                        {
                            if (_previousIo.TryGetValue(key, out var prior) && now > prior.Time)
                            {
                                double seconds = (now - prior.Time).TotalSeconds;
                                if (io.ReadBytes >= prior.Read) readBps = (io.ReadBytes - prior.Read)/seconds;
                                if (io.WriteBytes >= prior.Write) writeBps = (io.WriteBytes - prior.Write)/seconds;
                            }
                            _previousIo[key] = (io.ReadBytes, io.WriteBytes, now);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    long working = 0, privateBytes = 0;
                    try { working = Math.Max(0, p.WorkingSet64); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    try { privateBytes = Math.Max(0, p.PrivateMemorySize64); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    points.Add(new(pid, start, name, cpu, working, privateBytes, g.Gpu, g.Dedicated, g.Shared, readBps, writeBps));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                { Skipped++; /* process exited, access denied or protected: skip this observation */ }
            }
        }
        foreach (var old in _previous.Keys.Where(k => !active.Contains(k)).ToArray()) _previous.Remove(old);
        foreach (var old in _previousIo.Keys.Where(k => !active.Contains(k)).ToArray()) _previousIo.Remove(old);
        var ram = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatus(ref ram);
        var machine = new MachinePoint(new DateTimeOffset(now).ToUnixTimeSeconds(), gpu.Cpu,
            gpu.BusiestCore, gpu.Gpu, gpu.GpuMemory, _gpu.TotalGpuMemory,
            (long)(ram.TotalPhysical - ram.AvailablePhysical),
            (long)ram.TotalPhysical, gpu.Disk, points.Count,
            gpu.Commit, gpu.Limit, gpu.PageReads, gpu.PagesInput,
            gpu.DiskReadLatencyMs, gpu.DiskWriteLatencyMs, gpu.DiskReads, gpu.DiskWrites);
        return new(machine, points);
    }

    public void Dispose() => _gpu.Dispose();
}
