namespace AgentRunner.RunTargets;

/// <summary>Reads one secret value by reference, on the run host, at the moment it is needed.</summary>
public interface IRunTargetSecretSource
{
    /// <returns>The value, or null when the reference is unknown on this host.</returns>
    Task<string?> ReadAsync(string secretRef, CancellationToken ct);
}

/// <summary>
/// The AGT-3176 host secret store: one protected file per reference. The
/// secret provider of IP-6 replaces this source without touching callers.
/// </summary>
public sealed class FileRunTargetSecretSource(string root) : IRunTargetSecretSource
{
    public async Task<string?> ReadAsync(string secretRef, CancellationToken ct)
    {
        if (!RunTargetNames.IsSecretRef(secretRef)) return null;
        var path = Path.Combine(root, secretRef);
        if (!File.Exists(path)) return null;
        return (await File.ReadAllTextAsync(path, ct)).TrimEnd('\r', '\n');
    }
}

/// <summary>
/// Resolved secret values of one convergence step. They live in this object's
/// memory only: callers pass <see cref="Environment"/> to the compose process
/// and route every diagnostic through <see cref="Mask"/> before it is logged
/// or reported.
/// </summary>
public sealed class RunTargetResolvedSecrets
{
    private const string Redacted = "[redacted]";
    private readonly string[] _values;

    private RunTargetResolvedSecrets(IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> missing)
    {
        Environment = environment;
        Missing = missing;
        // Longest first, so a value that contains another is masked whole.
        _values = environment.Values.Where(value => value.Length >= 4).Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length).ToArray();
    }

    public IReadOnlyDictionary<string, string> Environment { get; }
    public IReadOnlyList<string> Missing { get; }

    public static async Task<RunTargetResolvedSecrets> ResolveAsync(
        IRunTargetSecretSource source, IReadOnlyList<string> secretRefs, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var secretRef in secretRefs)
        {
            var value = await source.ReadAsync(secretRef, ct);
            if (value is null) missing.Add(secretRef);
            else environment[RunTargetNames.SecretEnvironmentName(secretRef)] = value;
        }
        return new(environment, missing);
    }

    public string Mask(string text)
    {
        foreach (var value in _values)
            text = text.Replace(value, Redacted, StringComparison.Ordinal);
        return text;
    }
}
