using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentStudio.Secrets;

public enum SecretScopeKind
{
    Workspace,
    Project,
    Environment
}

/// <summary>
/// The scope a secret belongs to: the workspace, one project, or one
/// environment of a project. The canonical key is <c>workspace</c>,
/// <c>&lt;project&gt;</c> or <c>&lt;project&gt;/&lt;environment&gt;</c>, the
/// same text that sits between <c>secret://</c> and the name of a reference.
/// </summary>
public sealed record SecretScope
{
    public const string WorkspaceKey = "workspace";

    private static readonly Regex ProjectPattern = new("^[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,126}[A-Za-z0-9._-])?$", RegexOptions.Compiled);
    private static readonly Regex EnvironmentPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private SecretScope(SecretScopeKind kind, string? project, string? environment)
    {
        Kind = kind;
        Project = project;
        Environment = environment;
    }

    public static SecretScope Workspace { get; } = new(SecretScopeKind.Workspace, null, null);

    public SecretScopeKind Kind { get; }
    public string? Project { get; }
    public string? Environment { get; }

    public string Key => Kind switch
    {
        SecretScopeKind.Workspace => WorkspaceKey,
        SecretScopeKind.Project => Project!,
        _ => Project + "/" + Environment
    };

    /// <summary>Parent scopes in inheritance order, the scope itself first.</summary>
    public IReadOnlyList<SecretScope> InheritanceChain() => Kind switch
    {
        SecretScopeKind.Workspace => [this],
        SecretScopeKind.Project => [this, Workspace],
        _ => [this, ForProject(Project!), Workspace]
    };

    public static SecretScope ForProject(string project)
    {
        if (!IsValidProject(project))
            throw new SecretValidationException("invalid-scope", "Project names use letters, digits, internal spaces, dots, underscores or hyphens and cannot be 'workspace'.");
        return new(SecretScopeKind.Project, project, null);
    }

    public static SecretScope ForEnvironment(string project, string environment)
    {
        var parent = ForProject(project);
        if (!EnvironmentPattern.IsMatch(environment ?? string.Empty))
            throw new SecretValidationException("invalid-scope", "Environment ids use lowercase letters, digits, dots, underscores or hyphens.");
        return new(SecretScopeKind.Environment, parent.Project, environment);
    }

    public static SecretScope Parse(string? key)
        => TryParse(key, out var scope)
            ? scope
            : throw new SecretValidationException("invalid-scope", "Scope must be 'workspace', '<project>' or '<project>/<environment>'.");

    public static bool TryParse(string? key, [NotNullWhen(true)] out SecretScope? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var parts = key.Split('/');
        if (parts.Length == 1 && parts[0] == WorkspaceKey) { scope = Workspace; return true; }
        if (parts.Length == 1 && IsValidProject(parts[0])) { scope = new(SecretScopeKind.Project, parts[0], null); return true; }
        if (parts.Length == 2 && IsValidProject(parts[0]) && EnvironmentPattern.IsMatch(parts[1]))
        {
            scope = new(SecretScopeKind.Environment, parts[0], parts[1]);
            return true;
        }
        return false;
    }

    public bool Equals(SecretScope? other)
        => other is not null && Kind == other.Kind
           && string.Equals(Project, other.Project, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Environment, other.Environment, StringComparison.Ordinal);

    public override int GetHashCode()
        => HashCode.Combine(Kind, Project?.ToLowerInvariant(), Environment);

    public override string ToString() => Key;

    private static bool IsValidProject(string? project)
        => project is not null && ProjectPattern.IsMatch(project)
           && !string.Equals(project, WorkspaceKey, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A pointer to a secret value: <c>secret://&lt;scope&gt;/&lt;name&gt;[@version]</c>.
/// Configuration, deploy declarations and logs carry references, never values.
/// </summary>
public sealed record SecretReference(SecretScope Scope, string Name, int? Version = null)
{
    public const string Prefix = "secret://";

    private static readonly Regex NamePattern = new("^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    public static string ValidateName(string? name)
        => IsValidName(name)
            ? name!
            : throw new SecretValidationException("invalid-name", "Secret names start with a letter and use letters, digits, dots, underscores or hyphens (up to 64).");

    public static SecretReference Parse(string? text)
        => TryParse(text, out var reference)
            ? reference
            : throw new SecretValidationException("invalid-reference", "A reference has the form secret://<scope>/<name>[@version].");

    public static bool TryParse(string? text, [NotNullWhen(true)] out SecretReference? reference)
    {
        reference = null;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal) || text.Length > 512) return false;
        var body = text[Prefix.Length..];
        int? version = null;
        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            if (!int.TryParse(body[(at + 1)..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                return false;
            version = parsed;
            body = body[..at];
        }
        var slash = body.LastIndexOf('/');
        if (slash <= 0) return false;
        var name = body[(slash + 1)..];
        if (!IsValidName(name) || !SecretScope.TryParse(body[..slash], out var scope)) return false;
        reference = new SecretReference(scope, name, version);
        return true;
    }

    public SecretReference Unpinned() => this with { Version = null };

    public override string ToString()
        => Prefix + Scope.Key + "/" + Name + (Version is { } version ? "@" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
}

public class SecretValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
