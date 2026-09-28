namespace AgentStudio.Shared;

public static class PickupModes
{
    public const string Auto = "auto";
    public const string Manual = "manual";
    public const string Paused = "paused";

    public static readonly IReadOnlyList<string> All = [Auto, Manual, Paused];

    public static bool IsValid(string? value) =>
        All.Contains(value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Auto => Auto,
            Paused => Paused,
            _ => Manual,
        };

    public static string FromRunnerMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "auto-continuous" or "auto-single" or Auto => Auto,
            Paused => Paused,
            _ => Manual,
        };

    public static string ToRunnerMode(string? value) =>
        Normalize(value) switch
        {
            Auto => "auto-continuous",
            Paused => "paused",
            _ => "manual",
        };
}

public static class ExecutionLocations
{
    public const string Local = "local";
    public const string ClassPrefix = "class:";

    public static string? RequiredClassCapability(string? location)
    {
        if (location is null || !location.StartsWith(ClassPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var name = location[ClassPrefix.Length..].Trim().ToLowerInvariant();
        return name.Length == 0 ? null : $"platform:{name}";
    }

    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), Local, StringComparison.OrdinalIgnoreCase)
                ? Local
                : value.Trim();
}

/// <summary>
/// Resolves the two independent project execution dimensions and migrates the
/// legacy composite values accepted in <see cref="ProjectSettings.ExecutionRunner"/>.
/// Missing pickup defaults to manual; missing placement defaults to local.
/// </summary>
public static class ProjectExecutionPolicy
{
    public static bool HasProjectSlot(int occupiedTasks, int maxParallelism)
        => occupiedTasks < Math.Max(1, maxParallelism);

    public static string ResolvePickupMode(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (PickupModes.IsValid(settings.PickupMode))
            return PickupModes.Normalize(settings.PickupMode);

        var legacy = settings.ExecutionRunner?.Trim();
        if (string.Equals(legacy, "auto-continuous", StringComparison.OrdinalIgnoreCase))
            return PickupModes.Auto;
        if (string.Equals(legacy, PickupModes.Manual, StringComparison.OrdinalIgnoreCase))
            return PickupModes.Manual;
        if (string.Equals(legacy, PickupModes.Paused, StringComparison.OrdinalIgnoreCase))
            return PickupModes.Paused;
        if (IsLegacyRemoteRunner(legacy) && settings.RemoteExecutionEnabled)
            return PickupModes.Auto;

        var runnerMode = string.IsNullOrWhiteSpace(settings.RunnerMode)
            ? settings.DesiredRunnerMode
            : settings.RunnerMode;
        if (!string.IsNullOrWhiteSpace(runnerMode))
            return PickupModes.FromRunnerMode(runnerMode);

        return PickupModes.Manual;
    }

    public static string ResolveExecutionLocation(ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.ExecutionLocation))
            return ExecutionLocations.Normalize(settings.ExecutionLocation);
        if (!settings.RemoteExecutionEnabled)
            return ExecutionLocations.Local;

        var legacy = settings.ExecutionRunner?.Trim();
        return IsLegacyRemoteRunner(legacy)
            ? ExecutionLocations.Normalize(legacy)
            : ExecutionLocations.Local;
    }

    public static bool AllowsAutomaticPickup(ProjectSettings settings) =>
        ResolvePickupMode(settings) == PickupModes.Auto;

    public static bool IsLocalExecution(ProjectSettings settings) =>
        ResolveExecutionLocation(settings) == ExecutionLocations.Local;

    /// <summary>
    /// A class location offers work to remote runners for later capability
    /// admission. This preliminary routing result is not a lease permit.
    /// </summary>
    public static bool IsAssignedRemote(ProjectSettings settings, string? runnerId, string? runnerName = null)
    {
        var location = ResolveExecutionLocation(settings);
        if (location == ExecutionLocations.Local) return false;
        if (ExecutionLocations.RequiredClassCapability(location) is not null) return true;
        return string.Equals(location, runnerId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(location, runnerName, StringComparison.OrdinalIgnoreCase);
    }

    public static ProjectSettings Migrate(ProjectSettings settings)
    {
        var pickupMode = ResolvePickupMode(settings);
        var executionLocation = ResolveExecutionLocation(settings);
        var legacyRunner = executionLocation == ExecutionLocations.Local
            ? !settings.RemoteExecutionEnabled && IsLegacyRemoteRunner(settings.ExecutionRunner)
                ? settings.ExecutionRunner!.Trim()
                : null
            : executionLocation;
        var migratesLegacyRemote = string.IsNullOrWhiteSpace(settings.PickupMode)
                                   && settings.RemoteExecutionEnabled
                                   && IsLegacyRemoteRunner(settings.ExecutionRunner);
        var runnerMode = IsLegacyComposite(settings.ExecutionRunner)
                         || migratesLegacyRemote
                         || string.IsNullOrWhiteSpace(settings.RunnerMode)
            ? PickupModes.ToRunnerMode(pickupMode)
            : settings.RunnerMode;
        var desiredRunnerMode = IsLegacyComposite(settings.ExecutionRunner)
                                || migratesLegacyRemote
                                || (string.IsNullOrWhiteSpace(settings.RunnerMode)
                                    && string.IsNullOrWhiteSpace(settings.DesiredRunnerMode)
                                    && executionLocation != ExecutionLocations.Local)
            ? PickupModes.ToRunnerMode(pickupMode)
            : settings.DesiredRunnerMode;

        return settings with
        {
            PickupMode = pickupMode,
            ExecutionLocation = executionLocation,
            RunnerMode = runnerMode,
            DesiredRunnerMode = desiredRunnerMode,
            ExecutionRunner = legacyRunner,
            RemoteExecutionEnabled = executionLocation != ExecutionLocations.Local,
        };
    }

    public static bool IsLegacyComposite(string? value) =>
        string.Equals(value, "auto-continuous", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, PickupModes.Manual, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, PickupModes.Paused, StringComparison.OrdinalIgnoreCase);

    public static bool IsLegacyRemoteRunner(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, ExecutionLocations.Local, StringComparison.OrdinalIgnoreCase)
        && !IsLegacyComposite(value);
}
