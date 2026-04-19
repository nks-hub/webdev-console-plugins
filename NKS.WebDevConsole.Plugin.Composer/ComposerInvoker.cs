using CliWrap;
using CliWrap.Buffered;

namespace NKS.WebDevConsole.Plugin.Composer;

/// <summary>
/// Result of a single Composer CLI invocation.
/// </summary>
/// <param name="ExitCode">Process exit code. 0 = success.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
public sealed record ComposerCommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Abstraction over Composer child-process invocations so unit tests can
/// verify argv construction without spawning a real process. The default
/// implementation <see cref="CliWrapComposerProcessRunner"/> uses CliWrap;
/// tests substitute a Moq strict mock.
/// </summary>
public interface IComposerProcessRunner
{
    /// <summary>
    /// Runs a process to completion and returns exit code + captured output.
    /// </summary>
    /// <param name="executable">The executable to launch (php or composer shim).</param>
    /// <param name="arguments">Ordered argument list passed verbatim.</param>
    /// <param name="workingDirectory">Working directory for the child process.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ComposerCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct);
}

/// <summary>
/// Default <see cref="IComposerProcessRunner"/> implementation backed by CliWrap.
/// </summary>
internal sealed class CliWrapComposerProcessRunner : IComposerProcessRunner
{
    public async Task<ComposerCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct)
    {
        var cmd = Cli.Wrap(executable)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);

        var result = await cmd.ExecuteBufferedAsync(ct);
        return new ComposerCommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}

/// <summary>
/// Invokes Composer commands (install, require, arbitrary argv) against a site root.
///
/// Composer is a stateless CLI tool — there is no daemon process to start or stop.
/// Every method runs <c>php composer.phar &lt;args&gt; --no-interaction</c> in the given
/// <paramref name="siteRoot"/> and returns the captured output plus exit code.
///
/// If <see cref="ComposerConfig.ExecutablePath"/> ends with <c>.phar</c> the invoker
/// prepends <see cref="ComposerConfig.PhpPath"/> as the interpreter; otherwise it
/// treats the path as a native binary or shell wrapper and invokes it directly.
/// </summary>
public sealed class ComposerInvoker
{
    private readonly ComposerConfig _config;
    private readonly IComposerProcessRunner _runner;

    /// <param name="config">Composer configuration (paths, PHP location).</param>
    /// <param name="runner">Process runner; defaults to <see cref="CliWrapComposerProcessRunner"/>.</param>
    public ComposerInvoker(ComposerConfig config, IComposerProcessRunner? runner = null)
    {
        _config = config;
        _runner = runner ?? new CliWrapComposerProcessRunner();
    }

    /// <summary>
    /// Runs <c>composer install --no-interaction</c> in <paramref name="siteRoot"/>.
    /// </summary>
    /// <param name="siteRoot">Absolute path to the site whose <c>composer.json</c> should be processed.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ComposerCommandResult> InstallAsync(string siteRoot, CancellationToken ct = default)
        => RunAsync(siteRoot, ["install"], ct);

    /// <summary>
    /// Runs <c>composer require &lt;package&gt; --no-interaction</c> in <paramref name="siteRoot"/>.
    /// </summary>
    /// <param name="siteRoot">Absolute path to the site root.</param>
    /// <param name="package">Package name (and optional version constraint) to require, e.g. <c>nette/application:^3.2</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ComposerCommandResult> RequireAsync(string siteRoot, string package, CancellationToken ct = default)
        => RunAsync(siteRoot, ["require", package], ct);

    /// <summary>
    /// Generic escape hatch — runs Composer with an arbitrary argv in <paramref name="siteRoot"/>.
    /// <c>--no-interaction</c> is always appended automatically.
    /// </summary>
    /// <param name="siteRoot">Absolute path to the site root.</param>
    /// <param name="argv">Composer sub-command and flags, e.g. <c>["update", "--prefer-dist"]</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ComposerCommandResult> RunAsync(
        string siteRoot,
        IReadOnlyList<string> argv,
        CancellationToken ct = default)
    {
        var (executable, args) = BuildInvocation(argv);
        return _runner.RunAsync(executable, args, siteRoot, ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the (executable, argument list) pair for a Composer invocation.
    ///
    /// When <see cref="ComposerConfig.ExecutablePath"/> ends with <c>.phar</c>:
    ///   executable = PhpPath
    ///   arguments  = [composer.phar, ...argv, --no-interaction]
    ///
    /// When it is a native binary or shell wrapper:
    ///   executable = ExecutablePath
    ///   arguments  = [...argv, --no-interaction]
    /// </summary>
    private (string Executable, IReadOnlyList<string> Args) BuildInvocation(IReadOnlyList<string> argv)
    {
        var pharPath = _config.ExecutablePath
            ?? throw new InvalidOperationException(
                "ComposerConfig.ExecutablePath is not set. Call ApplyOwnBinaryDefaults() first.");

        var args = new List<string>();
        string executable;

        if (pharPath.EndsWith(".phar", StringComparison.OrdinalIgnoreCase))
        {
            // Invoke via PHP interpreter: php composer.phar <argv> --no-interaction
            executable = _config.PhpPath;
            args.Add(pharPath);
        }
        else
        {
            // Native binary / shell wrapper: composer <argv> --no-interaction
            executable = pharPath;
        }

        args.AddRange(argv);
        args.Add("--no-interaction");

        return (executable, args);
    }
}
