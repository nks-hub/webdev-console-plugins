using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK.Deploy;

namespace NKS.WebDevConsole.Plugin.NksDeploy;

/// <summary>
/// REST handlers for the NksDeploy plugin. Registered via the plugin's
/// <see cref="NksDeployPlugin.RegisterEndpoints"/> override which the
/// daemon's PluginLoader.WireEndpoints helper hooks into the routing
/// pipeline under <c>/api/nks.wdc.deploy/</c>. The daemon's Bearer-auth
/// middleware (matching <c>/api/*</c>) covers them automatically — no
/// per-handler auth attribute needed.
///
/// Path templates use Minimal API route syntax (<c>{domain}</c> etc.); the
/// final URLs are e.g. <c>/api/nks.wdc.deploy/sites/myapp.loc/hosts/production/deploy</c>.
/// </summary>
internal static class NksDeployRoutes
{
    /// <summary>
    /// Body for POST .../deploy. Backend-specific options ride along as a
    /// freeform JsonElement so future flags (skip-tests, branch override)
    /// don't need REST-layer changes.
    /// </summary>
    public sealed record StartDeployBody(
        string? IdempotencyKey,
        JsonElement? Options,
        DeploySnapshotOptions? Snapshot = null);

    public static void Register(EndpointRegistration r)
    {
        r.MapPost("sites/{domain}/hosts/{host}/deploy", StartDeploy);
        r.MapGet("sites/{domain}/deploys/{deployId}", GetDeploy);
        r.MapGet("sites/{domain}/history", GetHistory);
        r.MapPost("sites/{domain}/deploys/{deployId}/rollback", PostRollback);
        r.MapDelete("sites/{domain}/deploys/{deployId}", DeleteDeploy);
        // Phase 6.1 — atomic multi-host group fan-out.
        r.MapPost("sites/{domain}/groups", StartGroup);
        r.MapGet("sites/{domain}/groups/{groupId}", GetGroup);
        r.MapPost("sites/{domain}/groups/{groupId}/rollback", PostGroupRollback);
    }

    /// <summary>
    /// Body for POST .../groups. Hosts is required; per-host idempotency
    /// keys are derived as <c>"{groupKey}::{host}"</c> by the coordinator
    /// so retries within the same group dedupe correctly.
    /// </summary>
    public sealed record StartGroupBody(
        IReadOnlyList<string>? Hosts,
        string? IdempotencyKey,
        JsonElement? Options,
        DeploySnapshotOptions? Snapshot = null);

    /// <summary>
    /// Common header used by the MCP server to attach a daemon-issued,
    /// HMAC-signed intent token to a destructive call. Absent header =
    /// GUI / direct caller; the daemon's bearer-auth still applies.
    /// </summary>
    private const string IntentTokenHeader = "X-Intent-Token";

    /// <summary>
    /// CI / headless escape hatch — when set to "true" the validator
    /// pre-stamps <c>confirmed_at</c> instead of waiting for the GUI
    /// banner approval. The MCP server only sets this when the operator
    /// has explicitly opted-in via <c>MCP_DEPLOY_AUTO_APPROVE=true</c>.
    /// </summary>
    private const string AllowUnconfirmedHeader = "X-Allow-Unconfirmed";

    /// <summary>
    /// Returns "mcp" when the request carried a valid intent token, "gui"
    /// otherwise. Drives the deploy_runs.triggered_by column so the audit
    /// trail and history filter can distinguish AI-initiated deploys.
    /// </summary>
    private static string ResolveTriggeredBy(HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(IntentTokenHeader, out var v) && !string.IsNullOrWhiteSpace(v.ToString())
            ? "mcp"
            : "gui";

    /// <summary>
    /// Validate the intent token (if present) against the (domain, host, kind)
    /// the plugin is about to act on. Returns null on success; an IResult
    /// representing the rejection response otherwise. When no token is
    /// present this is a no-op pass — GUI and direct daemon calls still
    /// only need bearer auth.
    /// </summary>
    private static async Task<IResult?> CheckIntentAsync(
        HttpContext ctx,
        IDeployIntentValidator validator,
        string kind,
        string domain,
        string host,
        CancellationToken ct)
    {
        if (!ctx.Request.Headers.TryGetValue(IntentTokenHeader, out var raw)) return null;
        var token = raw.ToString();
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Headless / CI bypass for the GUI confirm banner. The MCP server
        // only attaches this header when MCP_DEPLOY_AUTO_APPROVE=true is
        // set in its environment, so we trust the bearer-authenticated
        // caller's intent here.
        var allowUnconfirmed = ctx.Request.Headers.TryGetValue(AllowUnconfirmedHeader, out var hv)
            && string.Equals(hv.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        var result = await validator.ValidateAndConsumeAsync(token, kind, domain, host, allowUnconfirmed, ct);
        if (result.Ok) return null;

        // Map the pending-confirmation reason to 425 Too Early so the MCP
        // client can show a clear "approve in GUI" hint instead of treating
        // the rejection as a permanent failure.
        if (string.Equals(result.Reason, "pending_confirmation", StringComparison.Ordinal))
        {
            return Results.Json(
                new
                {
                    error = "intent_pending_confirmation",
                    reason = result.Reason,
                    hint = "User must approve the deploy in the wdc GUI banner. Re-issue this call once approved.",
                },
                statusCode: 425); // Too Early — RFC 8470, not in StatusCodes constants
        }
        return Results.Json(
            new { error = "intent_invalid", reason = result.Reason },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> StartDeploy(
        string domain,
        string host,
        StartDeployBody? body,
        IDeployBackend backend,
        IDeployEventBroadcaster broadcaster,
        IDeployIntentValidator intentValidator,
        ILoggerFactory loggerFactory,
        HttpContext ctx)
    {
        if (!backend.CanDeploy(domain))
        {
            return Results.NotFound(new { error = "site_not_deployable", domain });
        }

        var rejection = await CheckIntentAsync(ctx, intentValidator, "deploy", domain, host, ctx.RequestAborted);
        if (rejection is not null) return rejection;

        var idempotencyKey = body?.IdempotencyKey ?? Guid.NewGuid().ToString();
        var optsElement = body?.Options ?? JsonDocument.Parse("{}").RootElement;
        var request = new DeployRequest(
            Domain: domain,
            Host: host,
            IdempotencyKey: idempotencyKey,
            TriggeredBy: ResolveTriggeredBy(ctx),
            BackendOptions: optsElement,
            Snapshot: body?.Snapshot);

        // IProgress that fans out to SSE so the wdc UI's deploy drawer
        // updates in real time. The caller is fire-and-forget — we return
        // 202 immediately with the deployId, then the background task runs
        // through to the terminal event.
        var logger = loggerFactory.CreateLogger("NksDeploy.Routes");
        var progress = new Progress<DeployEvent>(evt =>
        {
            // Forward each event onto the daemon's SSE bus. Errors here are
            // swallowed-and-logged: a failing SSE broadcast must NOT abort
            // the deploy itself.
            _ = broadcaster.BroadcastAsync("deploy:event", evt)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) logger.LogWarning(t.Exception, "SSE broadcast for deploy event failed");
                }, TaskScheduler.Default);
        });

        // Pre-mint the deployId so we can return it RIGHT NOW; the background
        // task does the real work. Use the request lifetime CT — once the
        // daemon shuts down, in-flight deploys cancel cleanly.
        var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var deployId = await backend.StartDeployAsync(request, progress, ctx.RequestAborted);
                responseTcs.TrySetResult(deployId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background deploy failed for {Domain}/{Host}", domain, host);
                responseTcs.TrySetException(ex);
            }
        });

        // We don't await responseTcs.Task — the deployId comes back to the
        // caller via the IDeployBackend contract: each event carries it.
        // Instead, we synchesise on a brief grace window (1 sec) for the
        // backend to mint + INSERT the row so the response carries the id.
        var idTask = await Task.WhenAny(responseTcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        if (idTask == responseTcs.Task && responseTcs.Task.Status == TaskStatus.RanToCompletion)
        {
            return Results.Accepted(value: new { deployId = responseTcs.Task.Result, idempotencyKey });
        }

        // Couldn't capture id in 1s — return Accepted with a placeholder; the
        // SSE event stream will carry the real id. For v0.2 this is a known
        // gap (callers must subscribe to SSE to get the id); v0.3 adds a
        // synchronous mint-then-spawn split.
        return Results.Accepted(value: new { deployId = (string?)null, idempotencyKey, note = "deployId arrives via SSE deploy:event" });
    }

    private static async Task<IResult> GetDeploy(
        string domain,
        string deployId,
        IDeployBackend backend,
        CancellationToken ct)
    {
        try
        {
            var result = await backend.GetStatusAsync(deployId, ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "deploy_not_found", deployId });
        }
    }

    private static async Task<IResult> GetHistory(
        string domain,
        IDeployBackend backend,
        CancellationToken ct,
        int limit = 50)
    {
        var entries = await backend.GetHistoryAsync(domain, limit, ct);
        return Results.Ok(new { domain, count = entries.Count, entries });
    }

    private static async Task<IResult> PostRollback(
        string domain,
        string deployId,
        IDeployBackend backend,
        IDeployIntentValidator intentValidator,
        IDeployRunsRepository runs,
        HttpContext ctx,
        CancellationToken ct)
    {
        try
        {
            // Resolve host from the persisted run so the intent token can be
            // bound to it. Failing here means the run vanished from the DB
            // — surface as 404 before we touch anything destructive.
            var run = await runs.GetByIdAsync(deployId, ct);
            if (run is null) return Results.NotFound(new { error = "deploy_not_found", deployId });

            var rejection = await CheckIntentAsync(ctx, intentValidator, "rollback", domain, run.Host, ct);
            if (rejection is not null) return rejection;

            // Phase 5 hardening — refuse to rollback while another deploy /
            // rollback is in-flight against the same (domain, host). The
            // backend would otherwise race the symlink switch and leave the
            // release tree in a half-written state. The in-flight set is
            // small (typically 0-1 rows per host), so a full scan is fine.
            var inFlight = await runs.ListInFlightAsync(ct);
            var conflict = inFlight.FirstOrDefault(r =>
                string.Equals(r.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Host, run.Host, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(r.Id, deployId, StringComparison.OrdinalIgnoreCase));
            if (conflict is not null)
            {
                return Results.Conflict(new
                {
                    error = "deploy_in_flight",
                    message = "Another deploy or rollback is currently running on this host. Wait for it to finish or cancel it first.",
                    blockingDeployId = conflict.Id,
                    blockingStatus = conflict.Status,
                });
            }

            await backend.RollbackAsync(deployId, ct);
            return Results.Accepted(value: new { sourceDeployId = deployId, status = "rolled_back" });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "deploy_not_found", deployId });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "rollback_failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> DeleteDeploy(
        string domain,
        string deployId,
        IDeployBackend backend,
        IDeployIntentValidator intentValidator,
        IDeployRunsRepository runs,
        HttpContext ctx,
        CancellationToken ct)
    {
        try
        {
            // Daemon-side gate: refuse if the run has crossed PONR. Reads
            // straight from the backend's status.
            var status = await backend.GetStatusAsync(deployId, ct);
            if (status.FinalPhase is DeployPhase.AboutToSwitch or DeployPhase.Switched
                or DeployPhase.AwaitingSoak or DeployPhase.HealthCheck)
            {
                return Results.Conflict(new
                {
                    error = "deploy_past_ponr",
                    message = "Deploy has crossed point of no return. Use rollback instead of cancel.",
                    deployId,
                });
            }

            // Validate intent AFTER the PONR gate so AI clients get the same
            // 409 a GUI user would (cheap to fail-fast on past-PONR; the
            // intent token can still be re-used until consumed).
            var run = await runs.GetByIdAsync(deployId, ct);
            if (run is null) return Results.NotFound(new { error = "deploy_not_found", deployId });
            var rejection = await CheckIntentAsync(ctx, intentValidator, "cancel", domain, run.Host, ct);
            if (rejection is not null) return rejection;

            await backend.CancelAsync(deployId, ct);
            return Results.Accepted(value: new { deployId, status = "cancellation_requested" });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "deploy_not_found", deployId });
        }
        catch (InvalidOperationException ex)
        {
            // Backend says no active deploy — treat as 409 (already terminal).
            return Results.Conflict(new { error = "deploy_not_active", message = ex.Message, deployId });
        }
    }

    // ──────────────────────── Phase 6.1 group endpoints ────────────────────────

    private static async Task<IResult> StartGroup(
        string domain,
        StartGroupBody? body,
        IDeployGroupCoordinator coordinator,
        IDeployEventBroadcaster broadcaster,
        IDeployIntentValidator intentValidator,
        ILoggerFactory loggerFactory,
        HttpContext ctx)
    {
        if (body?.Hosts is null || body.Hosts.Count == 0)
        {
            return Results.BadRequest(new { error = "hosts_required", message = "Provide at least one host." });
        }

        // Group-level intent validation: bind the token to a synthetic
        // host marker "*group*" so a single intent authorises the whole
        // fan-out. Per-host intents would force the AI to mint N tokens
        // for one user-visible action — bad UX. Operators issuing intents
        // for groups MUST request kind=deploy with host="*group*".
        var rejection = await CheckIntentAsync(ctx, intentValidator, "deploy", domain, "*group*", ctx.RequestAborted);
        if (rejection is not null) return rejection;

        var idempotencyKey = body.IdempotencyKey ?? Guid.NewGuid().ToString();
        var optsElement = body.Options ?? JsonDocument.Parse("{}").RootElement;
        var req = new DeployGroupRequest(
            Domain: domain,
            Hosts: body.Hosts,
            IdempotencyKey: idempotencyKey,
            TriggeredBy: ResolveTriggeredBy(ctx),
            BackendOptions: optsElement,
            Snapshot: body.Snapshot);

        var logger = loggerFactory.CreateLogger("NksDeploy.GroupRoutes");
        var progress = new Progress<DeployGroupEvent>(evt =>
        {
            _ = broadcaster.BroadcastAsync("deploy:group-event", evt)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) logger.LogWarning(t.Exception, "SSE broadcast for group event failed");
                }, TaskScheduler.Default);
        });

        try
        {
            var groupId = await coordinator.StartGroupAsync(req, progress, ctx.RequestAborted);
            return Results.Accepted(value: new { groupId, idempotencyKey, hostCount = body.Hosts.Count });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = "invalid_request", message = ex.Message });
        }
    }

    private static async Task<IResult> GetGroup(
        string domain,
        string groupId,
        IDeployGroupCoordinator coordinator,
        CancellationToken ct)
    {
        var status = await coordinator.GetGroupStatusAsync(groupId, ct);
        if (status is null) return Results.NotFound(new { error = "group_not_found", groupId });
        return Results.Ok(status);
    }

    private static async Task<IResult> PostGroupRollback(
        string domain,
        string groupId,
        IDeployGroupCoordinator coordinator,
        IDeployIntentValidator intentValidator,
        HttpContext ctx,
        CancellationToken ct)
    {
        // Same wildcard host marker as StartGroup so intent verbs map cleanly.
        var rejection = await CheckIntentAsync(ctx, intentValidator, "rollback", domain, "*group*", ct);
        if (rejection is not null) return rejection;

        try
        {
            await coordinator.RollbackGroupAsync(groupId, ct);
            return Results.Accepted(value: new { groupId, status = "rollback_initiated" });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "group_not_found", groupId });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "group_rollback_failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
