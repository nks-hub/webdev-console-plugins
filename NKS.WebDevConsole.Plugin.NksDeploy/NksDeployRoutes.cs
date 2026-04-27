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
        // Phase 6.7 — group history list (newest first, limit-bounded).
        r.MapGet("sites/{domain}/groups", ListGroups);
        // Phase 6.3 — per-site settings persistence + snapshot inventory.
        r.MapGet("sites/{domain}/settings", GetSettings);
        r.MapPut("sites/{domain}/settings", PutSettings);
        r.MapGet("sites/{domain}/snapshots", ListSnapshots);
        r.MapGet("sites/{domain}/snapshot-info", GetSnapshotInfo);
        // Phase 6.4 — operator-driven snapshot restore. Destructive — gated
        // by intent token AND requires an explicit confirm flag in body.
        r.MapPost("sites/{domain}/snapshots/{deployId}/restore", PostSnapshotRestore);
        // Phase 6.6 — on-demand snapshot WITHOUT a deploy. Useful before
        // manual DB ops (large migration via SQL client, schema rename).
        r.MapPost("sites/{domain}/snapshot-now", PostSnapshotNow);
        // Phase B (#109-B1) — operator utility: TCP-probe a host:port pair.
        // Used by the host-edit dialog's "Test SSH" button. Pure utility,
        // no NksDeployBackend deps. Site-scoped path lets future per-site
        // probe policy (proxy, fallback) attach without breaking callers.
        r.MapPost("test-host-connection", TestHostConnection);
        // Phase B (#109-B2) — deploy WITHOUT /hosts/{host}/ in path. Host
        // comes from body or defaults to "production". Mirrors daemon's
        // /api/nks.wdc.deploy/sites/{domain}/deploy convenience wrapper.
        // Lets GUI/MCP callers dispatch a deploy with just domain+body
        // when the operator's per-site default-host is "production".
        r.MapPost("sites/{domain}/deploy", StartDeployBodyHost);
        // Phase B (#109-B3) — rollback to a SPECIFIC release (vs default
        // "previous_release"). Body must include {targetReleaseId}.
        // Uses concrete NksDeployBackend.RollbackToAsync — interface stays
        // clean (no IDeployBackend.RollbackToAsync forced on all backends).
        r.MapPost("sites/{domain}/deploys/{deployId}/rollback-to", PostRollbackTo);
        // Phase B (#109-B4) — execute a single hook ad-hoc to validate
        // the operator's hook config (shell/http/php) before it runs in a
        // real deploy. Intent-gated under kind=test_hook because the hook
        // body is arbitrary code execution on the daemon host.
        r.MapPost("sites/{domain}/hooks/test", PostTestHook);
        // Phase B (#109-B5) — fire a single Slack notification ad-hoc so
        // operator can verify the configured webhook before a real
        // deploy:complete fires it. No intent gate — the message body is
        // canned ("test notification") so this isn't a code-execution
        // surface, just a network egress to the configured URL.
        r.MapPost("sites/{domain}/notifications/test", PostTestNotification);
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

    /// <summary>
    /// Phase 6.7 — list groups for the site (newest started_at first).
    /// limit defaults to 50, capped server-side at 200 by the repo.
    /// </summary>
    private static async Task<IResult> ListGroups(
        string domain,
        IDeployGroupsRepository groupsRepo,
        IDeployRunsRepository runsRepo,
        CancellationToken ct,
        int limit = 50)
    {
        var rows = await groupsRepo.ListForDomainAsync(domain, limit, ct);
        var entries = new List<object>(rows.Count);
        foreach (var r in rows)
        {
            // Phase 6.15b — enrich with per-host run status so the GUI
            // can offer "replay only failed hosts" subset.
            // Only fetch when there are recorded host deploys (most rows
            // pre-fan-out have empty maps and would just no-op).
            var hostStatuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (r.HostDeployIds.Count > 0)
            {
                var perHost = await runsRepo.ListByGroupAsync(r.Id, ct);
                foreach (var run in perHost)
                {
                    hostStatuses[run.Host] = run.Status;
                }
            }
            entries.Add(new
            {
                id = r.Id,
                domain = r.Domain,
                hosts = r.Hosts,
                hostDeployIds = r.HostDeployIds,
                hostStatuses,
                phase = r.Phase,
                startedAt = r.StartedAt.ToString("o"),
                completedAt = r.CompletedAt?.ToString("o"),
                errorMessage = r.ErrorMessage,
                triggeredBy = r.TriggeredBy,
            });
        }
        return Results.Ok(new { domain, count = entries.Count, entries });
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

    // ──────────────────────── Phase 6.3 settings + snapshots ────────────────────────

    /// <summary>
    /// Returns the per-site deploy settings JSON blob, or HTTP 200 with an
    /// empty object if no settings file exists yet (frontend supplies its
    /// own defaults via defaultDeploySettings()). Pass-through JSON — the
    /// daemon doesn't validate the structure; the frontend types are the
    /// source of truth.
    /// </summary>
    private static IResult GetSettings(string domain)
    {
        var path = SettingsPath(domain);
        if (!File.Exists(path)) return Results.Ok(new { });
        try
        {
            var text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            return Results.Json(doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            return Results.Problem(
                title: "settings_corrupted",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Atomic write of the per-site deploy settings. Body MUST be a JSON
    /// object (we don't validate the shape — frontend owns the schema).
    /// Uses temp+rename so a crashed write never corrupts an existing file.
    /// </summary>
    private static async Task<IResult> PutSettings(string domain, HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { error = "body_must_be_object" });
            }
            var dir = SettingsDir(domain);
            Directory.CreateDirectory(dir);
            var path = SettingsPath(domain);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, doc.RootElement.GetRawText());
            File.Move(tmp, path, overwrite: true);
            return Results.NoContent();
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", detail = ex.Message });
        }
    }

    /// <summary>
    /// List pre-deploy snapshot files for this site by scanning the deploy
    /// runs table for rows that wrote a pre_deploy_backup_path. Snapshots
    /// belonging to other sites are filtered out by joining on domain.
    /// </summary>
    private static async Task<IResult> ListSnapshots(
        string domain,
        IDeployRunsRepository runs,
        CancellationToken ct)
    {
        var rows = await runs.ListForDomainAsync(domain, limit: 200, ct);
        var entries = rows
            .Where(r => !string.IsNullOrEmpty(r.PreDeployBackupPath))
            .Select(r => new
            {
                id = r.Id,
                createdAt = r.StartedAt.ToString("o"),
                sizeBytes = r.PreDeployBackupSizeBytes ?? 0L,
                path = r.PreDeployBackupPath!,
            })
            .ToList();
        return Results.Ok(entries);
    }

    /// <summary>
    /// Auto-detect snapshotable database for this site. Mirrors the scan
    /// PreDeploySnapshotter does internally so the GUI can show a banner
    /// like "SQLite detected at /var/.../app.sqlite" before the user opts
    /// in. Returns { detected: bool, type: 'sqlite'|'mysql'|'pg'|null,
    /// path?: string, hint: string }.
    /// </summary>
    private static IResult GetSnapshotInfo(string domain, ISiteRegistry sites)
    {
        var site = sites.GetSite(domain);
        if (site is null) return Results.NotFound(new { error = "site_not_found", domain });

        var siteRoot = Directory.GetParent(site.DocumentRoot)?.FullName ?? site.DocumentRoot;
        string[] dirs = { siteRoot, Path.Combine(siteRoot, "app"), Path.Combine(siteRoot, "var"),
            Path.Combine(siteRoot, "data"), Path.Combine(siteRoot, "db"),
            Path.Combine(siteRoot, "database"), site.DocumentRoot };
        string[] exts = { "*.sqlite", "*.sqlite3", "*.db" };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    var hit = Directory.EnumerateFiles(dir, ext).FirstOrDefault();
                    if (hit is not null)
                    {
                        return Results.Ok(new
                        {
                            detected = true,
                            type = "sqlite",
                            path = hit,
                            hint = "SQLite database detected — pre-deploy snapshot will copy + gzip this file.",
                        });
                    }
                }
                catch (UnauthorizedAccessException) { /* skip */ }
            }
        }
        return Results.Ok(new
        {
            detected = false,
            type = (string?)null,
            path = (string?)null,
            hint = "No SQLite database found. MySQL/PostgreSQL snapshot integration ships in Phase 6.3 (mysqldump/pg_dump with credential resolution).",
        });
    }

    private static string SettingsDir(string domain) =>
        Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.SitesRoot, domain);

    private static string SettingsPath(string domain) =>
        Path.Combine(SettingsDir(domain), "deploy-settings.json");

    // ──────────────────────── Phase 6.6 on-demand snapshot ────────────────────────

    /// <summary>
    /// Take a database snapshot WITHOUT firing a deploy. Persists a
    /// synthetic deploy_runs row tagged backend_id=<c>"manual-snapshot"</c>
    /// + status=<c>"completed"</c> so the snapshot shows up in the
    /// per-site snapshot inventory and can be restored via the standard
    /// flow. Operator must confirm via body since this still touches the
    /// site's DB connection (reads only — no writes here).
    ///
    /// Useful before manual DB ops: large schema migrations, column
    /// renames, ad-hoc data fixes via mysql client. The snapshot is then
    /// available to restore via the existing endpoint if the manual op
    /// goes wrong.
    /// </summary>
    private static async Task<IResult> PostSnapshotNow(
        string domain,
        IPreDeploySnapshotter snapshotter,
        NksDeployBackend backend,
        IDeployRunsRepository runs,
        HttpContext ctx,
        CancellationToken ct)
    {
        // Optional body { host: "..." } — picks a specific host's current/.
        // No body or no host → first host with localTargetPath wins.
        string? bodyHost = null;
        if (ctx.Request.ContentLength is > 0)
        {
            try
            {
                using var bdoc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (bdoc.RootElement.TryGetProperty("host", out var hEl))
                    bodyHost = hEl.GetString();
            }
            catch { /* empty / non-JSON body is fine */ }
        }

        // Synthetic id for the deploy_runs row — namespaced so reports can
        // distinguish it from real deploys via the prefix.
        var id = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Phase C (#109-C1) — try filesystem-ZIP first (matches daemon's
        // inline snapshot-now behaviour). Falls back to the DB-only
        // IPreDeploySnapshotter when no host has localTargetPath.
        var zipResult = await backend.SnapshotCurrentReleaseAsync(domain, bodyHost, id, ct);

        var row = new DeployRunRow(
            Id: id,
            Domain: domain,
            Host: zipResult?.Host ?? "*manual*",
            ReleaseId: zipResult is not null ? now.ToString("yyyyMMdd_HHmmss") + "-manual" : null,
            Branch: null,
            CommitSha: null,
            Status: "running", // will flip to completed after snapshot succeeds
            IsPastPonr: false,
            StartedAt: now,
            CompletedAt: null,
            ExitCode: null,
            ErrorMessage: null,
            DurationMs: null,
            TriggeredBy: ResolveTriggeredBy(ctx),
            BackendId: "manual-snapshot",
            CreatedAt: now,
            UpdatedAt: now);
        await runs.InsertAsync(row, ct);

        if (zipResult is not null)
        {
            sw.Stop();
            await runs.UpdatePreDeployBackupAsync(id, zipResult.Value.Path, zipResult.Value.SizeBytes, ct);
            await runs.MarkCompletedAsync(id,
                success: true, exitCode: 0, errorMessage: null,
                durationMs: sw.ElapsedMilliseconds, ct);
            return Results.Ok(new
            {
                snapshotId = id,
                domain,
                path = zipResult.Value.Path,
                sizeBytes = zipResult.Value.SizeBytes,
                durationMs = sw.ElapsedMilliseconds,
                host = zipResult.Value.Host,
            });
        }

        try
        {
            var result = await snapshotter.CreateAsync(domain, id, ct);
            await runs.UpdatePreDeployBackupAsync(id, result.Path, result.SizeBytes, ct);
            await runs.MarkCompletedAsync(id,
                success: true, exitCode: 0, errorMessage: null,
                durationMs: (long)result.Duration.TotalMilliseconds, ct);
            return Results.Ok(new
            {
                snapshotId = id,
                domain,
                path = result.Path,
                sizeBytes = result.SizeBytes,
                durationMs = (long)result.Duration.TotalMilliseconds,
            });
        }
        catch (Exception ex)
        {
            // Surface the failure on the synthetic row so the snapshot list
            // shows it as failed rather than dangling in 'running'.
            await runs.MarkCompletedAsync(id,
                success: false, exitCode: null,
                errorMessage: $"snapshot_now_failed: {ex.Message}",
                durationMs: 0, ct);
            return Results.Problem(
                title: "snapshot_now_failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ──────────────────────── Phase 6.4 snapshot restore ────────────────────────

    /// <summary>Body for POST .../snapshots/{deployId}/restore.</summary>
    public sealed record RestoreSnapshotBody(bool Confirm);

    /// <summary>
    /// Restore the snapshot recorded on a deploy_runs row. Two gates
    /// enforce the destructive nature: (1) intent token validation
    /// (kind=restore — see CheckIntentAsync), (2) explicit
    /// <c>confirm:true</c> in the body so a stray POST cannot trigger
    /// a live-data overwrite.
    ///
    /// SQLite restores create a safety <c>.bak</c> next to the live file
    /// before overwrite; SQL restores have no such net (operator must
    /// have a separate DB backup). The endpoint surfaces every failure
    /// as a structured error rather than a generic 500.
    /// </summary>
    private static async Task<IResult> PostSnapshotRestore(
        string domain,
        string deployId,
        RestoreSnapshotBody? body,
        ISnapshotRestorer restorer,
        IDeployIntentValidator intentValidator,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (body is null || !body.Confirm)
        {
            return Results.BadRequest(new
            {
                error = "confirm_required",
                message = "Restore is irreversible — POST { \"confirm\": true } to proceed.",
            });
        }

        // Snapshot restore is its own intent kind so a deploy/rollback
        // intent can't be reused to silently overwrite live data. We bind
        // to the synthetic host marker "*restore*" mirroring the *group*
        // pattern from Phase 6.1.
        var rejection = await CheckIntentAsync(ctx, intentValidator, "restore", domain, "*restore*", ct);
        if (rejection is not null) return rejection;

        try
        {
            var result = await restorer.RestoreAsync(domain, deployId, ct);
            return Results.Ok(new
            {
                deployId,
                domain,
                mode = result.Mode,
                bytesProcessed = result.BytesProcessed,
                durationMs = (long)result.Duration.TotalMilliseconds,
            });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "deploy_not_found", deployId });
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound(new { error = "snapshot_archive_missing", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Domain mismatch / no .env / scaffold archive / missing client
            // — all surface as 400 because they're caller-resolvable rather
            // than server faults.
            return Results.BadRequest(new { error = "restore_refused", message = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "restore_failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Body for POST .../deploy (no /hosts/ segment). Same as StartDeployBody
    /// but adds optional Host field. When absent, defaults to "production"
    /// — matching the daemon convenience wrapper at Program.cs:3502.
    /// </summary>
    public sealed record StartDeployBodyWithHost(
        string? Host,
        string? IdempotencyKey,
        JsonElement? Options,
        DeploySnapshotOptions? Snapshot = null);

    /// <summary>
    /// Phase B (#109-B2) — deploy without /hosts/{host}/ in path.
    /// Reads host from body or defaults "production", then delegates
    /// to the standard StartDeploy handler with the resolved host.
    /// </summary>
    /// <summary>
    /// Body for POST .../rollback-to. <c>TargetReleaseId</c> is required and
    /// must match an existing release directory under the site's deploy root.
    /// nksdeploy phar validates the release id; we just forward it via -r.
    /// </summary>
    public sealed record RollbackToBody(string? TargetReleaseId);

    /// <summary>
    /// Phase B (#109-B3) — rollback to a specific release id. Mirrors the
    /// daemon's /api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}/rollback-to
    /// endpoint. Uses the plugin-internal concrete NksDeployBackend so the
    /// IDeployBackend SDK interface doesn't grow a new method.
    /// </summary>
    private static async Task<IResult> PostRollbackTo(
        string domain,
        string deployId,
        RollbackToBody? body,
        NksDeployBackend backend,
        IDeployIntentValidator intentValidator,
        IDeployRunsRepository runs,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.TargetReleaseId))
            return Results.BadRequest(new { error = "targetReleaseId required" });

        try
        {
            var run = await runs.GetByIdAsync(deployId, ct);
            if (run is null) return Results.NotFound(new { error = "deploy_not_found", deployId });

            // Same intent kind as plain rollback — both flip the symlink, both
            // need operator confirmation. The MCP gate kind is "rollback_to"
            // server-side; aligning under "rollback" for the validator keeps
            // existing grants applicable to both shapes.
            var rejection = await CheckIntentAsync(ctx, intentValidator, "rollback", domain, run.Host, ct);
            if (rejection is not null) return rejection;

            // Reuse the same in-flight guard as PostRollback — refuse to
            // start a rollback-to while another deploy/rollback is running
            // on the same host (race on the symlink swap).
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

            await backend.RollbackToAsync(deployId, body.TargetReleaseId, ct);
            return Results.Accepted(value: new
            {
                sourceDeployId = deployId,
                targetReleaseId = body.TargetReleaseId,
                status = "rolled_back",
            });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "deploy_not_found", deployId });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "rollback_to_failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Body for POST .../hooks/test. Mirrors daemon shape so the GUI's
    /// per-card "Test" button doesn't need a different request between
    /// built-in and plugin backends. <c>Type</c> is one of
    /// shell|http|php; <c>Command</c> is the arbitrary payload (URL for
    /// http, code/script-path for php, command-line for shell).
    /// </summary>
    public sealed record TestHookBody(
        string? Type,
        string? Command,
        int? TimeoutSeconds,
        IReadOnlyDictionary<string, string>? EnvVars);

    /// <summary>
    /// Phase B (#109-B4) — ad-hoc hook execution. Mirrors daemon's
    /// /api/nks.wdc.deploy/sites/{domain}/hooks/test. Direct C# (no phar
    /// involvement) because phar has no hook-test command and the test
    /// has no per-deploy context to pass it. Intent-gated under
    /// kind=test_hook (arbitrary code execution surface). Domain is
    /// part of the path for grant-scoping; the test itself is site-agnostic.
    /// </summary>
    private static async Task<IResult> PostTestHook(
        string domain,
        TestHookBody? body,
        NksDeployBackend backend,
        IDeployIntentValidator intentValidator,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Type))
            return Results.BadRequest(new { error = "type required (shell|http|php)" });
        if (string.IsNullOrWhiteSpace(body?.Command))
            return Results.BadRequest(new { error = "command required" });

        // host = domain because there's no per-host scope for ad-hoc hook
        // tests; the operator approves it for the site as a whole.
        var rejection = await CheckIntentAsync(ctx, intentValidator, "test_hook", domain, domain, ct);
        if (rejection is not null) return rejection;

        var (ok, durationMs, error) = await backend.TestHookAsync(
            body.Type!,
            body.Command!,
            body.TimeoutSeconds ?? 5,
            body.EnvVars,
            ct);

        return Results.Json(new
        {
            ok,
            durationMs,
            error,
        });
    }

    /// <summary>
    /// Body for POST .../notifications/test. <c>SlackWebhook</c> is
    /// optional — if absent the backend falls back to per-site
    /// <c>deploy-settings.json</c>'s <c>notifications.slackWebhook</c>.
    /// </summary>
    public sealed record TestNotificationBody(
        string? SlackWebhook,
        string? Host);

    /// <summary>
    /// Phase B (#109-B5) — Slack webhook smoke test. Mirrors daemon's
    /// /api/nks.wdc.deploy/sites/{domain}/notifications/test. Direct C#
    /// HttpClient post (no phar). Returns the same shape as the daemon
    /// so the GUI's per-channel "Test" button doesn't branch on backend.
    /// </summary>
    private static async Task<IResult> PostTestNotification(
        string domain,
        TestNotificationBody? body,
        NksDeployBackend backend,
        CancellationToken ct)
    {
        var (ok, durationMs, error) = await backend.TestNotificationAsync(
            domain, body?.Host, body?.SlackWebhook, ct);
        if (!ok && string.Equals(error, "slack_webhook_not_configured", StringComparison.Ordinal))
            return Results.BadRequest(new { error });
        return Results.Json(new { ok, durationMs, error });
    }

    private static Task<IResult> StartDeployBodyHost(
        string domain,
        StartDeployBodyWithHost? body,
        IDeployBackend backend,
        IDeployEventBroadcaster broadcaster,
        IDeployIntentValidator intentValidator,
        ILoggerFactory loggerFactory,
        HttpContext ctx)
    {
        var host = string.IsNullOrWhiteSpace(body?.Host) ? "production" : body.Host;
        // Re-pack body without Host (StartDeploy gets host from path arg).
        var inner = body is null
            ? null
            : new StartDeployBody(body.IdempotencyKey, body.Options, body.Snapshot);
        return StartDeploy(domain, host!, inner, backend, broadcaster, intentValidator, loggerFactory, ctx);
    }

    /// <summary>
    /// Phase B (#109-B1) — TCP-probe a host:port. Mirrors daemon's
    /// /api/nks.wdc.deploy/test-host-connection (Program.cs:2196).
    /// Pure utility, no NksDeployBackend deps. Used by GUI host-edit
    /// dialog's "Test SSH" button. 5s timeout, structured result.
    /// </summary>
    private static async Task<IResult> TestHostConnection(HttpContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var host = root.TryGetProperty("host", out var hEl) ? hEl.GetString() : null;
        var port = root.TryGetProperty("port", out var pEl) && pEl.TryGetInt32(out var p) ? p : 22;

        if (string.IsNullOrWhiteSpace(host))
            return Results.BadRequest(new { error = "host is required" });
        if (port < 1 || port > 65535)
            return Results.BadRequest(new { error = "port must be in [1, 65535]" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var probe = new System.Net.Sockets.TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await probe.ConnectAsync(host!, port, cts.Token);
            sw.Stop();
            return Results.Ok(new { ok = true, latencyMs = sw.ElapsedMilliseconds });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            return Results.Ok(new
            {
                ok = false, code = "timeout",
                error = $"TCP probe to {host}:{port} timed out after 5s",
            });
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            sw.Stop();
            return Results.Ok(new
            {
                ok = false, code = "socket_error",
                error = $"{host}:{port} unreachable: {ex.Message}",
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Results.Ok(new
            {
                ok = false, code = "unexpected",
                error = ex.Message,
            });
        }
    }
}
