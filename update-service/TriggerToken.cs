using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// The shared secret every state-changing route demands in
/// <see cref="TriggerToken.HeaderName"/>. The service never runs without one:
/// a configured <see cref="UpdateServiceOptions.TriggerToken"/> wins, otherwise
/// the token file is read, and when that is missing too the first start
/// generates a random token and writes it owner-only. The Studio backend reads
/// the same file to hand the token to the Update Center.
/// </summary>
public sealed class TriggerToken
{
    public const string HeaderName = "X-Update-Token";

    /// <summary>File name inside the service content root, beside appsettings.json.</summary>
    public const string DefaultFileName = ".update-token";

    private const int TokenBytes = 32;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly byte[] _digest;

    public TriggerToken(UpdateServiceOptions options, IHostEnvironment environment, ILogger<TriggerToken> logger)
    {
        FilePath = ResolveFilePath(options, environment.ContentRootPath);
        var configured = options.TriggerToken?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            Source = "configured";
        }
        else if (ReadFile(FilePath) is { } stored)
        {
            configured = stored;
            Source = "file";
            TightenUnixMode(FilePath, logger);
        }
        else
        {
            configured = Generate();
            configured = WriteNew(FilePath, configured) ?? configured;
            Source = "generated";
            logger.LogInformation("Generated the update trigger token at {TokenFile}", FilePath);
        }
        _digest = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    }

    /// <summary>Where the token file lives, whether or not the active token came from it.</summary>
    public string FilePath { get; }

    /// <summary><c>configured</c>, <c>file</c> or <c>generated</c>; never the value.</summary>
    public string Source { get; }

    /// <summary>Constant-time comparison of a presented header value with the active token.</summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(candidate, _digest);
    }

    public static string ResolveFilePath(UpdateServiceOptions options, string contentRoot)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(options.TriggerTokenFile)
            ? Path.Combine(contentRoot, DefaultFileName)
            : options.TriggerTokenFile);

    private static string Generate()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? ReadFile(string path)
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Writes a new owner-only file; returns the existing value when another start won the race.</summary>
    private static string? WriteNew(string path, string token)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path);
        try
        {
            using var stream = CreateOwnerOnly(path);
            stream.Write(Encoding.UTF8.GetBytes(token));
            stream.Flush(flushToDisk: true);
            return null;
        }
        catch (IOException) when (ReadFile(path) is { } existing)
        {
            return existing;
        }
    }

    private static FileStream CreateOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(FileMode.CreateNew,
                FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.None, 4096, FileOptions.None, WindowsOwnerOnly());
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = OwnerOnlyFile,
        });
    }

    private static void TightenUnixMode(string path, ILogger logger)
    {
        if (OperatingSystem.IsWindows() || File.GetUnixFileMode(path) == OwnerOnlyFile) return;
        File.SetUnixFileMode(path, OwnerOnlyFile);
        logger.LogWarning("Restricted the update trigger token file {TokenFile} to owner read and write", path);
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity WindowsOwnerOnly()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User ?? throw new InvalidOperationException("The service account has no Windows user identity."),
            FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}

/// <summary>Marks a route that refuses requests without the trigger token.</summary>
public sealed class TriggerTokenRequired;

public static class TriggerTokenEndpoints
{
    /// <summary>
    /// Refuses the request with 401 unless <see cref="TriggerToken.HeaderName"/>
    /// carries the active token. Runs before the body is read, so no request
    /// field (such as <c>force</c>) reaches the orchestrator unauthorized.
    /// </summary>
    public static RouteHandlerBuilder RequireTriggerToken(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new TriggerTokenRequired()).AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var token = http.RequestServices.GetRequiredService<TriggerToken>();
            if (token.Matches(http.Request.Headers[TriggerToken.HeaderName].FirstOrDefault()))
                return await next(context);
            http.RequestServices.GetRequiredService<ILogger<TriggerToken>>().LogWarning(
                "Refused {Method} {Path} from {Caller}: missing or wrong trigger token",
                http.Request.Method, http.Request.Path.Value, CallerDescription(http));
            return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
        });

    /// <summary>Who sent the request, for the service log. Never includes the token.</summary>
    public static string CallerDescription(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var origin = http.Request.Headers.Origin.FirstOrDefault();
        var agent = http.Request.Headers.UserAgent.FirstOrDefault();
        var parts = new List<string> { $"address={address}" };
        if (!string.IsNullOrEmpty(origin)) parts.Add($"origin={origin}");
        if (!string.IsNullOrEmpty(agent)) parts.Add($"userAgent={agent}");
        return string.Join(' ', parts);
    }
}
