using System.Text.RegularExpressions;
using Velopack;
using Velopack.Sources;

namespace Clemar.UpdateKit;

/// <summary>Host-independent update state, intended to be displayed by any UI.</summary>
public sealed record UpdateSnapshot(
    string Stage, string CurrentVersion, string? LatestVersion, int ProgressPercent,
    DateTimeOffset? CheckedAtUtc, string? Error, bool CanAutoUpdate, bool PendingRestart);

public sealed record UpdateCandidate(string Version);

/// <summary>Injectable transport enables fully offline tests and alternative release hosts.</summary>
public interface IUpdateEngine
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    UpdateCandidate? Pending { get; }
    Task<UpdateCandidate?> FindAsync(CancellationToken ct);
    Task DownloadAsync(UpdateCandidate candidate, Action<int> onProgress, CancellationToken ct);
    /// <summary>Launch updater to wait for a graceful exit, then install and optionally relaunch.</summary>
    void ApplyAfterExit(UpdateCandidate candidate, bool restart);
}

/// <summary>
/// Thin adapter to Velopack. The SDK owns package hashes, partial downloads,
/// versioned releases, rollback and installer handover; the UI never executes binaries.
/// </summary>
public sealed class VelopackEngine : IUpdateEngine
{
    private readonly UpdateManager _manager;
    private readonly Dictionary<string,UpdateInfo> _known = new(StringComparer.Ordinal);

    public static VelopackEngine ForGithub(string repositoryUrl, bool includePrereleases = false)
    {
        if (!Regex.IsMatch(repositoryUrl, @"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Only an exact HTTPS github.com owner/repository URL is accepted.",nameof(repositoryUrl));
        return new(new UpdateManager(new GithubSource(repositoryUrl.TrimEnd('/'), null, includePrereleases)));
    }
    public VelopackEngine(UpdateManager manager) => _manager = manager;
    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "development";
    public UpdateCandidate? Pending => _manager.UpdatePendingRestart is { } p ? new(p.Version.ToString()) : null;

    public async Task<UpdateCandidate?> FindAsync(CancellationToken ct)
    {
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
        if (info is null || info.IsDowngrade) return null;
        var version = info.TargetFullRelease.Version.ToString();
        _known[version] = info;
        return new(version);
    }
    public async Task DownloadAsync(UpdateCandidate candidate, Action<int> onProgress, CancellationToken ct)
    {
        if (!_known.TryGetValue(candidate.Version,out var info)) throw new InvalidOperationException("Unknown update version.");
        await _manager.DownloadUpdatesAsync(info,onProgress,ct);
    }
    public void ApplyAfterExit(UpdateCandidate candidate, bool restart)
    {
        var pending = _manager.UpdatePendingRestart;
        if (pending is null || pending.Version.ToString() != candidate.Version)
            throw new InvalidOperationException("No verified pending package matching the requested version.");
        _manager.WaitExitThenApplyUpdates(pending, silent:false, restart:restart);
    }
}

/// <summary>
/// Bounded, one-operation-at-a-time coordinator. No operation blocks application
/// startup and a network or download failure never stops the current version.
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly IUpdateEngine _engine;
    private readonly SemaphoreSlim _singlePass = new(1,1);
    private readonly object _gate = new();
    private UpdateSnapshot _state;
    private UpdateCandidate? _ready;
    private bool _applying;

    public UpdateCoordinator(IUpdateEngine engine)
    {
        _engine = engine;
        _ready = engine.Pending;
        _state = new(
            engine.IsInstalled ? _ready is null ? "idle" : "ready" : "unavailable",
            engine.CurrentVersion,_ready?.Version,_ready is null ? 0 : 100,
            null,null,engine.IsInstalled,_ready is not null);
    }
    public UpdateSnapshot Status { get { lock(_gate) return _state; } }
    private void Set(Func<UpdateSnapshot,UpdateSnapshot> change)
    {
        lock (_gate) _state = change(_state);
    }

    public async Task CheckAndStageAsync(CancellationToken ct = default)
    {
        if (!_engine.IsInstalled) return; // development source tree is never updatable
        if (!await _singlePass.WaitAsync(0,ct)) return; // deduplication
        try
        {
            if (_applying) return;
            Set(s=>s with { Stage="checking", Error=null, ProgressPercent=0 });
            var candidate = await _engine.FindAsync(ct);
            Set(s=>s with { CheckedAtUtc=DateTimeOffset.UtcNow });
            if (candidate is null)
            {
                var pending = _engine.Pending;
                _ready = pending;
                Set(s=>s with {Stage=pending is null ? "idle" : "ready",LatestVersion=pending?.Version,
                    ProgressPercent=pending is null?0:100,PendingRestart=pending is not null});
                return;
            }
            _ready = null;
            if (_engine.Pending?.Version == candidate.Version)
            {
                _ready = candidate;
                Set(s=>s with {Stage="ready",LatestVersion=candidate.Version,ProgressPercent=100,PendingRestart=true});
                return;
            }
            Set(s=>s with {Stage="downloading",LatestVersion=candidate.Version,ProgressPercent=0,PendingRestart=false});
            await _engine.DownloadAsync(candidate,p=>Set(s=>s with { ProgressPercent=Math.Clamp(p,0,100) }),ct);
            if (_engine.Pending?.Version != candidate.Version)
                throw new InvalidOperationException("Package download completed but was not staged for restart.");
            _ready = candidate;
            Set(s=>s with {Stage="ready",LatestVersion=candidate.Version,ProgressPercent=100,PendingRestart=true});
        }
        catch (OperationCanceledException) when(ct.IsCancellationRequested) { /* normal shutdown */ }
        catch (Exception ex)
        {
            // Expose a short error, never stack traces or local machine paths to the UI.
            Set(s=>s with {Stage="failed",Error=ex is HttpRequestException ? "Network check failed; will retry." :
                "Update check or download failed; the existing application remains usable."});
        }
        finally { _singlePass.Release(); }
    }
    /// <summary>Start the installer only after explicit user action and while ready.</summary>
    public bool BeginInstallAfterExit()
    {
        if (!_singlePass.Wait(0)) return false;
        try
        {
            if (_applying || _ready is not { } requested || _engine.Pending?.Version != requested.Version) return false;
            _engine.ApplyAfterExit(requested,restart:true);
            _applying = true;
            Set(s=>s with {Stage="installing",Error=null});
            return true;
        }
        catch
        {
            Set(s=>s with {Stage="failed",Error="Could not start the installer; no changes were made."});
            return false;
        }
        finally { _singlePass.Release(); }
    }
}
