using NKS.WebDevConsole.Plugin.SDK.Deploy;

namespace NKS.WebDevConsole.Plugin.NksDeploy;

/// <summary>
/// IDeployBackend implementation that drives the bundled nksdeploy PHP CLI
/// via CliWrap. v0.1 scaffold — concrete methods land in subsequent commits:
///
///  - StartDeployAsync: spawn `php nksdeploy.phar deploy {host} --ndjson-events
///    --format=json`, parse NDJSON event lines, forward via IProgress
///  - GetStatusAsync: SELECT from deploy_runs (in-flight) or final result envelope
///  - GetHistoryAsync: read remote /.dep/history.json over SSH (60s local cache)
///  - RollbackAsync: spawn `nksdeploy rollback {host} --yes --release {id} --format=json`
///  - CancelAsync: SIGTERM the active subprocess if pre-PONR; refuse otherwise
///
/// All methods currently throw NotImplementedException — wired through DI so
/// the daemon's DI container loads the plugin without crashing, but any caller
/// reaching the heavy methods discovers the gap immediately rather than via
/// silent no-op. Subsequent commits replace each NotImplementedException with
/// the real subprocess invocation.
/// </summary>
public sealed class NksDeployBackend : IDeployBackend
{
    public string BackendId => "nks-deploy";

    public bool CanDeploy(string domain)
    {
        // v0.1: assume any site is deployable. Real impl will check that
        // {site_root}/deploy.neon exists and the registered PHP version
        // is >= 8.3 (nksdeploy minimum).
        return true;
    }

    public Task<string> StartDeployAsync(
        DeployRequest request,
        IProgress<DeployEvent> progress,
        CancellationToken ct)
    {
        throw new NotImplementedException(
            "NksDeployBackend.StartDeployAsync — wiring to nksdeploy.phar subprocess is the next commit.");
    }

    public Task<DeployResult> GetStatusAsync(string deployId, CancellationToken ct)
    {
        throw new NotImplementedException(
            "NksDeployBackend.GetStatusAsync — reads deploy_runs SQLite row; landing alongside StartDeployAsync.");
    }

    public Task<IReadOnlyList<DeployHistoryEntry>> GetHistoryAsync(
        string domain,
        int limit,
        CancellationToken ct)
    {
        throw new NotImplementedException(
            "NksDeployBackend.GetHistoryAsync — SSH read of remote /.dep/history.json with 60s local cache.");
    }

    public Task RollbackAsync(string deployId, CancellationToken ct)
    {
        throw new NotImplementedException(
            "NksDeployBackend.RollbackAsync — invokes `nksdeploy rollback --yes --format=json`.");
    }

    public Task CancelAsync(string deployId, CancellationToken ct)
    {
        throw new NotImplementedException(
            "NksDeployBackend.CancelAsync — SIGTERM tracked subprocess + clear lock when pre-PONR.");
    }
}
