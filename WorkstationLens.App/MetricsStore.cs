using Microsoft.Data.Sqlite;

namespace WorkstationLens.App;

internal sealed class ProcessAggregate
{
    public required int Pid;
    public required long Started;
    public required string Name;
    public int Count;
    public double CpuSum, CpuPeak, GpuSum, GpuPeak;
    public double WorkingSum, PrivateSum, DedicatedSum, SharedSum;
    public double ReadBpsSum, WriteBpsSum;
    public long WorkingPeak, PrivatePeak, DedicatedPeak, SharedPeak;
    public void Add(ProcessPoint p)
    {
        Count++; CpuSum += p.Cpu; CpuPeak = Math.Max(CpuPeak, p.Cpu);
        GpuSum += p.Gpu; GpuPeak = Math.Max(GpuPeak, p.Gpu);
        WorkingSum += p.WorkingBytes; WorkingPeak = Math.Max(WorkingPeak, p.WorkingBytes);
        PrivateSum += p.PrivateBytes; PrivatePeak = Math.Max(PrivatePeak, p.PrivateBytes);
        DedicatedSum += p.DedicatedBytes; DedicatedPeak = Math.Max(DedicatedPeak, p.DedicatedBytes);
        SharedSum += p.SharedBytes; SharedPeak = Math.Max(SharedPeak, p.SharedBytes);
        ReadBpsSum += p.ReadBps; WriteBpsSum += p.WriteBps;
    }
}

internal sealed class MinuteBucket(long unixMinute)
{
    public long Time { get; } = unixMinute;
    public int Samples;
    public double CpuSum, CpuPeak, CorePeak, GpuSum, GpuPeak, RamSum, DiskSum, GpuMemorySum;
    public long RamTotal, GpuMemoryTotal;
    public double CommitSum, PageReadsSum, PageReadsPeak, PagesInputSum;
    public double ReadLatencySum, WriteLatencySum, ReadLatencyPeak, WriteLatencyPeak, DiskReadOpsSum, DiskWriteOpsSum;
    public long CommitLimit;
    public int HotCoreSamples;
    public int Processes;
    public readonly Dictionary<(int, long), ProcessAggregate> Process = new();

    public void Add(Snapshot s)
    {
        Samples++; CpuSum += s.Machine.Cpu; CpuPeak = Math.Max(CpuPeak, s.Machine.Cpu);
        CorePeak = Math.Max(CorePeak, s.Machine.BusiestCore);
        GpuSum += s.Machine.GpuEngine; GpuPeak = Math.Max(GpuPeak, s.Machine.GpuEngine);
        GpuMemorySum += s.Machine.GpuMemoryUsed; GpuMemoryTotal = s.Machine.GpuMemoryTotal;
        RamSum += s.Machine.RamUsed; RamTotal = s.Machine.RamTotal;
        DiskSum += s.Machine.DiskBytesPerSecond; Processes = s.Machine.Processes;
        CommitSum += s.Machine.CommittedBytes; CommitLimit = s.Machine.CommitLimit;
        PageReadsSum += s.Machine.PageReadsPerSec; PageReadsPeak = Math.Max(PageReadsPeak, s.Machine.PageReadsPerSec);
        PagesInputSum += s.Machine.PagesInputPerSec;
        ReadLatencySum += s.Machine.DiskReadLatencyMs; WriteLatencySum += s.Machine.DiskWriteLatencyMs;
        ReadLatencyPeak = Math.Max(ReadLatencyPeak, s.Machine.DiskReadLatencyMs);
        WriteLatencyPeak = Math.Max(WriteLatencyPeak, s.Machine.DiskWriteLatencyMs);
        DiskReadOpsSum += s.Machine.DiskReadOpsPerSec; DiskWriteOpsSum += s.Machine.DiskWriteOpsPerSec;
        if (s.Machine.BusiestCore >= 90) HotCoreSamples++;
        foreach (var p in s.Processes)
        {
            var key = (p.Pid, p.Started);
            if (!Process.TryGetValue(key, out var a))
                Process[key] = a = new ProcessAggregate { Pid = p.Pid, Started = p.Started, Name = p.Name };
            a.Add(p);
        }
    }
}

internal sealed class MetricsStore
{
    public string Path { get; }
    private readonly string _connection;

    public MetricsStore()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkstationLens");
        // Isolated, opt-in acceptance tests use their own SQLite under
        // LocalAppData, never the user's existing monitoring history.
        var testSlot = Environment.GetEnvironmentVariable("WORKSTATION_LENS_TEST_SLOT");
        if (!string.IsNullOrEmpty(testSlot))
        {
            if (testSlot.Length > 40 || !System.Text.RegularExpressions.Regex.IsMatch(testSlot, @"^[A-Za-z0-9-]+$"))
                throw new ArgumentException("Invalid Workstation Lens test slot.");
            dir = System.IO.Path.Combine(dir, "test-instances", testSlot);
        }
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "measurements.db");
        _connection = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var c = Connect();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS machine_minute (
              time INTEGER PRIMARY KEY, samples INTEGER NOT NULL, cpu REAL, cpu_peak REAL, core_peak REAL,
              gpu REAL, gpu_peak REAL, ram REAL, ram_total INTEGER, disk_bps REAL, processes INTEGER,
              gpu_mem_used REAL DEFAULT 0, gpu_mem_total INTEGER DEFAULT 0,
              commit_bytes REAL, commit_limit REAL,
              page_reads_sec REAL, page_reads_peak REAL, pages_input_sec REAL,
              read_latency_ms REAL, read_latency_peak REAL, write_latency_ms REAL, write_latency_peak REAL,
              disk_read_ops REAL, disk_write_ops REAL, hot_core_samples INTEGER);
            CREATE TABLE IF NOT EXISTS process_minute (
              time INTEGER, pid INTEGER, started INTEGER, name TEXT, samples INTEGER,
              cpu REAL, cpu_peak REAL, gpu REAL, gpu_peak REAL, working REAL, working_peak INTEGER,
              private REAL, private_peak INTEGER, dedicated REAL, dedicated_peak INTEGER,
              shared REAL, shared_peak INTEGER, read_bps REAL, write_bps REAL,
              PRIMARY KEY(time,pid,started)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_process_name_time ON process_minute(name,time);
            CREATE TABLE IF NOT EXISTS app_quarter (
              time INTEGER, name TEXT, minutes INTEGER, cpu_sum REAL, cpu_peak REAL,
              gpu_sum REAL, gpu_peak REAL, working_sum REAL, working_peak INTEGER,
              dedicated_sum REAL, dedicated_peak INTEGER, read_bps_sum REAL, write_bps_sum REAL,
              PRIMARY KEY(time,name)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_app_name_time ON app_quarter(name,time);
            """;
        cmd.ExecuteNonQuery();
        // Upgrade older locally collected pilot data without deleting observations.
        using var cols = c.CreateCommand();
        cols.CommandText = "PRAGMA table_info(machine_minute)";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cols.ExecuteReader()) while (reader.Read()) existing.Add(reader.GetString(1));
        var additional = new[] { "gpu_mem_used", "gpu_mem_total", "commit_bytes", "commit_limit",
            "page_reads_sec", "page_reads_peak", "pages_input_sec", "read_latency_ms", "read_latency_peak",
            "write_latency_ms", "write_latency_peak", "disk_read_ops", "disk_write_ops", "hot_core_samples" };
        foreach (var name in additional)
        {
            if (existing.Contains(name)) continue;
            using var alter = c.CreateCommand();
            alter.CommandText = $"ALTER TABLE machine_minute ADD COLUMN {name} REAL";
            alter.ExecuteNonQuery();
        }
        EnsureColumns(c, "process_minute", "read_bps", "write_bps");
        EnsureColumns(c, "app_quarter", "read_bps_sum", "write_bps_sum");
    }

    private static void EnsureColumns(SqliteConnection connection, string table, params string[] names)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var r = cmd.ExecuteReader()) while (r.Read()) exists.Add(r.GetString(1));
        foreach (var name in names)
        {
            if (exists.Contains(name)) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} REAL";
            alter.ExecuteNonQuery();
        }
    }

    private SqliteConnection Connect()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        return c;
    }

    public void Save(MinuteBucket b)
    {
        if (b.Samples == 0) return;
        using var c = Connect();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO machine_minute
            (time,samples,cpu,cpu_peak,core_peak,gpu,gpu_peak,ram,ram_total,disk_bps,processes,gpu_mem_used,gpu_mem_total,
             commit_bytes,commit_limit,page_reads_sec,page_reads_peak,pages_input_sec,read_latency_ms,read_latency_peak,
             write_latency_ms,write_latency_peak,disk_read_ops,disk_write_ops,hot_core_samples)
            VALUES ($t,$n,$c,$cp,$core,$g,$gp,$r,$rt,$disk,$p,$gm,$gmt,
                    $cm,$cl,$reads,$readp,$input,$rl,$rlp,$wl,$wlp,$rops,$wops,$hot)
            """;
        Set(cmd, ("$t",b.Time),("$n",b.Samples),("$c",b.CpuSum/b.Samples),
            ("$cp",b.CpuPeak),("$core",b.CorePeak),("$g",b.GpuSum/b.Samples),
            ("$gp",b.GpuPeak),("$r",b.RamSum/b.Samples),("$rt",b.RamTotal),
            ("$disk",b.DiskSum/b.Samples),("$p",b.Processes),
            ("$gm",b.GpuMemorySum/b.Samples),("$gmt",b.GpuMemoryTotal),
            ("$cm",b.CommitSum/b.Samples),("$cl",b.CommitLimit),
            ("$reads",b.PageReadsSum/b.Samples),("$readp",b.PageReadsPeak),
            ("$input",b.PagesInputSum/b.Samples),("$rl",b.ReadLatencySum/b.Samples),
            ("$rlp",b.ReadLatencyPeak),("$wl",b.WriteLatencySum/b.Samples),
            ("$wlp",b.WriteLatencyPeak),("$rops",b.DiskReadOpsSum/b.Samples),
            ("$wops",b.DiskWriteOpsSum/b.Samples),("$hot",b.HotCoreSamples));
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            INSERT OR REPLACE INTO process_minute
            (time,pid,started,name,samples,cpu,cpu_peak,gpu,gpu_peak,working,working_peak,
            private,private_peak,dedicated,dedicated_peak,shared,shared_peak,read_bps,write_bps)
            VALUES ($t,$pid,$start,$name,$n,$c,$cp,$g,$gp,$w,$wp,$pr,$prp,$d,$dp,$s,$sp,$read,$write)
            """;
        foreach (var p in b.Process.Values)
        {
            Set(cmd,("$t",b.Time),("$pid",p.Pid),("$start",p.Started),("$name",p.Name),
                ("$n",p.Count),("$c",p.CpuSum/p.Count),("$cp",p.CpuPeak),
                ("$g",p.GpuSum/p.Count),("$gp",p.GpuPeak),
                ("$w",p.WorkingSum/p.Count),("$wp",p.WorkingPeak),
                ("$pr",p.PrivateSum/p.Count),("$prp",p.PrivatePeak),
                ("$d",p.DedicatedSum/p.Count),("$dp",p.DedicatedPeak),
                ("$s",p.SharedSum/p.Count),("$sp",p.SharedPeak),
                ("$read",p.ReadBpsSum/p.Count),("$write",p.WriteBpsSum/p.Count));
            cmd.ExecuteNonQuery();
        }

        // Recompute the affected quarter from the authoritative per-minute rows.
        // A restart can flush the same minute twice. An incremental '+1 minute'
        // upsert would double count it in application history, even though
        // machine_minute and process_minute have time-based primary keys.
        long quarter = b.Time / 900 * 900;
        cmd.CommandText = "DELETE FROM app_quarter WHERE time=$q";
        Set(cmd,("$q",quarter));
        cmd.ExecuteNonQuery();
        cmd.CommandText = """
            INSERT INTO app_quarter
            (time,name,minutes,cpu_sum,cpu_peak,gpu_sum,gpu_peak,working_sum,working_peak,dedicated_sum,dedicated_peak,read_bps_sum,write_bps_sum)
            SELECT $q,name,COUNT(*),SUM(cpu),MAX(cpu_peak),SUM(gpu),MAX(gpu_peak),
                   SUM(working),MAX(working_peak),SUM(dedicated),MAX(dedicated_peak),
                   SUM(read_bps),SUM(write_bps)
            FROM (
                SELECT time,name,SUM(cpu) AS cpu,SUM(cpu_peak) AS cpu_peak,
                    MAX(gpu) AS gpu,MAX(gpu_peak) AS gpu_peak,
                    SUM(working) AS working,SUM(working_peak) AS working_peak,
                    SUM(dedicated) AS dedicated,SUM(dedicated_peak) AS dedicated_peak,
                    SUM(coalesce(read_bps,0)) AS read_bps,
                    SUM(coalesce(write_bps,0)) AS write_bps
                FROM process_minute
                WHERE time >= $q AND time < $q + 900
                GROUP BY time,name
            )
            GROUP BY name
            """;
        Set(cmd,("$q",quarter));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void Retain()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var c = Connect();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM process_minute WHERE time < $short; " +
            "DELETE FROM machine_minute WHERE time < $long; DELETE FROM app_quarter WHERE time < $long;";
        Set(cmd,("$short",now-14*86400),("$long",now-90*86400));
        cmd.ExecuteNonQuery();
    }

    public List<Dictionary<string, object?>> Query(string sql, params (string Name, object Value)[] args)
    {
        using var c = Connect();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Set(cmd,args);
        using var reader = cmd.ExecuteReader();
        var output = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i=0;i<reader.FieldCount;i++) item[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            output.Add(item);
        }
        return output;
    }

    private static void Set(SqliteCommand cmd, params (string Name, object Value)[] args)
    {
        cmd.Parameters.Clear();
        foreach (var (name,value) in args) cmd.Parameters.AddWithValue(name,value);
    }
}
