using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AgentStudio.Orchestrator;

/// <summary>
/// Canonical context key for one orchestrator session (multichat concept,
/// AGT-1917 Phase 1; Dossier scope added AGT-2725). Four shapes exist:
/// <c>global</c>, <c>project:&lt;PROJ-ID&gt;</c>,
/// <c>workbench:&lt;PROJ-ID&gt;/&lt;DOSSIER-KEY&gt;</c> and
/// <c>task:&lt;PROJ-ID&gt;/&lt;TASK-KEY&gt;</c>.
///
/// <para>
/// The key doubles as the registry folder name after <see cref="Encode"/>,
/// which maps every character outside <c>[A-Za-z0-9_-]</c> to a <c>~XX</c>
/// UTF-8 hex escape. That keeps folder names reversible, collision-free and
/// Windows-safe (no <c>:</c>, <c>/</c>, trailing dots, reserved characters).
/// Example: <c>task:AGT/AGT-1917</c> becomes <c>task~3AAGT~2FAGT-1917</c>.
/// </para>
/// </summary>
public sealed class OrchestratorContextKey : IEquatable<OrchestratorContextKey>
{
    public const string GlobalKind = "global";
    public const string ProjectKind = "project";
    public const string WorkbenchKind = "workbench";
    public const string TaskKind = "task";

    /// <summary>The canonical string form, e.g. <c>task:AGT/AGT-1917</c>.</summary>
    public string Value { get; }

    /// <summary>
    /// One of <see cref="GlobalKind"/> / <see cref="ProjectKind"/> /
    /// <see cref="WorkbenchKind"/> / <see cref="TaskKind"/>.
    /// </summary>
    public string Kind { get; }

    /// <summary>Project id for project / workbench / task contexts; null for global.</summary>
    public string? ProjectId { get; }

    /// <summary>Task key for task contexts; null otherwise.</summary>
    public string? TaskKey { get; }

    /// <summary>Dossier (Workbench) key for workbench contexts; null otherwise.</summary>
    public string? WorkbenchKey { get; }

    /// <summary>The singleton key for the app-wide orchestrator session.</summary>
    public static readonly OrchestratorContextKey Global = new(GlobalKind, GlobalKind, null, null, null);

    private OrchestratorContextKey(string value, string kind, string? projectId, string? taskKey, string? workbenchKey)
    {
        Value = value;
        Kind = kind;
        ProjectId = projectId;
        TaskKey = taskKey;
        WorkbenchKey = workbenchKey;
    }

    public bool IsGlobal => Kind == GlobalKind;

    /// <summary>
    /// Parse a canonical context key. Strict on purpose: kinds are
    /// lower-case, no surrounding whitespace, exactly one <c>/</c> between
    /// project id and task key. Ids are otherwise opaque (spaces are fine,
    /// project display names contain them today).
    /// </summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out OrchestratorContextKey? key)
    {
        key = null;
        if (string.IsNullOrEmpty(raw) || raw != raw.Trim()) return false;

        // Self-healing (AGT-2165): keys arriving over HTTP catch-all routes can
        // still carry percent-escapes when a frontend encodeURIComponent'ed the
        // whole key (task%3A...%2F...). ASP.NET leaves those escapes in the
        // route value, the strict grammar below rejects them, and the chat died
        // with "Invalid orchestrator context key". Unescape once and re-enter;
        // canonical keys never contain '%', so valid input is unaffected. The
        // parsed key's Value is the canonical decoded form either way - callers
        // must use key.Value (not their raw input) for registry paths.
        if (raw.Contains('%'))
        {
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(raw); }
            catch (UriFormatException) { return false; }
            if (unescaped != raw) return TryParse(unescaped, out key);
            return false;
        }

        if (raw == GlobalKind)
        {
            key = Global;
            return true;
        }

        const string projectPrefix = ProjectKind + ":";
        if (raw.StartsWith(projectPrefix, StringComparison.Ordinal))
        {
            var id = raw[projectPrefix.Length..];
            if (!IsValidIdPart(id)) return false;
            key = new OrchestratorContextKey(raw, ProjectKind, id, null, null);
            return true;
        }

        const string workbenchPrefix = WorkbenchKind + ":";
        if (raw.StartsWith(workbenchPrefix, StringComparison.Ordinal))
        {
            var rest = raw[workbenchPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) return false;
            var projectId = rest[..slash];
            var workbenchKey = rest[(slash + 1)..];
            if (!IsValidIdPart(projectId) || !IsValidIdPart(workbenchKey)) return false;
            key = new OrchestratorContextKey(raw, WorkbenchKind, projectId, null, workbenchKey);
            return true;
        }

        const string taskPrefix = TaskKind + ":";
        if (raw.StartsWith(taskPrefix, StringComparison.Ordinal))
        {
            var rest = raw[taskPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) return false;
            var projectId = rest[..slash];
            var taskKey = rest[(slash + 1)..];
            if (!IsValidIdPart(projectId) || !IsValidIdPart(taskKey)) return false;
            key = new OrchestratorContextKey(raw, TaskKind, projectId, taskKey, null);
            return true;
        }

        return false;
    }

    private static bool IsValidIdPart(string part)
    {
        if (string.IsNullOrWhiteSpace(part) || part != part.Trim()) return false;
        if (part.Contains('/') || part.Contains('\\')) return false;
        foreach (var c in part)
        {
            if (char.IsControl(c)) return false;
        }
        return true;
    }

    /// <summary>Filesystem-safe folder name for this key (reversible via <see cref="TryDecode"/>).</summary>
    public string Encode() => Encode(Value);

    internal static string Encode(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                sb.Append(c);
            else
                sb.Append('~').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reverse of <see cref="Encode"/> plus a <see cref="TryParse"/> pass,
    /// so a decoded folder name that is not a valid context key (stray
    /// directory, torn write) is rejected instead of round-tripping garbage.
    /// </summary>
    public static bool TryDecode(string? encoded, [NotNullWhen(true)] out OrchestratorContextKey? key)
    {
        key = null;
        if (string.IsNullOrEmpty(encoded)) return false;
        var bytes = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '~')
            {
                if (i + 2 >= encoded.Length) return false;
                if (!byte.TryParse(encoded.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return false;
                bytes.Add(b);
                i += 2;
            }
            else if (c < 0x80 && (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                bytes.Add((byte)c);
            }
            else
            {
                return false;
            }
        }
        return TryParse(Encoding.UTF8.GetString(bytes.ToArray()), out key);
    }

    public bool Equals(OrchestratorContextKey? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as OrchestratorContextKey);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}
