using System.Text;
using System.Diagnostics;
using Clemar.UpdateKit;
using Velopack;
using WorkstationLens.App;

// Velopack handles installer callbacks and applies already staged updates on
// the NEXT launch, before collector/database code runs. Development builds
// are not installed and never download or execute a release.
VelopackApp.Build().Run();
var builder = WebApplication.CreateBuilder(args);
var chosenPort = int.TryParse(Environment.GetEnvironmentVariable("WORKSTATION_LENS_PORT"), out int requestedPort)
    && requestedPort is >= 1024 and <= 65535 ? requestedPort : 18765;
bool background = args.Any(arg => string.Equals(arg,"--background",StringComparison.OrdinalIgnoreCase));
string dashboardUrl = $"http://127.0.0.1:{chosenPort}/";
if (!background)
{
    // Windows Start Menu shortcut launches this same EXE. When the collector
    // is already listening, open the running instance instead of racing
    // a second collector/database writer or failing to bind the same port.
    try
    {
        using var probe = new HttpClient{ Timeout = TimeSpan.FromMilliseconds(750) };
        // Keep the generated entry point synchronous: Velopack verifies that
        // its lifecycle handler is the FIRST code in Main, and a top-level
        // await would transform Main into an async MoveNext() state machine.
        using var response = probe.GetAsync(dashboardUrl+"api/status").GetAwaiter().GetResult();
        if (response.IsSuccessStatusCode)
        {
            OpenDashboard(dashboardUrl);
            return;
        }
    }
    catch (HttpRequestException) { }
    catch (TaskCanceledException) { }
}
builder.WebHost.UseUrls($"http://127.0.0.1:{chosenPort}");
builder.Services.AddSingleton<MetricsStore>();
builder.Services.AddSingleton<EvidenceService>();
builder.Services.AddSingleton<IUpdateEngine>(
    _ => VelopackEngine.ForGithub("https://github.com/clemarstudio/WorkstationLens"));
builder.Services.AddSingleton<UpdateCoordinator>();
builder.Services.AddHostedService<UpdateBackgroundService>();
builder.Services.AddSingleton<CollectorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorService>());
builder.Services.AddHostedService(sp => new TrayIconService(sp.GetRequiredService<IHostApplicationLifetime>(),dashboardUrl));
var app = builder.Build();
if (!background)
{
    app.Lifetime.ApplicationStarted.Register(() =>
        _ = Task.Run(async () =>
        {
            // Allow the HTTP listener to be ready before opening the browser.
            await Task.Delay(650);
            OpenDashboard(dashboardUrl);
        }));
}
// Local-only endpoints must also reject DNS-rebinding Host headers. Binding
// only to 127.0.0.1 is not sufficient when a browser visits an attacker domain
// whose DNS answer points to the user's loopback address.
app.Use(async (context,next) =>
{
    var host = context.Request.Host.Host;
    if (!string.Equals(host,"127.0.0.1",StringComparison.OrdinalIgnoreCase)
        && !string.Equals(host,"localhost",StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode=400;
        await context.Response.WriteAsync("Loopback host required.");
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
app.Use(async (context,next) =>
{
    // A packaged updater must never serve a new index.html with stale
    // app.js from the browser cache after an in-place version change.
    if (context.Request.Path == "/" || context.Request.Path == "/index.html" ||
        context.Request.Path == "/app.js")
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseStaticFiles();
app.MapGet("/assets/clemar-update-banner.js",
    () => Results.Text(UpdateBannerScript.Content,"text/javascript; charset=utf-8"));
app.MapGet("/api/update", (UpdateCoordinator updates) => Results.Ok(updates.Status));
app.MapPost("/api/update/check", (HttpRequest request, UpdateCoordinator updates) =>
{
    if (!MayControlUpdates(request)) return Results.Unauthorized();
    if (!updates.Status.CanAutoUpdate) return Results.Conflict(new {error="Updates require an installed release."});
    _ = Task.Run(() => updates.CheckAndStageAsync());
    return Results.Accepted();
});
app.MapPost("/api/update/install", (HttpRequest request,UpdateCoordinator updates,IHostApplicationLifetime lifetime) =>
{
    if (!MayControlUpdates(request)) return Results.Unauthorized();
    if (!updates.BeginInstallAfterExit()) return Results.Conflict(new {error="No staged update ready to apply."});
    // Respond to the browser first. Then let CollectorService gracefully
    // persist the in-flight minute; Velopack waits for this process to exit.
    _ = Task.Run(async () => { await Task.Delay(400); lifetime.StopApplication(); });
    return Results.Ok(new {restarting=true});
});

app.MapGet("/api/status", (CollectorService collector, MetricsStore store) =>
{
    var current = collector.Latest;
    return Results.Ok(new
    {
        startedUtc = collector.Started, nowUtc = DateTime.UtcNow, collector.Samples,
        collector.StoredMinutes, collector.LastError, collector.GpuStatus,
        databaseMinutes = store.Query("SELECT COUNT(*) AS minutes FROM machine_minute")[0]["minutes"],
        collector.EnumeratedProcesses, collector.SkippedProcesses,
        collector.LastSampleMs,
        database = store.Path, intervalSeconds = 15, current?.Machine,
        top = current?.Processes.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.WorkingBytes).Take(20)
    });
});
// Small polling endpoint: no SQLite reads, no process enumeration, just
// new immutable machine samples since the browser's last received timestamp.
// The in-memory ring survives browser refreshes; minute history survives
// application restarts in SQLite.
app.MapGet("/api/live", (long? after, CollectorService collector) =>
    Results.Ok(new
    {
        machines = collector.RecentMachine(after.GetValueOrDefault()),
        collector.StoredMinutes,
        collector.LastError,
        collector.GpuStatus,
        intervalSeconds = 15
    }));

app.MapGet("/api/history", (int? hours, long? from, long? to, MetricsStore store) =>
{
    if (!TimeWindow.TryCreate(hours,from,to,out var window,out var error))
        return Results.BadRequest(new {error});
    double h = window.RequestedHours;
    long step = h <= 48 ? 60 : h <= 336 ? 900 : h <= 720 ? 3600 : 21600;
    var rows = store.Query("""
        SELECT time / $step * $step AS time, ROUND(AVG(cpu),3) AS cpu,
        MAX(cpu_peak) AS cpuPeak, MAX(core_peak) AS corePeak,
        ROUND(AVG(gpu),3) AS gpu, MAX(gpu_peak) AS gpuPeak,
        ROUND(AVG(gpu_mem_used),0) AS vramUsed, MAX(gpu_mem_total) AS vramTotal,
        ROUND(AVG(ram),0) AS ram, MAX(ram_total) AS ramTotal,
        ROUND(AVG(commit_bytes),0) AS commitBytes,MAX(commit_limit) AS commitLimit,
        ROUND(AVG(page_reads_sec),3) AS pageReads,MAX(page_reads_peak) AS pageReadsPeak,
        ROUND(AVG(pages_input_sec),3) AS pagesInput,
        ROUND(AVG(read_latency_ms),3) AS readLatency,MAX(read_latency_peak) AS readLatencyPeak,
        ROUND(AVG(write_latency_ms),3) AS writeLatency,MAX(write_latency_peak) AS writeLatencyPeak,
        ROUND(AVG(disk_read_ops),3) AS diskReads,ROUND(AVG(disk_write_ops),3) AS diskWrites,
        SUM(hot_core_samples) AS hotCoreSamples,
        ROUND(AVG(disk_bps),0) AS disk, MAX(processes) AS processes
        FROM machine_minute WHERE time >= $since AND time < $until
        GROUP BY time / $step ORDER BY time
        """,("$step",step),("$since",window.Start),("$until",window.End));
    return Results.Ok(rows);
});
app.MapGet("/api/insights", (int? hours, long? from, long? to, MetricsStore store) =>
{
    if (!TimeWindow.TryCreate(hours,from,to,out var window,out var error))
        return Results.BadRequest(new {error});
    return Results.Ok(store.Query("""
        SELECT COUNT(*) AS recordedMinutes,
            SUM(CASE WHEN cpu>=80 THEN 1 ELSE 0 END) AS cpuHighMinutes,
            SUM(CASE WHEN gpu>=75 THEN 1 ELSE 0 END) AS gpuHighMinutes,
            SUM(CASE WHEN ram_total>0 AND ram*1.0/ram_total>=0.75 THEN 1 ELSE 0 END) AS ramHighMinutes,
            SUM(CASE WHEN gpu_mem_total>0 AND gpu_mem_used*1.0/gpu_mem_total>=0.75 THEN 1 ELSE 0 END) AS vramHighMinutes,
            SUM(CASE WHEN gpu_mem_total>0 THEN 1 ELSE 0 END) AS vramObservedMinutes,
            SUM(CASE WHEN core_peak>=90 AND NOT (samples<=2 AND cpu_peak>=99) THEN 1 ELSE 0 END) AS hotCoreMinutes,
            SUM(CASE WHEN page_reads_sec>=100 THEN 1 ELSE 0 END) AS pagingMinutes,
            SUM(CASE WHEN page_reads_sec IS NOT NULL THEN 1 ELSE 0 END) AS pagingObservedMinutes
        FROM machine_minute WHERE time >= $since AND time < $until
        """,("$since",window.Start),("$until",window.End))[0]);
});

app.MapGet("/api/apps", (int? hours, MetricsStore store) =>
{
    var h = Math.Clamp(hours ?? 24, 1, 2160);
    long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - h * 3600L;
    return Results.Ok(store.Query("""
        SELECT name, SUM(minutes) AS activeMinutes, ROUND(SUM(cpu_sum)/SUM(minutes),3) AS cpu,
        ROUND(MAX(cpu_peak),3) AS cpuPeak, ROUND(SUM(gpu_sum)/SUM(minutes),3) AS gpu,
        ROUND(MAX(gpu_peak),3) AS gpuPeak, ROUND(SUM(working_sum)/SUM(minutes),0) AS ram,
        MAX(working_peak) AS ramPeak,
        CASE WHEN SUM(dedicated_sum)/SUM(minutes) > (SELECT MAX(gpu_mem_total) FROM machine_minute)
             THEN NULL ELSE ROUND(SUM(dedicated_sum)/SUM(minutes),0) END AS vram,
        CASE WHEN MAX(dedicated_peak) > (SELECT MAX(gpu_mem_total) FROM machine_minute)
             THEN NULL ELSE MAX(dedicated_peak) END AS vramPeak,
        ROUND(SUM(cpu_sum),2) AS cpuMinuteScore,
        ROUND(SUM(read_bps_sum)/SUM(minutes),0) AS readBps,
        ROUND(SUM(write_bps_sum)/SUM(minutes),0) AS writeBps
        FROM app_quarter WHERE time >= $since GROUP BY name
        ORDER BY cpuMinuteScore DESC LIMIT 150
        """,("$since",since)));
});

app.MapGet("/api/processes", (string? appName, int? hours, MetricsStore store) =>
{
    if (string.IsNullOrWhiteSpace(appName)) return Results.BadRequest("An application name is required.");
    long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Math.Clamp(hours ?? 24,1,336) * 3600L;
    return Results.Ok(store.Query("""
        SELECT pid,started,name,COUNT(*) AS activeMinutes,ROUND(AVG(cpu),3) AS cpu,
        MAX(cpu_peak) AS cpuPeak,ROUND(AVG(working),0) AS ram,MAX(working_peak) AS ramPeak,
        ROUND(AVG(gpu),3) AS gpu,MAX(gpu_peak) AS gpuPeak,
        CASE WHEN AVG(dedicated) > (SELECT MAX(gpu_mem_total) FROM machine_minute)
             THEN NULL ELSE ROUND(AVG(dedicated),0) END AS vram,
        CASE WHEN MAX(dedicated_peak) > (SELECT MAX(gpu_mem_total) FROM machine_minute)
             THEN NULL ELSE MAX(dedicated_peak) END AS vramPeak,
        ROUND(AVG(read_bps),0) AS readBps,ROUND(AVG(write_bps),0) AS writeBps
        FROM process_minute WHERE name=$name AND time >= $since GROUP BY pid,started,name
        ORDER BY SUM(cpu) DESC LIMIT 200
        """,("$name",appName),("$since",since)));
});

app.MapGet("/api/app-history", (string? appName, int? hours, MetricsStore store) =>
{
    if (string.IsNullOrWhiteSpace(appName)) return Results.BadRequest("An application name is required.");
    long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Math.Clamp(hours ?? 24,1,2160) * 3600L;
    return Results.Ok(store.Query("""
        SELECT time,ROUND(cpu_sum/minutes,3) AS cpu,cpu_peak AS cpuPeak,
        ROUND(gpu_sum/minutes,3) AS gpu,gpu_peak AS gpuPeak,
        ROUND(working_sum/minutes,0) AS ram,working_peak AS ramPeak,
        ROUND(dedicated_sum/minutes,0) AS vram,dedicated_peak AS vramPeak
        FROM app_quarter WHERE name=$name AND time >= $since ORDER BY time
        """,("$name",appName),("$since",since)));
});

app.MapGet("/api/export", (string? kind, string? appName, int? hours, MetricsStore store) =>
{
    long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Math.Clamp(hours ?? 24,1,2160) * 3600L;
    List<Dictionary<string, object?>> rows;
    switch (kind)
    {
        case "processes" when !string.IsNullOrWhiteSpace(appName):
            rows = store.Query("SELECT * FROM process_minute WHERE name=$name AND time>=$since ORDER BY time", ("$name",appName),("$since",since));
            break;
        case "apps":
            rows = store.Query("SELECT * FROM app_quarter WHERE time>=$since ORDER BY time",("$since",since));
            break;
        default:
            rows = store.Query("SELECT * FROM machine_minute WHERE time>=$since ORDER BY time",("$since",since));
            break;
    }
    var output = new StringBuilder();
    if (rows.Count > 0)
    {
        var columns = rows[0].Keys.ToArray();
        output.AppendLine(string.Join(",",columns));
        foreach (var row in rows)
            output.AppendLine(string.Join(",",columns.Select(c => Csv(row[c]))));
    }
    return Results.File(Encoding.UTF8.GetBytes(output.ToString()),"text/csv; charset=utf-8",$"workstation-lens-{kind ?? "system"}.csv");
});

app.MapGet("/api/labels", (EvidenceService evidence) => Results.Ok(evidence.Labels()));
app.MapPost("/api/labels", (HttpRequest request, WorkloadLabelInput body, EvidenceService evidence) =>
{
    if (request.Headers["X-Lens-Write"] != "1") return Results.Unauthorized();
    try { return Results.Ok(evidence.Add(body)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error=ex.Message }); }
});
app.MapDelete("/api/labels/{id:long}", (long id, HttpRequest request, EvidenceService evidence) =>
{
    if (request.Headers["X-Lens-Write"] != "1") return Results.Unauthorized();
    evidence.Delete(id);
    return Results.Ok(new { deleted=id });
});
app.MapGet("/api/evidence", (int? hours, EvidenceService evidence) => Results.Ok(evidence.Build(hours??168)));
app.MapGet("/api/evidence/download", (int? hours, EvidenceService evidence) =>
{
    var json=System.Text.Json.JsonSerializer.Serialize(evidence.Build(hours??168),
        new System.Text.Json.JsonSerializerOptions { WriteIndented=true });
    return Results.File(Encoding.UTF8.GetBytes(json),"application/json","workstation-lens-evidence.json");
});
app.MapGet("/api/evidence/report.md", (int? hours, EvidenceService evidence) =>
    Results.File(Encoding.UTF8.GetBytes(evidence.BuildMarkdown(hours??168)),
        "text/markdown; charset=utf-8","workstation-lens-report.md"));
app.MapGet("/api/export/bundle", (int? hours, long? from, long? to, MetricsStore store,
    EvidenceService evidence, CancellationToken cancellation) =>
{
    if (!TimeWindow.TryCreate(hours,from,to,out var window,out var error))
        return Task.FromResult<IResult>(Results.BadRequest(new {error}));
    return ExportBundleService.DownloadAsync(window,store,evidence,cancellation);
});

// Used by the local Stop-Lens.ps1 helper. A custom header prevents cross-site HTML forms
// from triggering shutdown, while the loopback binding prevents network connections.
app.MapPost("/api/control/stop", (HttpRequest request, IHostApplicationLifetime lifetime) =>
{
    if (request.Headers["X-Lens-Control"] != "stop") return Results.Unauthorized();
    _ = Task.Run(async () => { await Task.Delay(150); lifetime.StopApplication(); });
    return Results.Ok(new { stopping = true });
});

app.Run();

static string Csv(object? obj)
{
    var value = Convert.ToString(obj,System.Globalization.CultureInfo.InvariantCulture) ?? "";
    return value.IndexOfAny([',','"','\r','\n']) >= 0 ? "\"" + value.Replace("\"","\"\"") + "\"" : value;
}
static bool MayControlUpdates(HttpRequest request)
{
    if (request.Headers["X-Clemar-Update"] != "1") return false;
    if (!string.Equals(request.Host.Host,"127.0.0.1",StringComparison.OrdinalIgnoreCase)) return false;
    var origin = request.Headers.Origin.ToString();
    return string.IsNullOrEmpty(origin) ||
        string.Equals(origin,$"http://{request.Host.Value}",StringComparison.OrdinalIgnoreCase);
}
static void OpenDashboard(string url)
{
    try { Process.Start(new ProcessStartInfo(url){UseShellExecute=true}); }
    catch { /* Collector must continue even on headless installations. */ }
}
