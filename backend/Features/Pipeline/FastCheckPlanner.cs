using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>Fast checks of one diff plus the audit lines that say what each touched area got.</summary>
public sealed record FastCheckPlan(IReadOnlyList<VerifyCommand> Commands, IReadOnlyList<string> Reasons);

/// <summary>
/// Lint, type check and format for the touched areas of a per-card plan
/// (AGT-3310, pipeline Dossier tier T1). Convention over settings:
/// <list type="bullet">
/// <item>Lint: the declared lint commands run repository-wide, as before. A
/// touched Node package without an attributable declared lint command adds its
/// own <c>lint</c> script.</item>
/// <item>Type check: a touched Node package adds its <c>typecheck</c> script; a
/// touched .NET project is type-checked by the build commands.</item>
/// <item>Format: a touched Node package adds its <c>format:check</c> script.
/// A package without one, and .NET code, get an audit line instead of an
/// invented formatter run that would fail on files the card did not format.</item>
/// </list>
/// A script runs through the declared invocation of its package when one
/// exists (for example <c>bash scripts/npm-frontend.sh run lint</c> becomes
/// <c>bash scripts/npm-frontend.sh run typecheck</c>), otherwise as
/// <c>npm run</c> in the package directory.
/// </summary>
public static partial class FastCheckPlanner
{
    private static readonly (string Check, string Script)[] PackageChecks =
    [
        ("type check", "typecheck"),
        ("format", "format:check"),
    ];

    public static FastCheckPlan Plan(
        string repositoryPath,
        VerifyPlan verifyPlan,
        IReadOnlyList<string> changedFiles)
    {
        var files = changedFiles.Select(path => path.Replace('\\', '/').TrimStart('/'))
            .Where(path => path.Length > 0).ToList();
        var commands = new List<VerifyCommand>();
        var reasons = new List<string>();
        var declaredLint = verifyPlan.Commands.Where(command => command.Kind == VerifyCommandKind.Lint).ToList();
        if (declaredLint.Count > 0)
            reasons.Add($"fast check lint: {declaredLint.Count} declared lint command(s) run repository-wide");

        var packages = VerifyCommandPlanner.NodePackageDirs(repositoryPath);
        foreach (var package in packages.Where(package => Touches(files, package, packages)))
        {
            var label = package.Length == 0 ? "repository root package" : package + "/";
            var scripts = VerifyCommandPlanner.ReadNpmScripts(Path.Combine(repositoryPath, package, "package.json"));
            var invocation = Invocation(verifyPlan, package);
            var checks = PackageChecks.ToList();
            if (!declaredLint.Any(command => AttributedTo(command, package))) checks.Insert(0, ("lint", "lint"));
            foreach (var (check, script) in checks)
            {
                if (!VerifyCommandPlanner.HasScript(scripts, script))
                {
                    reasons.Add($"fast check {check}: {label} declares no '{script}' script");
                    continue;
                }
                var command = invocation is { } declared
                    ? new VerifyCommand(declared.Ecosystem, VerifyCommandKind.Lint, declared.WorkingSubdir,
                        $"{declared.Prefix} {script}") { Shell = declared.Shell }
                    : new VerifyCommand(VerifyEcosystem.Node, VerifyCommandKind.Lint, package, $"npm run {script}")
                    {
                        Shell = VerifyCommandShell.Bash,
                    };
                if (verifyPlan.Commands.Any(existing => SameCommand(existing, command))) continue;
                commands.Add(command with { SelectionReason = $"fast check {check} for touched {label}" });
                reasons.Add($"fast check {check}: {Describe(command)}");
            }
        }

        if (DotNetWorkPackagePlanner.TouchesDotNet(files))
        {
            reasons.Add("fast check type check: the build commands compile the touched .NET projects");
            reasons.Add("fast check format: no .NET format check is declared");
        }
        return new FastCheckPlan(commands, reasons);
    }

    private static bool Touches(IReadOnlyList<string> files, string package, IReadOnlyList<string> packages)
        => package.Length > 0
            ? files.Any(path => path.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase))
            : files.Any(path => !packages.Any(other => other.Length > 0
                && path.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The declared <c>&lt;prefix&gt; run &lt;script&gt;</c> invocation of a package, if any.</summary>
    private static (string Prefix, string WorkingSubdir, VerifyEcosystem Ecosystem, VerifyCommandShell Shell)? Invocation(
        VerifyPlan verifyPlan, string package)
    {
        foreach (var command in verifyPlan.Commands
                     .OrderBy(command => command.Kind == VerifyCommandKind.Lint ? 0 : 1)
                     .Where(command => AttributedTo(command, package)))
        {
            var match = RunScript().Match(command.Command.Trim());
            if (match.Success)
                return (match.Groups["prefix"].Value, command.WorkingSubdir, command.Ecosystem, command.Shell);
        }
        return null;
    }

    /// <summary>
    /// A command belongs to a package when it runs in the package directory,
    /// or when it runs at the root and names the package as a whole word.
    /// </summary>
    private static bool AttributedTo(VerifyCommand command, string package)
    {
        var subdir = command.WorkingSubdir.Replace('\\', '/').Trim('/');
        if (subdir.Length > 0) return string.Equals(subdir, package, StringComparison.OrdinalIgnoreCase);
        return package.Length > 0 && Regex.IsMatch(command.Command,
            $"(?<![A-Za-z0-9]){Regex.Escape(package)}(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool SameCommand(VerifyCommand left, VerifyCommand right)
        => string.Equals(left.Command.Trim(), right.Command.Trim(), StringComparison.Ordinal)
            && string.Equals(left.WorkingSubdir.Trim('/'), right.WorkingSubdir.Trim('/'), StringComparison.OrdinalIgnoreCase);

    private static string Describe(VerifyCommand command)
        => command.WorkingSubdir.Length == 0 ? command.Command : $"({command.WorkingSubdir}) {command.Command}";

    [GeneratedRegex(@"^(?<prefix>.*\brun)\s+[A-Za-z0-9:_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RunScript();
}
