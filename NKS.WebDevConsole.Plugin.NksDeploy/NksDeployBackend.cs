using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Plugin.SDK.Deploy;

namespace NKS.WebDevConsole.Plugin.NksDeploy;

/// <summary>
/// IDeployBackend implementation that drives the bundled nksdeploy PHP CLI
/// via CliWrap. v0.2 ships a real <see cref="StartDeployAsync"/>; the other
/// methods still throw NotImplementedException pending follow-up commits.
///
/// Lifecycle:
///   1. Mint a UUID DeployId, INSERT a deploy_runs row (status='running').
///   2. Resolve PHP binary (site-pinned via WdcPaths.BinariesRoot first,
///      then PATH).
///   3. Resolve nksdeploy.phar (bundled with this plugin, then PATH).
///   4. Spawn `php nksdeploy.phar deploy {host} --ndjson-events --format=json
///      -c {site_root}/deploy.neon`. Working dir = site root so the PHP
///      hook security guard's project-root check sees the right path.
///   5. Stream stdout via CliWrap event stream; parse each line as NDJSON,
///      forward through IProgress as a DeployEvent. The terminal envelope
///      (event=deploy_complete) finalises the run.
///   6. UPDATE deploy_runs to 'completed' / 'failed' on subprocess exit.
///
/// In-flight subprocess handles live in <see cref="_active"/> keyed by
/// DeployId so <see cref="CancelAsync"/> can SIGTERM them.
/// </summary>
public sealed class NksDeployBackend : IDeployBackend
{
    private readonly ISiteRegistry _siteRegistry;
    private readonly IDeployRunsRepository _runs;
    private readonly IPreDeploySnapshotter? _snapshotter;
    private readonly ILogger<NksDeployBackend> _logger;

    /// <summary>
    /// In-flight subprocess registry. Keyed by DeployId, value carries both
    /// the linked CTS (for graceful cancel) and the subprocess PID (for
    /// the watchdog force-kill path when the PHP child ignores the SIGTERM
    /// CliWrap sends on token cancellation). PID is set the moment CliWrap
    /// emits its <c>StartedCommandEvent</c>.
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveDeploy> _active = new();

    /// <summary>Mutable so the deploy task can stamp the PID after spawn.</summary>
    private sealed class ActiveDeploy
    {
        public ActiveDeploy(CancellationTokenSource cts) { Cts = cts; }
        public CancellationTokenSource Cts { get; }
        public int? Pid { get; set; }
    }

    public NksDeployBackend(
        ISiteRegistry siteRegistry,
        IDeployRunsRepository runs,
        ILogger<NksDeployBackend> logger,
        IPreDeploySnapshotter? snapshotter = null)
    {
        _siteRegistry = siteRegistry;
        _runs = runs;
        _logger = logger;
        // Snapshotter is optional — older daemons without Phase 6.2 wiring
        // still resolve this backend without binding break. When null,
        // request.Snapshot is silently ignored (logged at debug).
        _snapshotter = snapshotter;
    }

    public string BackendId => "nks-deploy";

    public bool CanDeploy(string domain)
    {
        var site = _siteRegistry.GetSite(domain);
        if (site is null) return false;

        // Site is deployable when its root holds a deploy.neon. We check both
        // the parent of DocumentRoot (typical: /sites/myapp/www → /sites/myapp)
        // and DocumentRoot itself (Nette projects put www/ inside src tree).
        var siteRoot = ResolveSiteRoot(site.DocumentRoot);
        return File.Exists(Path.Combine(siteRoot, "deploy.neon"));
    }

    public async Task<string> StartDeployAsync(
        DeployRequest request,
        IProgress<DeployEvent> progress,
        CancellationToken ct)
    {
        var site = _siteRegistry.GetSite(request.Domain)
            ?? throw new InvalidOperationException($"Unknown site: {request.Domain}");

        var deployId = Guid.NewGuid().ToString();
        var siteRoot = ResolveSiteRoot(site.DocumentRoot);
        var configPath = Path.Combine(siteRoot, "deploy.neon");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"deploy.neon not found at {configPath}", configPath);
        }

        var php = ResolvePhpBinary(site.PhpVersion);
        var phar = ResolveNksDeployPhar();

        var startedAt = DateTimeOffset.UtcNow;
        await _runs.InsertAsync(new DeployRunRow(
            Id: deployId,
            Domain: request.Domain,
            Host: request.Host,
            ReleaseId: null,
            Branch: ParseBranchOption(request.BackendOptions),
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

        // Phase 6.2 — pre-deploy DB snapshot. Runs BEFORE we spawn the
        // PHP subprocess so a snapshot failure aborts the deploy with
        // zero side effects. Best-effort skip when no snapshotter is
        // wired (older daemons) or Snapshot.Include is false.
        if (request.Snapshot is { Include: true } && _snapshotter is not null)
        {
            progress.Report(new DeployEvent(
                DeployId: deployId,
                Phase: DeployPhase.PreflightChecks,
                Step: "pre_deploy_backup",
                Message: "Snapshotting database before deploy",
                Timestamp: DateTimeOffset.UtcNow,
                IsTerminal: false,
                IsPastPonr: false));
            try
            {
                var snap = await _snapshotter.CreateAsync(request.Domain, deployId, ct);
                await _runs.UpdatePreDeployBackupAsync(deployId, snap.Path, snap.SizeBytes, ct);
                progress.Report(new DeployEvent(
                    DeployId: deployId,
                    Phase: DeployPhase.PreflightChecks,
                    Step: "pre_deploy_backup",
                    Message: $"Snapshot ok: {snap.Path} ({snap.SizeBytes} bytes, {snap.Duration.TotalMilliseconds:F0} ms)",
                    Timestamp: DateTimeOffset.UtcNow,
                    IsTerminal: false,
                    IsPastPonr: false));
            }
            catch (Exception snapEx)
            {
                _logger.LogError(snapEx, "[{DeployId}] pre-deploy snapshot FAILED — aborting deploy", deployId);
                await _runs.MarkCompletedAsync(deployId,
                    success: false, exitCode: null,
                    errorMessage: $"pre_deploy_backup_failed: {snapEx.Message}",
                    durationMs: 0, ct);
                progress.Report(new DeployEvent(
                    DeployId: deployId,
                    Phase: DeployPhase.Failed,
                    Step: "pre_deploy_backup",
                    Message: $"Snapshot failed: {snapEx.Message}",
                    Timestamp: DateTimeOffset.UtcNow,
                    IsTerminal: true,
                    IsPastPonr: false));
                throw;
            }
        }

        // Per-deploy CTS so CancelAsync can signal without affecting the
        // shared CT supplied by the caller.
        var deployCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var active = new ActiveDeploy(deployCts);
        _active[deployId] = active;

        var sw = Stopwatch.StartNew();
        var success = false;
        int? exitCode = null;
        string? errorMessage = null;

        try
        {
            // -d flags MUST precede the script path so PHP applies them
            // before parsing the phar. Suppresses any startup notices /
            // warnings the host PHP install might emit (deprecated INI
            // entries, missing extensions, php.ini paths) — those would
            // otherwise leak into the NDJSON pipeline as non-JSON lines.
            var args = new List<string>
            {
                "-d", "display_errors=0",
                "-d", "error_reporting=0",
                "-d", "log_errors=0",
                phar,
                "deploy",
                request.Host,
                "-c", configPath,
                "--ndjson-events",
                "--format=json",
            };
            var branch = ParseBranchOption(request.BackendOptions);
            if (!string.IsNullOrEmpty(branch))
            {
                args.Add("--branch");
                args.Add(branch);
            }

            var cmd = Cli.Wrap(php)
                .WithArguments(args)
                .WithWorkingDirectory(siteRoot)
                .WithValidation(CommandResultValidation.None);

            await foreach (var ev in cmd.ListenAsync(deployCts.Token))
            {
                switch (ev)
                {
                    case StartedCommandEvent started:
                        // Stamp the PID into the active registry as soon as
                        // CliWrap reports it — the watchdog in CancelAsync
                        // needs this to escalate from CTS-cancel to a real
                        // OS-level kill if the PHP child ignores SIGTERM.
                        active.Pid = started.ProcessId;
                        break;
                    case StandardOutputCommandEvent stdout:
                        // Per-line handler failures must not abort the
                        // event-stream loop — that would orphan the PHP
                        // subprocess (no exit code processed) and the
                        // user would see a permanently-running deploy.
                        try
                        {
                            await HandleStdoutLineAsync(deployId, request.Domain, stdout.Text, progress, deployCts.Token);
                        }
                        catch (OperationCanceledException) when (deployCts.IsCancellationRequested)
                        {
                            throw; // genuine cancel — let the outer catch handle
                        }
                        catch (Exception lineEx)
                        {
                            _logger.LogWarning(lineEx,
                                "[{DeployId}] stdout line handler threw; continuing pipeline", deployId);
                        }
                        break;
                    case StandardErrorCommandEvent stderr when !string.IsNullOrWhiteSpace(stderr.Text):
                        _logger.LogDebug("[{DeployId}] stderr: {Line}", deployId, stderr.Text);
                        break;
                    case ExitedCommandEvent exited:
                        exitCode = exited.ExitCode;
                        success = exited.ExitCode == 0;
                        if (!success)
                        {
                            errorMessage = $"nksdeploy exit code {exited.ExitCode}";
                        }
                        break;
                }
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
            _logger.LogError(ex, "Deploy {DeployId} threw: {Msg}", deployId, ex.Message);
        }
        finally
        {
            sw.Stop();
            _active.TryRemove(deployId, out _);
            await _runs.MarkCompletedAsync(deployId, success, exitCode, errorMessage, sw.ElapsedMilliseconds, CancellationToken.None);
            deployCts.Dispose();
        }

        // Final terminal event for any IProgress consumer that didn't see
        // deploy_complete on stdout (e.g. subprocess crashed without emitting it).
        progress.Report(new DeployEvent(
            DeployId: deployId,
            Phase: success ? DeployPhase.Done : DeployPhase.Failed,
            Step: "deploy_complete",
            Message: errorMessage ?? (success ? "deploy succeeded" : "deploy failed"),
            Timestamp: DateTimeOffset.UtcNow,
            IsTerminal: true,
            IsPastPonr: false));

        return deployId;
    }

    public async Task<DeployResult> GetStatusAsync(string deployId, CancellationToken ct)
    {
        var row = await _runs.GetByIdAsync(deployId, ct)
            ?? throw new KeyNotFoundException($"Unknown deploy id: {deployId}");

        var phase = StatusToPhase(row.Status);
        var success = row.Status == "completed";
        return new DeployResult(
            DeployId: row.Id,
            Success: success,
            ErrorMessage: row.ErrorMessage,
            StartedAt: row.StartedAt,
            CompletedAt: row.CompletedAt,
            ReleaseId: row.ReleaseId,
            CommitSha: row.CommitSha,
            FinalPhase: phase);
    }

    public async Task<IReadOnlyList<DeployHistoryEntry>> GetHistoryAsync(string domain, int limit, CancellationToken ct)
    {
        // v0.2 reads the local deploy_runs journal — every deploy this wdc
        // instance triggered. The richer "merge with remote /.dep/history.json"
        // path lands in a follow-up so the daemon can show deploys triggered
        // from another workstation; for now the local journal is the
        // authoritative view of "what THIS wdc has done".
        var rows = await _runs.ListForDomainAsync(domain, limit, ct);
        return rows.Select(r => new DeployHistoryEntry(
            DeployId: r.Id,
            Domain: r.Domain,
            Host: r.Host,
            Branch: r.Branch ?? "",
            FinalPhase: StatusToPhase(r.Status),
            StartedAt: r.StartedAt,
            CompletedAt: r.CompletedAt,
            CommitSha: r.CommitSha,
            ReleaseId: r.ReleaseId,
            Error: r.ErrorMessage)).ToList();
    }

    private static DeployPhase StatusToPhase(string status) => status switch
    {
        "queued" => DeployPhase.Queued,
        "running" => DeployPhase.PreflightChecks,
        "awaiting_soak" => DeployPhase.AwaitingSoak,
        "completed" => DeployPhase.Done,
        "failed" => DeployPhase.Failed,
        "cancelled" => DeployPhase.Cancelled,
        "rolling_back" => DeployPhase.RollingBack,
        "rolled_back" => DeployPhase.RolledBack,
        _ => DeployPhase.Queued,
    };

    public async Task RollbackAsync(string deployId, CancellationToken ct)
    {
        // Look up the source deploy to find the host + the release we want
        // to rewind FROM. The actual target release is "previous" (nksdeploy
        // resolves it server-side from /releases/), so we don't pass --release
        // explicitly — just rely on nksdeploy's "default to previous" logic.
        var source = await _runs.GetByIdAsync(deployId, ct)
            ?? throw new KeyNotFoundException($"Unknown deploy id: {deployId}");

        var site = _siteRegistry.GetSite(source.Domain)
            ?? throw new InvalidOperationException($"Site no longer registered: {source.Domain}");
        var siteRoot = ResolveSiteRoot(site.DocumentRoot);
        var configPath = Path.Combine(siteRoot, "deploy.neon");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"deploy.neon not found at {configPath}", configPath);
        }

        var php = ResolvePhpBinary(site.PhpVersion);
        var phar = ResolveNksDeployPhar();

        // Rollback gets its own deploy_runs row — separate audit entry, so
        // the history page shows "deploy → rollback → deploy" as three
        // distinct events even though they touch the same site/host.
        var rollbackId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;
        await _runs.InsertAsync(new DeployRunRow(
            Id: rollbackId,
            Domain: source.Domain,
            Host: source.Host,
            ReleaseId: source.ReleaseId,
            Branch: source.Branch,
            CommitSha: source.CommitSha,
            Status: "rolling_back",
            IsPastPonr: true, // a rollback IS the "past PONR" recovery action
            StartedAt: startedAt,
            CompletedAt: null,
            ExitCode: null,
            ErrorMessage: null,
            DurationMs: null,
            TriggeredBy: "gui",
            BackendId: BackendId,
            CreatedAt: startedAt,
            UpdatedAt: startedAt
        ), ct);

        var sw = Stopwatch.StartNew();
        var success = false;
        int? exitCode = null;
        string? errorMessage = null;

        try
        {
            // Buffered execution — rollback is short and we only need the
            // final exit code. No NDJSON event stream needed (the wdc UI
            // shows rollback as a single atomic operation).
            var args = new List<string>
            {
                "-d", "display_errors=0",
                "-d", "error_reporting=0",
                "-d", "log_errors=0",
                phar,
                "rollback",
                source.Host,
                "-c", configPath,
                "--yes",
                "--format=json",
            };

            var cmd = await Cli.Wrap(php)
                .WithArguments(args)
                .WithWorkingDirectory(siteRoot)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            exitCode = cmd.ExitCode;
            success = cmd.ExitCode == 0;
            if (!success)
            {
                errorMessage = $"nksdeploy rollback exit code {cmd.ExitCode}: {cmd.StandardError}";
            }
        }
        catch (OperationCanceledException)
        {
            errorMessage = "rollback cancelled";
            await _runs.UpdateStatusAsync(rollbackId, "cancelled", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            _logger.LogError(ex, "Rollback {RollbackId} (source {SourceId}) threw: {Msg}",
                rollbackId, deployId, ex.Message);
        }
        finally
        {
            sw.Stop();
            // On success: write 'rolled_back' (final state) instead of the
            // generic 'completed' that MarkCompletedAsync would write.
            if (success)
            {
                await _runs.UpdateStatusAsync(rollbackId, "rolled_back", CancellationToken.None);
                await _runs.MarkCompletedAsync(rollbackId, true, exitCode, null, sw.ElapsedMilliseconds, CancellationToken.None);
                // Re-write status because MarkCompletedAsync clobbers it to 'completed'.
                await _runs.UpdateStatusAsync(rollbackId, "rolled_back", CancellationToken.None);
            }
            else
            {
                await _runs.MarkCompletedAsync(rollbackId, false, exitCode, errorMessage, sw.ElapsedMilliseconds, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Two-phase cancel: signal the linked CTS (CliWrap translates this to
    /// a SIGTERM-equivalent on the child), then start a 10-second watchdog
    /// task. If the deploy is still in <see cref="_active"/> after the
    /// grace window we OS-kill the process tree by PID — this guards
    /// against PHP scripts that swallow SIGTERM (long-blocking SSH
    /// transfer, hook script with a sleep loop, etc.).
    ///
    /// Returns immediately so REST handlers don't block on the 10 s grace.
    /// </summary>
    public Task CancelAsync(string deployId, CancellationToken ct)
    {
        if (!_active.TryGetValue(deployId, out var active))
        {
            // Unknown id (already completed / never started) — surface a soft error;
            // the daemon REST handler maps this to 409.
            throw new InvalidOperationException($"No active deploy with id {deployId}");
        }

        try { active.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* race with completion finally */ }

        // Watchdog: if the deploy is still registered after 10 seconds,
        // SIGKILL the process tree. We snapshot the PID at fire time —
        // even if the entry vanished between schedule and fire we just
        // exit cleanly.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                if (!_active.TryGetValue(deployId, out var stillActive)) return;
                var pid = stillActive.Pid;
                if (pid is null)
                {
                    _logger.LogWarning(
                        "Cancel watchdog for {DeployId} — no PID recorded; cannot escalate to kill",
                        deployId);
                    return;
                }
                try
                {
                    using var proc = Process.GetProcessById(pid.Value);
                    _logger.LogWarning(
                        "Cancel watchdog: PHP subprocess for {DeployId} (pid={Pid}) ignored SIGTERM, force-killing process tree",
                        deployId, pid.Value);
                    proc.Kill(entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                    // Process already exited between the active-check and
                    // GetProcessById — happy path, nothing to do.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Cancel watchdog failed to kill pid={Pid} for {DeployId}", pid.Value, deployId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cancel watchdog for {DeployId} threw", deployId);
            }
        });

        return Task.CompletedTask;
    }

    // ────────────────────────── helpers ──────────────────────────

    private static string ResolveSiteRoot(string documentRoot)
    {
        // Convention: deploy.neon sits next to composer.json at the project
        // root, which is the parent of www/ for Nette projects. Fall back to
        // documentRoot itself if no parent has deploy.neon.
        var parent = Directory.GetParent(documentRoot)?.FullName;
        if (parent is not null && File.Exists(Path.Combine(parent, "deploy.neon")))
        {
            return parent;
        }
        return documentRoot;
    }

    private static string ResolvePhpBinary(string phpVersion)
    {
        var candidate = Path.Combine(WdcPaths.BinariesRoot, "php", phpVersion,
            OperatingSystem.IsWindows() ? "php.exe" : "php");
        if (File.Exists(candidate)) return candidate;

        // Fallback: rely on PATH. CliWrap will surface a clear error if
        // even `php` is unavailable.
        return OperatingSystem.IsWindows() ? "php.exe" : "php";
    }

    private static string ResolveNksDeployPhar()
    {
        // Bundled alongside the plugin DLL: plugins/{id}/nksdeploy.phar.
        // The plugin loader copies plugin.json + DLL + sibling files into
        // its own subdir, so AppContext.BaseDirectory resolves to the
        // plugin's own dir at runtime.
        var bundled = Path.Combine(AppContext.BaseDirectory, "nksdeploy.phar");
        if (File.Exists(bundled)) return bundled;

        // Dev fallback: the standalone deploy-skript checkout. The plugin
        // installer ships the phar under the daemon binaries root in
        // production, so this branch only fires during local dev when
        // nksdeploy is run from source.
        var devCheckout = Path.Combine(WdcPaths.BinariesRoot, "nksdeploy", "nksdeploy.phar");
        if (File.Exists(devCheckout)) return devCheckout;

        // Final fallback: PATH lookup via plain name. The subprocess will
        // fail with a clear "command not found" if absent, which the
        // daemon's error path maps to 503 nksdeploy_unavailable.
        return "nksdeploy";
    }

    private async Task HandleStdoutLineAsync(
        string deployId,
        string domain,
        string line,
        IProgress<DeployEvent> progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // nksdeploy --ndjson-events emits one JSON object per line. PHP
        // notices that leak to stdout (they shouldn't — DeployCommand sets
        // error_reporting on entry — but defense-in-depth) are logged and
        // skipped, not parsed.
        if (!line.TrimStart().StartsWith('{'))
        {
            _logger.LogDebug("[{DeployId}] non-json stdout: {Line}", deployId, line);
            return;
        }

        Dictionary<string, JsonElement>? evt;
        try
        {
            evt = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[{DeployId}] malformed ndjson line, skipping: {Line}", deployId, line);
            return;
        }
        if (evt is null) return;

        var eventName = evt.TryGetValue("event", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() : null;
        var step = evt.TryGetValue("step", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() ?? "" : "";
        var message = evt.TryGetValue("message", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetString() ?? "" : "";

        var (phase, isTerminal, isPastPonr) = MapPhase(eventName, step, evt);

        // Reflect macro-state changes in the persistent row so the daemon's
        // GET /status returns coherent state even if the subprocess dies
        // before emitting deploy_complete.
        var statusForDb = phase switch
        {
            DeployPhase.AwaitingSoak => "awaiting_soak",
            DeployPhase.RollingBack => "rolling_back",
            DeployPhase.RolledBack => "rolled_back",
            _ => null,
        };
        // The DB mirror is best-effort — a SQLite write hiccup (lock
        // contention with backup, disk-full mid-deploy, etc.) must NOT
        // abort the live subprocess pipeline. The deploy_runs row will
        // stay at the previous status; the terminal MarkCompletedAsync in
        // the outer finally still runs and corrects the final state.
        if (statusForDb is not null)
        {
            try { await _runs.UpdateStatusAsync(deployId, statusForDb, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[{DeployId}] mirror UpdateStatusAsync({Status}) failed; continuing", deployId, statusForDb);
            }
        }
        if (isPastPonr)
        {
            try { await _runs.MarkPastPonrAsync(deployId, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[{DeployId}] mirror MarkPastPonrAsync failed; continuing", deployId);
            }
        }

        progress.Report(new DeployEvent(
            DeployId: deployId,
            Phase: phase,
            Step: step,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            IsTerminal: isTerminal,
            IsPastPonr: isPastPonr));
    }

    /// <summary>
    /// Map a raw NDJSON event from nksdeploy to a wdc DeployPhase + terminal /
    /// PONR flags. The phase mapping is deliberately coarse — fine-grained
    /// step names live in <see cref="DeployEvent.Step"/>.
    /// </summary>
    private static (DeployPhase Phase, bool IsTerminal, bool IsPastPonr) MapPhase(
        string? eventName,
        string step,
        Dictionary<string, JsonElement> evt)
    {
        if (eventName == "deploy_complete")
        {
            var statusOk = evt.TryGetValue("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "success";
            return (statusOk ? DeployPhase.Done : DeployPhase.Failed, IsTerminal: true, IsPastPonr: false);
        }

        if (eventName == "step_started")
        {
            var phase = step switch
            {
                "git:pull" or "git_clone" or "git_pull" => DeployPhase.Fetching,
                "composer:install" or "composer_install" or "npm:build" or "npm_build" => DeployPhase.Building,
                "doctrine:schema-update" or "schema_update" or "schema_validate" => DeployPhase.Migrating,
                "symlink_switch" => DeployPhase.AboutToSwitch,
                "deploy:health-check" or "health_check" => DeployPhase.HealthCheck,
                _ => DeployPhase.PreflightChecks,
            };
            return (phase, IsTerminal: false, IsPastPonr: false);
        }

        if (eventName == "step_done" && step == "symlink_switch")
        {
            var statusOk = evt.TryGetValue("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "ok";
            return (DeployPhase.Switched, IsTerminal: false, IsPastPonr: statusOk);
        }

        // Generic log/info lines stay in PreflightChecks bucket — the
        // wdc UI's StepWaterfall keys off Step name, not Phase, for granular
        // progress display, so the macro phase here just disambiguates the
        // major UI sections.
        return (DeployPhase.PreflightChecks, IsTerminal: false, IsPastPonr: false);
    }

    private static string? ParseBranchOption(JsonElement opts)
    {
        if (opts.ValueKind != JsonValueKind.Object) return null;
        if (!opts.TryGetProperty("branch", out var b)) return null;
        return b.ValueKind == JsonValueKind.String ? b.GetString() : null;
    }
}
