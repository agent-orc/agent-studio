using Microsoft.AspNetCore.Hosting;

namespace AgentStudio.TaskServer;

public sealed class TaskServerBootstrapOptions
{
    public const string NoAuthentication = "none";
    public const string BearerAuthentication = "bearer";

    private TaskServerBootstrapOptions(
        string listenUrl,
        string storePath,
        string backupPath,
        string archivePath,
        string authenticationMode,
        string? authenticationToken,
        string? studioAuthenticationToken,
        string? engineAuthenticationToken,
        string? bootstrapRunnerId,
        string? bootstrapRunnerAuthenticationToken,
        string? legacyRunnerAuthenticationToken,
        bool usesLegacyRoleAuthentication)
    {
        ListenUrl = listenUrl;
        StorePath = storePath;
        BackupPath = backupPath;
        ArchivePath = archivePath;
        AuthenticationMode = authenticationMode;
        AuthenticationToken = authenticationToken;
        StudioAuthenticationToken = studioAuthenticationToken;
        EngineAuthenticationToken = engineAuthenticationToken;
        BootstrapRunnerId = bootstrapRunnerId;
        BootstrapRunnerAuthenticationToken = bootstrapRunnerAuthenticationToken;
        LegacyRunnerAuthenticationToken = legacyRunnerAuthenticationToken;
        UsesLegacyRoleAuthentication = usesLegacyRoleAuthentication;
    }

    public string ListenUrl { get; }
    public string StorePath { get; }
    public string BackupPath { get; }
    public string ArchivePath { get; }
    public string AuthenticationMode { get; }
    public string? AuthenticationToken { get; }
    public string? StudioAuthenticationToken { get; }
    public string? EngineAuthenticationToken { get; }
    public string? BootstrapRunnerId { get; }
    public string? BootstrapRunnerAuthenticationToken { get; }
    public string? LegacyRunnerAuthenticationToken { get; }
    public bool UsesSharedBearerAuthentication =>
        string.Equals(AuthenticationMode, BearerAuthentication, StringComparison.Ordinal);
    public bool UsesLegacyRoleAuthentication { get; }
    public bool RequiresAuthentication =>
        UsesSharedBearerAuthentication || UsesLegacyRoleAuthentication;

    public static TaskServerBootstrapOptions Load(IConfiguration configuration)
    {
        var listenUrl = First(
            configuration[WebHostDefaults.ServerUrlsKey],
            configuration["LISTEN_URL"],
            configuration[$"{TaskServerOptions.SectionName}:ListenUrl"],
            "http://127.0.0.1:5071")
            .Trim();
        var storePath = First(
            configuration["STORE_PATH"],
            configuration[$"{TaskServerOptions.SectionName}:DataDirectory"],
            "data")
            .Trim();
        var backupPath = FirstOrNull(
            configuration["BACKUP_PATH"],
            configuration[$"{TaskServerOptions.SectionName}:BackupDirectory"]);
        var archivePath = FirstOrNull(
            configuration["ARCHIVE_PATH"],
            configuration[$"{TaskServerOptions.SectionName}:RetentionArchivePath"]);
        var authenticationMode = First(
                configuration["AUTH"],
                configuration["AUTH_MODE"],
                configuration[$"{TaskServerOptions.SectionName}:AuthMode"],
                NoAuthentication)
            .Trim()
            .ToLowerInvariant();
        var usesLegacyRoleAuthentication = configuration.GetValue<bool>(
            $"{TaskServerOptions.SectionName}:RequireAuthentication");

        if (authenticationMode is not (NoAuthentication or BearerAuthentication))
            throw new InvalidOperationException(
                "AUTH must be 'none' or 'bearer'.");

        var token = ReadCredential(
            configuration,
            "AUTH_TOKEN",
            $"{TaskServerOptions.SectionName}:AuthToken",
            "AUTH_TOKEN_FILE",
            $"{TaskServerOptions.SectionName}:AuthTokenFile");
        var studioToken = ReadCredential(
            configuration,
            "STUDIO_AUTH_TOKEN",
            $"{TaskServerOptions.SectionName}:StudioAuthToken",
            "STUDIO_AUTH_TOKEN_FILE",
            $"{TaskServerOptions.SectionName}:StudioAuthTokenFile");
        var engineToken = ReadCredential(
            configuration,
            "ENGINE_AUTH_TOKEN",
            $"{TaskServerOptions.SectionName}:EngineAuthToken",
            "ENGINE_AUTH_TOKEN_FILE",
            $"{TaskServerOptions.SectionName}:EngineAuthTokenFile");
        var bootstrapRunnerId = FirstOrNull(configuration["BOOTSTRAP_RUNNER_ID"]);
        var bootstrapRunnerToken = ReadCredential(
            configuration,
            "BOOTSTRAP_RUNNER_AUTH_TOKEN",
            $"{TaskServerOptions.SectionName}:BootstrapRunnerAuthToken",
            "BOOTSTRAP_RUNNER_AUTH_TOKEN_FILE",
            $"{TaskServerOptions.SectionName}:BootstrapRunnerAuthTokenFile");
        if ((bootstrapRunnerId is null) != (bootstrapRunnerToken is null))
            throw new InvalidOperationException(
                "BOOTSTRAP_RUNNER_ID and BOOTSTRAP_RUNNER_AUTH_TOKEN(_FILE) must be configured together.");

        studioToken ??= token;
        if (usesLegacyRoleAuthentication)
            studioToken ??= FirstOrNull(
                configuration[$"{TaskServerOptions.SectionName}:StudioBearerToken"]);
        var legacyRunnerToken = usesLegacyRoleAuthentication
            ? FirstOrNull(configuration[$"{TaskServerOptions.SectionName}:RunnerBearerToken"])
            : null;

        foreach (var configuredToken in new[] { token, studioToken, engineToken, bootstrapRunnerToken, legacyRunnerToken })
            if (configuredToken is not null && configuredToken.Length < 32)
                throw new InvalidOperationException(
                    "Configured bearer credentials must contain at least 32 characters.");
        if (authenticationMode == NoAuthentication && token is not null)
            throw new InvalidOperationException(
                "AUTH_TOKEN and AUTH_TOKEN_FILE are invalid when AUTH=none.");
        if (authenticationMode == NoAuthentication
            && !usesLegacyRoleAuthentication
            && (studioToken is not null
                || engineToken is not null
                || bootstrapRunnerToken is not null))
            throw new InvalidOperationException(
                "Principal bootstrap credentials are invalid when AUTH=none.");
        if (usesLegacyRoleAuthentication
            && (studioToken is null || legacyRunnerToken is null))
            throw new InvalidOperationException(
                "Deprecated role authentication requires StudioBearerToken and RunnerBearerToken.");
        if (usesLegacyRoleAuthentication
            && string.Equals(studioToken, legacyRunnerToken, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "StudioBearerToken and RunnerBearerToken must be distinct credentials.");
        if (usesLegacyRoleAuthentication
            && authenticationMode == BearerAuthentication)
        {
            throw new InvalidOperationException(
                "Configure either AUTH=bearer or legacy role authentication, not both.");
        }
        if (authenticationMode == NoAuthentication
            && !usesLegacyRoleAuthentication
            && !ListensOnlyOnLoopback(listenUrl))
        {
            throw new InvalidOperationException(
                "AUTH=none is permitted only when every LISTEN_URL address is loopback.");
        }

        var resolvedStorePath = ResolveStorePath(storePath);
        return new TaskServerBootstrapOptions(
            listenUrl,
            resolvedStorePath,
            backupPath is null
                ? Path.Combine(resolvedStorePath, "backups")
                : ResolvePath(backupPath),
            archivePath is null
                ? Path.Combine(resolvedStorePath, "archive")
                : ResolvePath(archivePath),
            authenticationMode,
            token,
            studioToken,
            engineToken,
            bootstrapRunnerId,
            bootstrapRunnerToken,
            legacyRunnerToken,
            usesLegacyRoleAuthentication);
    }

    private static string? ReadCredential(
        IConfiguration configuration,
        string directKey,
        string legacyDirectKey,
        string fileKey,
        string legacyFileKey)
    {
        var direct = FirstOrNull(configuration[directKey], configuration[legacyDirectKey]);
        var file = FirstOrNull(configuration[fileKey], configuration[legacyFileKey]);
        if (direct is not null && file is not null)
            throw new InvalidOperationException(
                $"Configure only one of {directKey} or {fileKey}.");
        if (file is null)
            return direct;
        var resolved = Path.GetFullPath(file);
        if (!File.Exists(resolved))
            throw new InvalidOperationException($"{fileKey} does not exist: {resolved}");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(resolved);
            if ((mode & (UnixFileMode.OtherRead
                         | UnixFileMode.OtherWrite
                         | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException(
                    $"{fileKey} must not be accessible to other users.");
        }
        return File.ReadAllText(resolved).Trim();
    }

    private static bool ListensOnlyOnLoopback(string value)
    {
        var addresses = value.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0) return false;
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !(uri.IsLoopback
                     || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        return true;
    }

    private static string ResolvePath(string path)
        => Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path));

    private static string ResolveStorePath(string path)
    {
        if (!string.Equals(path, "user-data", StringComparison.OrdinalIgnoreCase))
            return ResolvePath(path);
        var userData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(userData, "AgentStudio", "task-server");
    }

    private static string First(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? FirstOrNull(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
