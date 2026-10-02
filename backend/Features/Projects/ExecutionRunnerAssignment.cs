namespace AgentStudio.Projects;

/// <summary>Canonical validation for every public execution-runner mutation.</summary>
public static class ExecutionRunnerAssignment
{
    public static string? NormalizeAndValidate(string? value, ClientIdentityStore clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized == null
            || string.Equals(normalized, "local", StringComparison.OrdinalIgnoreCase))
            return null;

        var classCapability = ExecutionLocations.RequiredClassCapability(normalized);
        if (classCapability is not null)
        {
            var className = classCapability["platform:".Length..];
            if (className is not ("linux" or "windows" or "macos"))
                throw new ArgumentException(
                    "executionRunner class must be linux, windows, or macos",
                    nameof(value));
            return ExecutionLocations.ClassPrefix + className;
        }

        var identity = clients.Find(normalized);
        if (identity == null
            || identity.Kind == ClientIdentityKind.Retired
            || identity.Kind != ClientIdentityKind.Service)
        {
            throw new ArgumentException(
                $"executionRunner '{normalized}' must identify a registered active service client",
                nameof(value));
        }

        return identity.Id;
    }
}
