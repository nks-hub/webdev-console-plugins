using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK.Deploy;

namespace NKS.WebDevConsole.Plugin.LocalRsync;

/// <summary>
/// Minimal IDeployBackend that publishes the site directory to a local
/// filesystem path via rsync. Intentionally narrow — exists to prove that
/// the IDeployBackend contract isn't accidentally tied to nksdeploy's
/// release-directory + symlink-switch model. A future Capistrano / Kamal
/// backend would slot in the same way.
///
/// Activation: site's deploy.neon must contain a top-level
/// <c>backend: localrsync</c> key AND a <c>localrsync.target</c> path. The
/// CanDeploy probe parses just enough NEON (line-grep — no schema) to
/// detect those keys; full parsing happens at deploy time.
///
/// Mechanism: <c>rsync -av --delete {siteRoot}/ {target}/</c>. No release
/// directories, no atomic switch — the target is updated in-place. There
/// is no rollback story (rsync doesn't keep a previous copy). RollbackAsync
/// returns NotSupportedException, exercising the "backend doesn't support
/// every operation" case the wdc UI must handle gracefully.
/// </summary>
public sealed class LocalRsyncBackend : IDeployBackend
{
    private readonly ISiteRegistry _siteRegistry;
    private readonly IDeployRunsRepository _runs;
    private readonly ILogger<LocalRsyncBackend> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public LocalRsyncBackend(
        ISiteRegistry siteRegistry,
        IDeployRunsRepository runs,
        ILogger<LocalRsyncBackend> logger)
    {
        _siteRegistry = siteRegistry;
        _runs = runs;
        _logger = logger;
    }

    public string BackendId => "local-rsync";

    public bool CanDeploy(string domain)
    {
        var site = _siteRegistry.GetSite(domain);
        if (site is null) return false;

        var configPath = Path.Combine(site.DocumentRoot, "deploy.neon");
        if (!File.Exists(configPath))
        {
            // Try parent of DocumentRoot too (Nette projects have www/ inside src tree).
            configPath = Path.Combine(Directory.GetParent(site.DocumentRoot)?.FullName ?? "", "deploy.neon");
            if (!File.Exists(configPath)) return false;
        }

        // Cheap line-grep probe — full NEON parsing happens during deploy.
        // This is intentionally fragile for activation (typos in deploy.neon
        // mean we just don't claim the deploy, falling through to nks-deploy).
        try
        {
            var lines = File.ReadAllLines(configPath);
            return lines.Any(l => l.TrimStart().StartsWith("backend:") && l.Contains("localrsync"));
        }
        catch { return false; }
    }

    public async Task<string> StartDeployAsync(
        DeployRequest request,
        IProgress<DeployEvent> progress,
        CancellationToken ct)
    {
        var site = _siteRegistry.GetSite(request.Domain)
            ?? throw new InvalidOperationException($"Unknown site: {request.Domain}");

        var siteRoot = ResolveSiteRoot(site.DocumentRoot);
        var target = ParseTargetOption(request.BackendOptions)
            ?? throw new InvalidOperationException("localrsync requires `target` in backendOptions");

        var deployId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;
        await _runs.InsertAsync(new DeployRunRow(
            Id: deployId,
            Domain: request.Domain,
            Host: request.Host,
            ReleaseId: null,
            Branch: null,
            CommitSha: null,
            Status: "running",
            IsPastPonr: false,
            StartedAt: startedAt,
            CompletedAt: null,
            ExitCode: null,
            ErrorMessage: null,
            DurationMs: null,
            TriggeredBy: request.TriggeredBy,
            BackendId: BackendId,
            CreatedAt: startedAt,
            UpdatedAt: startedAt
        ), ct);

        var deployCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active[deployId] = deployCts;

        var sw = Stopwatch.StartNew();
        var success = false;
        int? exitCode = null;
        string? errorMessage = null;

        progress.Report(new DeployEvent(
            DeployId: deployId, Phase: DeployPhase.Building, Step: "rsync_start",
            Message: $"rsync -av {siteRoot}/ {target}/", Timestamp: DateTimeOffset.UtcNow,
            IsTerminal: false, IsPastPonr: false));

        try
        {
            // rsync trailing-slash semantics: source/ → target/ copies CONTENTS.
            // --delete removes files that no longer exist in source (the
            // "this is a publish, not an append" semantic).
            var args = new[] { "-av", "--delete", siteRoot.TrimEnd('/') + "/", target.TrimEnd('/') + "/" };
            var cmd = await Cli.Wrap("rsync")
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(deployCts.Token);

            exitCode = cmd.ExitCode;
            success = cmd.ExitCode == 0;
            if (!success)
            {
                errorMessage = $"rsync exit code {cmd.ExitCode}: {cmd.StandardError}";
            }
        }
        catch (OperationCanceledException) when (deployCts.IsCancellationRequested)
        {
            errorMessage = "deploy cancelled";
            await _runs.UpdateStatusAsync(deployId, "cancelled", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _logger.LogError(ex, "Local rsync deploy {DeployId} threw", deployId);
        }
        finally
        {
            sw.Stop();
            _active.TryRemove(deployId, out _);
            await _runs.MarkCompletedAsync(deployId, success, exitCode, errorMessage, sw.ElapsedMilliseconds, CancellationToken.None);
            deployCts.Dispose();
        }

        progress.Report(new DeployEvent(
            DeployId: deployId,
            Phase: success ? DeployPhase.Done : DeployPhase.Failed,
            Step: "deploy_complete",
            Message: errorMessage ?? "rsync completed",
            Timestamp: DateTimeOffset.UtcNow,
            IsTerminal: true,
            IsPastPonr: false));

        return deployId;
    }

    public async Task<DeployResult> GetStatusAsync(string deployId, CancellationToken ct)
    {
        var row = await _runs.GetByIdAsync(deployId, ct)
            ?? throw new KeyNotFoundException($"Unknown deploy id: {deployId}");
        return new DeployResult(
            DeployId: row.Id,
            Success: row.Status == "completed",
            ErrorMessage: row.ErrorMessage,
            StartedAt: row.StartedAt,
            CompletedAt: row.CompletedAt,
            ReleaseId: null,
            CommitSha: null,
            FinalPhase: row.Status == "completed" ? DeployPhase.Done : DeployPhase.Failed);
    }

    public async Task<IReadOnlyList<DeployHistoryEntry>> GetHistoryAsync(string domain, int limit, CancellationToken ct)
    {
        var rows = await _runs.ListForDomainAsync(domain, limit, ct);
        return rows
            .Where(r => r.BackendId == BackendId)
            .Select(r => new DeployHistoryEntry(
                DeployId: r.Id, Domain: r.Domain, Host: r.Host,
                Branch: r.Branch ?? "", FinalPhase: DeployPhase.Done,
                StartedAt: r.StartedAt, CompletedAt: r.CompletedAt,
                CommitSha: null, ReleaseId: null, Error: r.ErrorMessage))
            .ToList();
    }

    public Task RollbackAsync(string deployId, CancellationToken ct) =>
        // rsync overwrites in place — no "previous release" to roll back to.
        // Throwing NotSupportedException exercises the wdc UI path that must
        // gracefully handle backends without rollback capability.
        throw new NotSupportedException("local-rsync backend has no rollback (in-place sync, no history kept)");

    public Task CancelAsync(string deployId, CancellationToken ct)
    {
        if (_active.TryGetValue(deployId, out var cts))
        {
            cts.Cancel();
            return Task.CompletedTask;
        }
        throw new InvalidOperationException($"No active deploy with id {deployId}");
    }

    private static string ResolveSiteRoot(string documentRoot)
    {
        var parent = Directory.GetParent(documentRoot)?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, "deploy.neon"))) return parent;
        return documentRoot;
    }

    private static string? ParseTargetOption(JsonElement opts)
    {
        if (opts.ValueKind != JsonValueKind.Object) return null;
        if (!opts.TryGetProperty("target", out var t)) return null;
        return t.ValueKind == JsonValueKind.String ? t.GetString() : null;
    }
}
