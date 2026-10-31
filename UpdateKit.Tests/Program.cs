using Clemar.UpdateKit;

static void True(bool condition,string label)
{
    if (!condition) throw new Exception("FAIL: "+label);
    Console.WriteLine("PASS "+label);
}

var sourceBuild = new FakeEngine{IsInstalled=false};
var source = new UpdateCoordinator(sourceBuild);
await source.CheckAndStageAsync();
True(source.Status.Stage=="unavailable" && sourceBuild.Checks==0,"source build cannot check/install");
True(!source.BeginInstallAfterExit(),"uninstalled app cannot install");

var noRelease = new FakeEngine();
var idle = new UpdateCoordinator(noRelease);
await idle.CheckAndStageAsync();
True(idle.Status.Stage=="idle" && noRelease.Checks==1 && idle.Status.CheckedAtUtc is not null,
    "no release means quiet idle, not error");

var engine = new FakeEngine{NewVersion="1.0.1"};
var updates = new UpdateCoordinator(engine);
await updates.CheckAndStageAsync();
True(updates.Status.Stage=="ready" && updates.Status.LatestVersion=="1.0.1" &&
    updates.Status.PendingRestart && updates.Status.ProgressPercent==100,"download and stage");
True(engine.Checks==1 && engine.Downloads==1,"single download");
await updates.CheckAndStageAsync();
True(engine.Downloads==1,"staged release reused");
True(updates.BeginInstallAfterExit() && engine.Installs==1,"explicit install accepted");
True(!updates.BeginInstallAfterExit(),"double install refused");

var restart = new UpdateCoordinator(new FakeEngine{Pending=new("2.2.0")});
True(restart.Status.Stage=="ready" && restart.Status.PendingRestart,"staged update persists across application restart");

var failing = new FakeEngine{FindFailure=true};
var errors = new UpdateCoordinator(failing);
await errors.CheckAndStageAsync();
True(errors.Status.Stage=="failed" && errors.Status.Error is not null,"network failure is non-fatal");
failing.FindFailure=false;
await errors.CheckAndStageAsync();
True(errors.Status.Stage=="idle" && errors.Status.Error is null,"subsequent check recovers");

var blocked = new FakeEngine{NewVersion="3.0.0",WaitDownload=new()};
var once = new UpdateCoordinator(blocked);
var first=once.CheckAndStageAsync();
await blocked.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
await once.CheckAndStageAsync();
True(blocked.Downloads==1 && once.Status.Stage=="downloading","concurrent checks deduplicated");
blocked.WaitDownload.SetResult();
await first;
True(once.Status.Stage=="ready" && blocked.Downloads==1,"completed deferred download is ready");

foreach (var bad in new[]{"http://github.com/clemarstudio/WorkstationLens",
    "https://evil.example/clemarstudio/WorkstationLens",
    "https://github.com/clemarstudio/WorkstationLens@evil.example",
    "https://github.com/clemarstudio/WorkstationLens/../../other"})
{
    bool rejected=false;
    try{VelopackEngine.ForGithub(bad);}
    catch(ArgumentException){rejected=true;}
    True(rejected,"reject unsafe release URL: "+bad);
}

Console.WriteLine("ALL UPDATER TESTS PASSED");

sealed class FakeEngine : IUpdateEngine
{
    public bool IsInstalled {get;set;}=true;
    public string CurrentVersion => "1.0.0";
    public UpdateCandidate? Pending {get;set;}
    public string? NewVersion {get;set;}
    public bool FindFailure {get;set;}
    public TaskCompletionSource? WaitDownload {get;set;}
    public TaskCompletionSource DownloadStarted {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Checks {get;private set;}
    public int Downloads {get;private set;}
    public int Installs {get;private set;}
    public Task<UpdateCandidate?> FindAsync(CancellationToken ct)
    {
        Checks++;
        if(FindFailure)throw new HttpRequestException("Offline");
        return Task.FromResult(NewVersion is null ? null : new UpdateCandidate(NewVersion));
    }
    public async Task DownloadAsync(UpdateCandidate version, Action<int> progress, CancellationToken ct)
    {
        Downloads++;progress(42);DownloadStarted.TrySetResult();
        if(WaitDownload is not null)await WaitDownload.Task.WaitAsync(ct);
        Pending=version;progress(100);
    }
    public void ApplyAfterExit(UpdateCandidate version,bool restart)
    {
        if(!restart||Pending?.Version!=version.Version)throw new Exception("Invalid install target");
        Installs++;
    }
}
