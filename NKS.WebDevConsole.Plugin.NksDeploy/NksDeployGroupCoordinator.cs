using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK.Deploy;

namespace NKS.WebDevConsole.Plugin.NksDeploy;

/// <summary>
/// Orchestrates a fan-out deploy across N hosts of the same site
/// (Phase 6.1). Composes the existing single-host
/// <see cref="IDeployBackend"/> in parallel — does NOT re-implement the
/// nksdeploy CLI invocation.
///
/// State machine — see <see cref="DeployGroupPhase"/>:
///   Initializing → Deploying → AwaitingAllSoak → AllSucceeded | RolledBack | PartialFailure
///   Initializing → Deploying → GroupFailed (any failure pre-PONR with no other hosts past PONR)
///
/// Per-host events flow through the per-host backend's IProgress sink;
/// this coordinator wraps that sink to ALSO project them onto the group's
/// own IProgress so the GUI's group drawer sees a unified timeline.
///
/// Design choice — NOT a separate CLI invocation:
///   We deliberately reuse <see cref="IDeployBackend.StartDeployAsync"/>
///   per host instead of teaching nksdeploy a "group" verb. That keeps
///   the PHP CLI ignorant of multi-host orchestration (single
///   responsibility) and lets the daemon coordinate cancel + rollback
///   centrally — including against backends that are NOT nksdeploy
///   (LocalRsync / future Capistrano / Kamal would just plug in their
///   own IDeployBackend).
/// </summary>
public sealed class NksDeployGroupCoordinator : IDeployGroupCoordinator
{
    private readonly IDeployBackend _backend;
    private readonly IDeployGroupsRepository _groups;
    private readonly IDeployRunsRepository _runs;
    private readonly ILogger<NksDeployGroupCoordinator> _logger;

    /// <summary>
    /// In-flight group cancellation tokens. Keyed by groupId; cancel via
    /// <see cref="RollbackGroupAsync"/> indirectly (rollback signals each
    /// per-host backend, the orchestrator below sees the cascade).
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeGroups = new();

    public NksDeployGroupCoordinator(
        IDeployBackend backend,
        IDeployGroupsRepository groups,
        IDeployRunsRepository runs,
        ILogger<NksDeployGroupCoordinator> logger)
    {
        _backend = backend;
        _groups = groups;
        _runs = runs;
        _logger = logger;
    }

    public async Task<string> StartGroupAsync(
        DeployGroupRequest req,
        IProgress<DeployGroupEvent> progress,
        CancellationToken ct)
    {
        if (req.Hosts.Count == 0)
            throw new ArgumentException("Hosts list cannot be empty", nameof(req));
        // De-dupe just in case the caller didn't.
        var hosts = req.Hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var groupId = Guid.NewGuid().ToString("D");
        var startedAt = DateTimeOffset.UtcNow;
        await _groups.InsertAsync(new DeployGroupRow(
            Id: groupId,
            Domain: req.Domain,
            Hosts: hosts,
            HostDeployIds: new Dictionary<string, string>(),
            Phase: nameof(DeployGroupPhase.Initializing).ToLowerInvariant(),
            StartedAt: startedAt,
            CompletedAt: null,
            ErrorMessage: null,
            TriggeredBy: req.TriggeredBy,
            CreatedAt: startedAt,
            UpdatedAt: startedAt
        ), ct);

        var groupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeGroups[groupId] = groupCts;

        // Fire-and-forget orchestration loop — caller gets the groupId
        // immediately, observers track via progress / status endpoint.
        _ = Task.Run(() => RunGroupAsync(groupId, req.Domain, hosts, req, progress, groupCts.Token));

        EmitGroupEvent(groupId, DeployGroupPhase.Initializing, "group_started",
            $"Group fan-out started for {hosts.Count} host(s)", false, progress);
        return groupId;
    }

    public async Task<DeployGroupStatus?> GetGroupStatusAsync(string groupId, CancellationToken ct)
    {
        var row = await _groups.GetByIdAsync(groupId, ct);
        if (row is null) return null;
        return new DeployGroupStatus(
            GroupId: row.Id,
            Domain: row.Domain,
            Hosts: row.Hosts,
            Phase: ParsePhase(row.Phase),
            HostDeployIds: row.HostDeployIds,
            StartedAt: row.StartedAt,
            CompletedAt: row.CompletedAt,
            ErrorMessage: row.ErrorMessage);
    }

    public async Task RollbackGroupAsync(string groupId, CancellationToken ct)
    {
        var row = await _groups.GetByIdAsync(groupId, ct)
            ?? throw new KeyNotFoundException($"Unknown group id: {groupId}");

        await _groups.UpdatePhaseAsync(groupId,
            nameof(DeployGroupPhase.RollingBackAll).ToLowerInvariant(),
            isTerminal: false, errorMessage: null, ct);

        var rollbackTasks = row.HostDeployIds.Select(async kvp =>
        {
            try
            {
                await _backend.RollbackAsync(kvp.Value, ct);
                return (kvp.Key, ok: true, error: (string?)null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Group {GroupId} rollback failed for host {Host}", groupId, kvp.Key);
                return (kvp.Key, ok: false, error: (string?)ex.Message);
            }
        }).ToArray();

        var results = await Task.WhenAll(rollbackTasks);
        var anyFailed = results.Any(r => !r.ok);
        var terminalPhase = anyFailed
            ? nameof(DeployGroupPhase.PartialFailure).ToLowerInvariant()
            : nameof(DeployGroupPhase.RolledBack).ToLowerInvariant();
        var msg = anyFailed
            ? "rollback partial failure: " + string.Join("; ",
                  results.Where(r => !r.ok).Select(r => $"{r.Key}: {r.error}"))
            : null;
        await _groups.UpdatePhaseAsync(groupId, terminalPhase,
            isTerminal: true, errorMessage: msg, ct);
    }

    /// <summary>
    /// The actual fan-out loop. Runs detached from the caller so the REST
    /// handler can return 202 immediately. All exceptions are caught and
    /// projected onto the group's terminal state — uncaught throws here
    /// would leave the group row dangling in 'deploying' forever.
    /// </summary>
    private async Task RunGroupAsync(
        string groupId,
        string domain,
        IReadOnlyList<string> hosts,
        DeployGroupRequest req,
        IProgress<DeployGroupEvent> progress,
        CancellationToken ct)
    {
        try
        {
            await _groups.UpdatePhaseAsync(groupId,
                nameof(DeployGroupPhase.Deploying).ToLowerInvariant(),
                isTerminal: false, errorMessage: null, ct);
            EmitGroupEvent(groupId, DeployGroupPhase.Deploying, "fan_out",
                "Starting per-host deploys in parallel", false, progress);

            // Per-host deploys — each one's IProgress is wrapped so events
            // also project to the group's IProgress sink with host context.
            var hostTasks = hosts.Select(async host =>
            {
                var perHostProgress = new Progress<DeployEvent>(evt =>
                {
                    progress.Report(new DeployGroupEvent(
                        GroupId: groupId,
                        DeployId: evt.DeployId,
                        Host: host,
                        Phase: MapHostPhaseToGroup(evt.Phase),
                        Step: evt.Step,
                        Message: evt.Message,
                        Timestamp: evt.Timestamp,
                        IsTerminal: false));
                });

                try
                {
                    var hostReq = new DeployRequest(
                        Domain: domain,
                        Host: host,
                        IdempotencyKey: $"{req.IdempotencyKey}::{host}",
                        TriggeredBy: req.TriggeredBy,
                        BackendOptions: req.BackendOptions,
                        // Phase 6.2 — fan out the snapshot opt-in. Each
                        // host snapshots its own DB; the coordinator does
                        // NOT assume hosts share a database.
                        Snapshot: req.Snapshot);
                    var deployId = await _backend.StartDeployAsync(hostReq, perHostProgress, ct);
                    await _groups.RecordHostDeployAsync(groupId, host, deployId, ct);
                    var status = await _backend.GetStatusAsync(deployId, ct);
                    return (host, deployId, success: status.Success, error: status.ErrorMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Group {GroupId} host {Host} deploy threw", groupId, host);
                    return (host, deployId: (string?)null, success: false, error: ex.Message);
                }
            }).ToArray();

            var results = await Task.WhenAll(hostTasks);
            var failed = results.Where(r => !r.success).ToList();
            var succeeded = results.Where(r => r.success).ToList();

            if (failed.Count == 0)
            {
                await _groups.UpdatePhaseAsync(groupId,
                    nameof(DeployGroupPhase.AllSucceeded).ToLowerInvariant(),
                    isTerminal: true, errorMessage: null, ct);
                EmitGroupEvent(groupId, DeployGroupPhase.AllSucceeded, "group_complete",
                    $"All {hosts.Count} host(s) succeeded", true, progress);
                return;
            }

            // Some hosts failed. Determine if any succeeded ones are past PONR
            // (committed releases that must be rolled back to keep the group
            // atomic). Any committed-but-failed host stays in its failed state
            // — we cannot un-fail a deploy that crashed mid-way.
            var rollbackTargets = new List<(string Host, string DeployId)>();
            foreach (var s in succeeded)
            {
                if (s.deployId is null) continue;
                rollbackTargets.Add((s.host, s.deployId));
            }

            if (rollbackTargets.Count == 0)
            {
                // No committed releases anywhere — clean group failure.
                await _groups.UpdatePhaseAsync(groupId,
                    nameof(DeployGroupPhase.GroupFailed).ToLowerInvariant(),
                    isTerminal: true,
                    errorMessage: "All hosts failed before any release was switched: " +
                        string.Join("; ", failed.Select(f => $"{f.host}: {f.error}")),
                    ct);
                EmitGroupEvent(groupId, DeployGroupPhase.GroupFailed, "group_failed",
                    $"All {failed.Count} host(s) failed pre-PONR", true, progress);
                return;
            }

            // Roll back the committed hosts in parallel.
            await _groups.UpdatePhaseAsync(groupId,
                nameof(DeployGroupPhase.RollingBackAll).ToLowerInvariant(),
                isTerminal: false, errorMessage: null, ct);
            EmitGroupEvent(groupId, DeployGroupPhase.RollingBackAll, "fan_out_rollback",
                $"{failed.Count} host(s) failed; rolling back {rollbackTargets.Count} committed deploy(s)",
                false, progress);

            var rollbackResults = await Task.WhenAll(rollbackTargets.Select(async t =>
            {
                try
                {
                    await _backend.RollbackAsync(t.DeployId, ct);
                    return (t.Host, ok: true, error: (string?)null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Group {GroupId} cascade rollback failed for {Host}", groupId, t.Host);
                    return (t.Host, ok: false, error: (string?)ex.Message);
                }
            }));

            var rollbackFailed = rollbackResults.Where(r => !r.ok).ToList();
            var terminal = rollbackFailed.Count == 0
                ? nameof(DeployGroupPhase.RolledBack).ToLowerInvariant()
                : nameof(DeployGroupPhase.PartialFailure).ToLowerInvariant();
            var terminalMsg = "Original failures: " +
                string.Join("; ", failed.Select(f => $"{f.host}: {f.error}"));
            if (rollbackFailed.Count > 0)
            {
                terminalMsg += " | Rollback failures: " +
                    string.Join("; ", rollbackFailed.Select(r => $"{r.Host}: {r.error}"));
            }
            await _groups.UpdatePhaseAsync(groupId, terminal,
                isTerminal: true, errorMessage: terminalMsg, ct);
            EmitGroupEvent(groupId, ParsePhase(terminal), "group_terminal", terminalMsg, true, progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Group {GroupId} orchestrator threw", groupId);
            await _groups.UpdatePhaseAsync(groupId,
                nameof(DeployGroupPhase.GroupFailed).ToLowerInvariant(),
                isTerminal: true, errorMessage: ex.Message, ct);
        }
        finally
        {
            if (_activeGroups.TryRemove(groupId, out var cts))
            {
                try { cts.Dispose(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private void EmitGroupEvent(
        string groupId,
        DeployGroupPhase phase,
        string step,
        string message,
        bool isTerminal,
        IProgress<DeployGroupEvent> progress)
    {
        progress.Report(new DeployGroupEvent(
            GroupId: groupId,
            DeployId: null,
            Host: null,
            Phase: phase,
            Step: step,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            IsTerminal: isTerminal));
    }

    private static DeployGroupPhase MapHostPhaseToGroup(DeployPhase hostPhase) => hostPhase switch
    {
        DeployPhase.Done => DeployGroupPhase.Deploying,         // host done; group still aggregating
        DeployPhase.Failed => DeployGroupPhase.Deploying,       // host failed; group decides next
        DeployPhase.AwaitingSoak => DeployGroupPhase.AwaitingAllSoak,
        _ => DeployGroupPhase.Deploying,
    };

    private static DeployGroupPhase ParsePhase(string raw) => raw switch
    {
        "initializing" => DeployGroupPhase.Initializing,
        "preflight" => DeployGroupPhase.Preflight,
        "deploying" => DeployGroupPhase.Deploying,
        "awaiting_all_soak" => DeployGroupPhase.AwaitingAllSoak,
        "all_succeeded" => DeployGroupPhase.AllSucceeded,
        "partial_failure" => DeployGroupPhase.PartialFailure,
        "rolling_back_all" => DeployGroupPhase.RollingBackAll,
        "rolled_back" => DeployGroupPhase.RolledBack,
        "group_failed" => DeployGroupPhase.GroupFailed,
        _ => DeployGroupPhase.Initializing,
    };
}
