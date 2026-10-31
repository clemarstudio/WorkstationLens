using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WorkstationLens.App;

/// <summary>One user-triggered local bundle; streams database rows without loading all PIDs into RAM.</summary>
internal static class ExportBundleService
{
    private static readonly SemaphoreSlim ExportGate = new(1, 1);
    private static readonly string[] Columns =
    [
        "record_type", "time_utc", "time_unix", "application", "pid", "active_minutes", "samples",
        "cpu_avg_pct", "cpu_peak_pct", "hottest_core_pct", "gpu_engine_avg_pct", "gpu_engine_peak_pct",
        "system_ram_used_gib", "system_ram_total_gib", "app_ram_used_gib", "app_ram_peak_gib",
        "adapter_vram_used_gib", "adapter_vram_total_gib", "attributed_gpu_memory_gib",
        "committed_gib", "commit_limit_gib", "hard_page_reads_per_sec",
        "disk_read_latency_ms", "disk_write_latency_ms", "disk_total_bytes_per_sec",
        "process_read_bytes_per_sec", "process_write_bytes_per_sec"
    ];

    // Columns have stable definitions for all record types. The type column
    // identifies machine-minute, quarter-hour application, and PID-minute rows.
    // Unavailable fields are empty, never silently fabricated as zero.
    private const string MachineSql = """
        SELECT 'machine_minute' AS record_type,strftime('%Y-%m-%dT%H:%M:%SZ',time,'unixepoch') AS time_utc,
            time AS time_unix,NULL AS application,NULL AS pid,NULL AS active_minutes,samples,
            cpu,cpu_peak,core_peak,gpu,gpu_peak,
            ram/1073741824.0,ram_total/1073741824.0,NULL,NULL,
            CASE WHEN time < 1793275260 AND gpu_mem_used=0 THEN NULL ELSE gpu_mem_used/1073741824.0 END,
            gpu_mem_total/1073741824.0,NULL,
            commit_bytes/1073741824.0,commit_limit/1073741824.0,page_reads_sec,
            read_latency_ms,write_latency_ms,disk_bps,NULL,NULL
        FROM machine_minute WHERE time >= $since AND time < $until ORDER BY time
        """;
    private const string AppSql = """
        SELECT 'application_quarter' AS record_type,strftime('%Y-%m-%dT%H:%M:%SZ',time,'unixepoch') AS time_utc,
            time AS time_unix,name AS application,NULL AS pid,minutes AS active_minutes,NULL AS samples,
            cpu_sum/NULLIF(minutes,0),cpu_peak,NULL,gpu_sum/NULLIF(minutes,0),gpu_peak,
            NULL,NULL,working_sum/1073741824.0/NULLIF(minutes,0),
            working_peak/1073741824.0,NULL,NULL,
            dedicated_sum/1073741824.0/NULLIF(minutes,0),NULL,NULL,NULL,NULL,NULL,NULL,
            read_bps_sum/NULLIF(minutes,0),write_bps_sum/NULLIF(minutes,0)
        FROM app_quarter WHERE time >= $since AND time < $until ORDER BY time,name
        """;
    private const string ProcessSql = """
        SELECT 'process_minute' AS record_type,strftime('%Y-%m-%dT%H:%M:%SZ',time,'unixepoch') AS time_utc,
            time AS time_unix,name AS application,pid,NULL AS active_minutes,samples,
            cpu,cpu_peak,NULL,gpu,gpu_peak,
            NULL,NULL,working/1073741824.0,working_peak/1073741824.0,NULL,NULL,
            dedicated/1073741824.0,NULL,NULL,NULL,NULL,NULL,NULL,read_bps,write_bps
        FROM process_minute WHERE time >= $since AND time < $until ORDER BY time,pid
        """;

    public static async Task<IResult> DownloadAsync(TimeWindow window, MetricsStore store, EvidenceService evidence,
        CancellationToken ct)
    {
        await ExportGate.WaitAsync(ct);
        string? file = null;
        try
        {
            file = Path.Combine(Path.GetTempPath(), $"workstation-lens-{Guid.NewGuid():N}.zip");
            using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64*1024, FileOptions.SequentialScan))
            using (var zip = new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:false))
            {
                var entry = zip.CreateEntry("workstation-lens-records.csv",CompressionLevel.Fastest);
                using (var csv = new StreamWriter(entry.Open(),new UTF8Encoding(true),64*1024))
                    WriteCsv(csv, store.Path,window,ct);

                var json = JsonSerializer.Serialize(evidence.Build(window),
                    new JsonSerializerOptions { WriteIndented = true });
                WriteEntry(zip,"evidence.json",json);
                WriteEntry(zip,"report.md",evidence.BuildMarkdown(window));
                WriteEntry(zip,"README.txt",
                    "Workstation Lens — Export Guide\n" +
                    $"Period (UTC): {DateTimeOffset.FromUnixTimeSeconds(window.Start):yyyy-MM-dd HH:mm} to {DateTimeOffset.FromUnixTimeSeconds(window.End):yyyy-MM-dd HH:mm}\n\n" +
                    "START HERE\n" +
                    "1. report.md — Read this first: usage summary, high values and limitations.\n" +
                    "   'Valid minutes' counts measured data; P95/P99 show high-use levels.\n" +
                    "2. evidence.json — Structured summary for an LLM or analysis tool.\n" +
                    "3. workstation-lens-records.csv — Detailed data for Excel or scripts (UTF-8 CSV).\n\n" +
                    "CSV QUICK GUIDE\n" +
                    "Filter the record_type column:\n" +
                    "  machine_minute      Whole-PC reading, grouped by minute.\n" +
                    "  application_quarter Per-app summary, grouped in 15-minute intervals.\n" +
                    "  process_minute      Individual process (PID), grouped by minute.\n" +
                    "time_utc is UTC; time_unix is seconds since 1970 UTC.\n" +
                    "_pct = percent; _gib = GiB; _ms = milliseconds; _bytes_per_sec = bytes/second.\n" +
                    "Blank cells mean unavailable or not applicable, NOT zero.\n\n" +
                    "GOOD TO KNOW\n" +
                    "The period ends before the shown end time. Gaps do not mean the PC was idle.\n" +
                    "Application intervals at the edges may include minutes outside the period.\n" +
                    "System/app history lasts up to 90 days; PID details up to 14 days.\n" +
                    "Per-process GPU memory is attributed, not total physical VRAM.\n" +
                    "Process I/O can include cached or network traffic, not just disk activity.\n" +
                    "Review application names and workload notes before sharing with a cloud LLM.\n" +
                    "Workstation Lens never uploads this bundle automatically.\n");
            }
            var resultStream = new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,
                64*1024,FileOptions.DeleteOnClose|FileOptions.SequentialScan);
            file = null; // FileStream owns deletion after the HTTP response completes.
            string downloadName = $"workstation-lens-{DateTime.UtcNow:yyyyMMdd}-evidence.zip";
            return Results.File(resultStream,"application/zip",downloadName);
        }
        finally
        {
            if (file is not null) try { File.Delete(file); } catch (IOException) { }
            ExportGate.Release();
        }
    }

    private static void WriteEntry(ZipArchive archive,string name,string content)
    {
        var entry = archive.CreateEntry(name,CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(),new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteCsv(StreamWriter writer,string dbPath,TimeWindow window,CancellationToken ct)
    {
        writer.WriteLine(string.Join(",",Columns));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {DataSource=dbPath,Mode=SqliteOpenMode.ReadOnly,Cache=SqliteCacheMode.Shared}.ToString());
        db.Open();
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        WriteRows(db,writer,MachineSql,window.Start,window.End,ct);
        WriteRows(db,writer,AppSql,window.Start,window.End,ct);
        // All requested records, but PID detail cannot extend beyond 14 days.
        WriteRows(db,writer,ProcessSql,Math.Max(window.Start,now-14*86400L),window.End,ct);
    }
    private static void WriteRows(SqliteConnection db,StreamWriter writer,string sql,long since,long until,CancellationToken ct)
    {
        using var cmd=db.CreateCommand();
        cmd.CommandText=sql;cmd.Parameters.AddWithValue("$since",since);
        cmd.Parameters.AddWithValue("$until",until);
        using var rows=cmd.ExecuteReader();
        if (rows.FieldCount != Columns.Length)
            throw new InvalidOperationException("Unified CSV schema has an unexpected number of columns.");
        while(rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            for (int i=0;i<rows.FieldCount;i++)
            {
                if (i>0) writer.Write(',');
                if (rows.IsDBNull(i)) continue;
                string cell=Convert.ToString(rows.GetValue(i),CultureInfo.InvariantCulture)??"";
                // Prevent Excel formula injection from process names.
                if ((i==3) && cell.Length>0 && "=+-@".Contains(cell[0])) cell="'"+cell;
                if(cell.IndexOfAny([',','"','\r','\n'])>=0) cell='"'+cell.Replace("\"","\"\"")+'"';
                writer.Write(cell);
            }
            writer.WriteLine();
        }
    }
}
