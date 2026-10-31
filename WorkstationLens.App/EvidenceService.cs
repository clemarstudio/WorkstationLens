using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WorkstationLens.App;

internal sealed class WorkloadLabelInput
{
    public string? Category { get; set; }
    public string? Note { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
}

internal sealed class EvidenceService
{
    private readonly MetricsStore _store;
    private readonly string _dbConnection;
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    { "local-llm", "llm-offload", "llm-gpu-only", "no-local-llm", "revit", "coding", "gaming", "numerai", "freqtrade", "other" };

    public EvidenceService(MetricsStore store)
    {
        _store = store;
        _dbConnection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = store.Path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS workload_labels(
                id INTEGER PRIMARY KEY AUTOINCREMENT, category TEXT NOT NULL, note TEXT NOT NULL,
                start INTEGER NOT NULL, end INTEGER NOT NULL, created INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_workload_labels_start_end ON workload_labels(start,end);
            """;
        cmd.ExecuteNonQuery();
    }

    private Microsoft.Data.Sqlite.SqliteConnection Open()
    {
        var c = new Microsoft.Data.Sqlite.SqliteConnection(_dbConnection); c.Open(); return c;
    }
    public object Add(WorkloadLabelInput input)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (input.Category is null || !Categories.Contains(input.Category)
            || input.End < input.Start || input.End > now + 3600 || input.Start < now - 90*86400
            || input.Note is null || input.Note.Length > 300)
            throw new ArgumentException("Invalid category, time range or note (max 300 characters; past 90 days).");
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workload_labels(category,note,start,end,created)
            VALUES($category,$note,$start,$end,$created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$category",input.Category.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$note",input.Note);
        cmd.Parameters.AddWithValue("$start",input.Start);
        cmd.Parameters.AddWithValue("$end",input.End);
        cmd.Parameters.AddWithValue("$created",now);
        long id = (long)(cmd.ExecuteScalar() ?? 0L);
        return new { id, input.Category, input.Note, input.Start, input.End };
    }
    public void Delete(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM workload_labels WHERE id=$id";
        cmd.Parameters.AddWithValue("$id",id);
        cmd.ExecuteNonQuery();
    }
    public List<Dictionary<string,object?>> Labels()
        => _store.Query("SELECT id,category,note,start,end,created FROM workload_labels ORDER BY start DESC LIMIT 500");

    // A portable, bounded evidence file: no process command lines, path names,
    // window titles, URLs, network traffic content or externally uploaded data.
    // Missing historical metrics remain null, NOT observed zero.
    public object Build(int hours) => Build(TimeWindow.LastHours(hours));
    public object Build(TimeWindow window)
    {
        long start = window.Start;
        long end = window.End;
        var system = _store.Query("""
            SELECT time,
                CASE WHEN samples<=2 AND cpu_peak>=99 THEN NULL ELSE cpu END AS cpu,
                CASE WHEN samples<=2 AND cpu_peak>=99 THEN NULL ELSE core_peak END AS core_peak,
                gpu,gpu_peak,ram,ram_total,
                CASE WHEN time < 1793275260 AND gpu_mem_used=0 THEN NULL ELSE gpu_mem_used END AS gpu_mem_used,
                gpu_mem_total,processes,commit_bytes,commit_limit,
                page_reads_sec,page_reads_peak,pages_input_sec,
                read_latency_ms,read_latency_peak,write_latency_ms,write_latency_peak,
                disk_read_ops,disk_write_ops,hot_core_samples,samples
            FROM machine_minute WHERE time >= $since AND time < $until ORDER BY time
            """,("$since",start),("$until",end));
        const string groupedSql = """
            SELECT name,SUM(minutes) AS active_minutes,
                ROUND(SUM(cpu_sum)/SUM(minutes),4) AS average_cpu_pct_when_active,
                MAX(cpu_peak) AS cpu_peak_sampled, ROUND(MAX(working_peak)/1073741824.0,3) AS ram_gib_peak_sampled,
                CASE WHEN MAX(dedicated_peak) > (SELECT MAX(gpu_mem_total) FROM machine_minute)
                     THEN NULL ELSE ROUND(MAX(dedicated_peak)/1073741824.0,3)
                END AS gpu_memory_attributed_gib_peak,
                ROUND(SUM(cpu_sum),3) AS cpu_pct_minutes,
                ROUND(SUM(read_bps_sum)/SUM(minutes),1) AS avg_io_read_bps,
                ROUND(SUM(write_bps_sum)/SUM(minutes),1) AS avg_io_write_bps
            FROM app_quarter WHERE time >= $since AND time < $until GROUP BY name
            """;
        var apps = _store.Query(groupedSql+" ORDER BY cpu_pct_minutes DESC LIMIT 30",("$since",start),("$until",end));
        var ramApps = _store.Query(groupedSql+" ORDER BY ram_gib_peak_sampled DESC LIMIT 20",("$since",start),("$until",end));
        var vramApps = _store.Query(groupedSql+" ORDER BY gpu_memory_attributed_gib_peak DESC LIMIT 20",("$since",start),("$until",end));
        var ioApps = _store.Query(groupedSql+" ORDER BY coalesce(avg_io_read_bps,0)+coalesce(avg_io_write_bps,0) DESC LIMIT 20",("$since",start),("$until",end));
        var labels = Labels().Where(x=>Convert.ToInt64(x["end"])>=start &&
            Convert.ToInt64(x["start"])<end).ToArray();
        var byTag = new List<object>();
        foreach (var category in labels.Select(x=>Convert.ToString(x["category"])!).Distinct())
        {
            var ranges = labels.Where(x=>string.Equals(Convert.ToString(x["category"]),category,StringComparison.OrdinalIgnoreCase))
                .Select(x=>(Start:Convert.ToInt64(x["start"]),End:Convert.ToInt64(x["end"]))).ToArray();
            var covered = system.Where(x=>ranges.Any(r=>Convert.ToInt64(x["time"])>=r.Start && Convert.ToInt64(x["time"])<=r.End)).ToArray();
            byTag.Add(new {category, count=covered.Length, metrics=Metrics(covered)});
        }
        var perDay = system.GroupBy(x=>DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(x["time"])).UtcDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))
            .Select(g=>new { day=g.Key, minutes=g.Count(), metrics=Metrics(g.ToList()) }).ToArray();
        return new {
            format="workstation-lens-evidence-v1",
            exportedUtc=DateTimeOffset.UtcNow,
            observationHoursRequested=Math.Round(window.RequestedHours,3),
            requestedWindowStartUtc=DateTimeOffset.FromUnixTimeSeconds(start),
            requestedWindowEndUtc=DateTimeOffset.FromUnixTimeSeconds(end),
            customWindow=window.IsCustom,
            observationMinutes=system.Count,
            timeStartUtc=system.Count>0 ? DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(system[0]["time"])) : (DateTimeOffset?)null,
            timeEndUtc=system.Count>0 ? DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(system[^1]["time"])) : (DateTimeOffset?)null,
            host = new {os=RuntimeInformation.OSDescription, architecture=RuntimeInformation.OSArchitecture.ToString(),
                logicalProcessors=Environment.ProcessorCount, physicalRamGiB=system.Count>0 ? Num(system[^1],"ram_total")/1073741824d : 0,
                gpuMemoryGiB=system.Count>0 ? Num(system[^1],"gpu_mem_total")/1073741824d : 0},
            all=Metrics(system), days=perDay, labels, taggedPeriods=byTag, cpuRankedApplications=apps,
            ramRankedApplications=ramApps, attributedGpuMemoryRankedApplications=vramApps, ioRankedApplications=ioApps,
            scope = new {
                aggregation="15 second sampling; 1-minute machine statistics and application quarter-hour summaries",
                window="UTC [start,end) exclusive end; application quarter-hour aggregates have 15-minute time resolution and may be partial at boundaries",
                cpu="Percent of host machine-wide CPU capacity, not a performance benchmark or target-system CPU requirement",
                gpu="WDDM busiest engine; not CUDA SM occupancy",
                attributedGpuMemory="WDDM per-process allocations are not additive physical VRAM. Values exceeding GPU physical capacity (e.g. GalaxyClient reporting >32 GiB) are treated as untrustworthy and excluded from GPU attributed peak rankings; use GPU Adapter Memory for sizing",
                systemRam="Global physically in-use memory, includes reclaimable caches; check page-read and commit pressure",
                io="Win32 per-process I/O bytes can include device/network, not exclusively SSD; disk latency covers all physical disks",
                labels="User-entered time labels; overlapping periods count in each tag and do not establish causality",
                missingMetrics="Columns introduced on 2026-10-30 have NULL for earlier historical rows; exclude unknowns from statistics",
                startupCpuAnomaly="Previously observed startup counter 100% with <=2 samples filtered from exported CPU statistics; raw DB history remains intact",
                boundaries="No inference of target GPU capacity, RAM savings, thermal behaviour, responsiveness, model quality, Pi 5 architecture compatibility or performance without controlled counterfactual tests"
            },
            questions = new [] {
                "Q1 without LLM offload: compare explicitly labelled llm-gpu-only or no-local-llm runs with llm-offload runs; report GPU resident VRAM P95 and maxima for 16/24 GB; avoid assuming historical unlabeled sessions had no LLM",
                "Q2 resource hogs: rank CPU percentage-minutes, concurrent RAM, I/O; distinguish persistent activity from legitimate workload; inspect processes only after user approval",
                "Q3 without local LLM: use no-local-llm sessions or matched before-after benchmarks, compare core pressure, RAM, VRAM, paging and storage; do not subtract process Working Sets as independent savings",
                "Q4 Numerai/Freqtrade to Pi 5: require labelled job runtime, simultaneous memory peaks, CPU saturation, I/O, time-to-completion, target ARM64 dependencies, Raspberry Pi 5 RAM/model, networking and reliability; Windows percentages do not predict ARM speed"
            }
        };
    }
    /// <summary>A compact, human-readable evidence brief for any web LLM.</summary>
    public string BuildMarkdown(int hours) => BuildMarkdown(TimeWindow.LastHours(hours));
    public string BuildMarkdown(TimeWindow window)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Build(window)));
        var root = doc.RootElement;
        var all = root.GetProperty("all");
        int minutes = root.GetProperty("observationMinutes").GetInt32();
        string Value(JsonElement item,string property)
        {
            var value=item.GetProperty(property);
            return value.ValueKind==JsonValueKind.Number
                ? value.GetDouble().ToString("0.###",CultureInfo.InvariantCulture) : "not measured";
        }
        var report = new StringBuilder();
        report.AppendLine("# Workstation Lens — local workload evidence")
            .AppendLine()
            .AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} · Requested window (UTC): {DateTimeOffset.FromUnixTimeSeconds(window.Start):yyyy-MM-dd HH:mm} to {DateTimeOffset.FromUnixTimeSeconds(window.End):yyyy-MM-dd HH:mm} (end exclusive).")
            .AppendLine($"Recorded observation minutes: **{minutes}**. Unrecorded hours are not idle observations.")
            .AppendLine()
            .AppendLine("## Machine-wide measurements")
            .AppendLine()
            .AppendLine("| Metric | Valid minutes | Average | P95 | P99 | Highest minute |")
            .AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (name,key) in new (string,string)[]{
            ("CPU total (%)","cpuPct"),("Busiest core sampled (%)","hottestCorePct"),
            ("GPU busiest engine (%)","gpuEnginePct"),("Physical RAM used (GiB)","ramGiB"),
            ("GPU Adapter VRAM used (GiB)","gpuVramGiB"),("Committed memory (GiB)","commitGiB"),
            ("Hard page reads/sec","hardPageReadsPerSec"),
            ("Disk read latency with I/O (ms)","readLatencyMsWithIo"),
            ("Disk write latency with I/O (ms)","writeLatencyMsWithIo")
        })
        {
            var stat=all.GetProperty(key);
            report.AppendLine($"| {name} | {stat.GetProperty("observations").GetInt32()} | {Value(stat,"avg")} | {Value(stat,"p95")} | {Value(stat,"p99")} | {Value(stat,"max")} |");
        }
        report.AppendLine().AppendLine("## Top CPU-active applications")
            .AppendLine("CPU %-minutes adds CPU percentage over active minutes (e.g. 10% for 3 minutes = 30 %-minutes).")
            .AppendLine().AppendLine("| Application | CPU %-minutes | Active minutes | Peak RAM GiB |")
            .AppendLine("|---|---:|---:|---:|");
        foreach (var item in root.GetProperty("cpuRankedApplications").EnumerateArray().Take(15))
        {
            var name=item.GetProperty("name").GetString()??"unknown";
            // Neutralize Markdown cell delimiters; this is a downloadable text report.
            name=name.Replace("|","/").Replace("\r"," ").Replace("\n"," ");
            report.AppendLine($"| {name} | {Value(item,"cpu_pct_minutes")} | {Value(item,"active_minutes")} | {Value(item,"ram_gib_peak_sampled")} |");
        }
        report.AppendLine().AppendLine("## User-confirmed workload periods");
        var labels=root.GetProperty("labels").EnumerateArray().Take(30).ToArray();
        if (labels.Length==0) report.AppendLine("No tagged sessions. Do not assume that unlabelled periods were free of local LLMs or other jobs.");
        foreach(var label in labels)
        {
            var category=label.GetProperty("category").GetString();
            var from=DateTimeOffset.FromUnixTimeSeconds(label.GetProperty("start").GetInt64());
            var to=DateTimeOffset.FromUnixTimeSeconds(label.GetProperty("end").GetInt64());
            var note=(label.GetProperty("note").GetString()??"").Replace("\r"," ").Replace("\n"," ");
            report.AppendLine($"- **{category}**: {from:yyyy-MM-dd HH:mm}–{to:yyyy-MM-dd HH:mm} UTC. {note}");
        }
        report.AppendLine().AppendLine("## Measurement limitations")
            .AppendLine("- CPU percent is relative to the existing host; it does not predict a different CPU or Raspberry Pi runtime.")
            .AppendLine("- App Working Sets and WDDM GPU allocations can double count shared memory. Use *GPU Adapter VRAM used* for card memory sizing.")
            .AppendLine("- Commit is not physical RAM usage. Hard-page reads do not necessarily indicate RAM exhaustion.")
            .AppendLine("- Process logical I/O includes cached, device or network I/O; it is not physical SSD TBW.")
            .AppendLine("- Older unmeasured fields are unknown, not zero. Sampling may miss short peaks.")
            .AppendLine("- App names and session notes are potentially sensitive. Review this file before sharing externally.")
            .AppendLine()
            .AppendLine("## Prompt for your chosen LLM")
            .AppendLine("> Analyze only the evidence actually measured. Identify sustained background waste and candidate bottlenecks; distinguish measurements, reasonable hypotheses and unknowns. Explain what workload-tagged benchmark would confirm each proposed hardware or software change. Never recommend deleting a file, disabling sync or downgrading hardware without supporting task-level measurements.")
            .AppendLine()
            .AppendLine("For more precise analysis, attach the matching JSON evidence file. Workstation Lens itself sends no monitoring data to an LLM.");
        return report.ToString();
    }
    private static double Num(Dictionary<string,object?> row,string key)
        => row.TryGetValue(key,out var value) && value is not null ? Convert.ToDouble(value,CultureInfo.InvariantCulture) : 0;
    private static object Stats(IEnumerable<Dictionary<string,object?>> rows,string column,double scale=1)
    {
        var values = rows.Where(r=>r.TryGetValue(column,out var v) && v!=null)
            .Select(r=>Num(r,column)/scale).OrderBy(x=>x).ToArray();
        if (values.Length==0) return new { observations=0,avg=(double?)null,p50=(double?)null,p95=(double?)null,p99=(double?)null,max=(double?)null };
        double P(double p)=>Math.Round(values[(int)Math.Floor((values.Length-1)*p)],3);
        return new {observations=values.Length,avg=(double?)Math.Round(values.Average(),3),p50=(double?)P(.5),
            p95=(double?)P(.95),p99=(double?)P(.99),max=(double?)P(1)};
    }
    private static object Metrics(IEnumerable<Dictionary<string,object?>> source)
    {
        var rows = source.ToArray();
        return new {
            cpuPct=Stats(rows,"cpu"),hottestCorePct=Stats(rows,"core_peak"),
            gpuEnginePct=Stats(rows,"gpu"),
            ramGiB=Stats(rows,"ram",1073741824),
            gpuVramGiB=Stats(rows,"gpu_mem_used",1073741824),
            commitGiB=Stats(rows,"commit_bytes",1073741824),
            hardPageReadsPerSec=Stats(rows,"page_reads_sec"),
            pagesInputPerSec=Stats(rows,"pages_input_sec"),
            diskReadIops=Stats(rows,"disk_read_ops"),
            diskWriteIops=Stats(rows,"disk_write_ops"),
            readLatencyMsWithIo=Stats(rows.Where(r=>Num(r,"disk_read_ops")>=1),"read_latency_ms"),
            writeLatencyMsWithIo=Stats(rows.Where(r=>Num(r,"disk_write_ops")>=1),"write_latency_ms"),
            hotCoreSampleCount=rows.Sum(r=>Num(r,"hot_core_samples")),
            hotCoreObservedSamples=rows.Count(r=>r.TryGetValue("hot_core_samples",out var n)&&n!=null)
        };
    }
}
