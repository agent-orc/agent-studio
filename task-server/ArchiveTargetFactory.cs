using AgentStudio.Retention;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private LocalDirectoryTarget LocalArchiveTarget()
        => new(_options.ResolveRetentionArchivePath());

    private S3Target S3ArchiveTarget()
    {
        if (!Uri.TryCreate(_options.ArchiveS3Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("The S3 archive target requires an absolute http(s) ARCHIVE_S3_ENDPOINT.");
        if (string.IsNullOrWhiteSpace(_options.ArchiveS3Bucket))
            throw new InvalidOperationException("The S3 archive target requires ARCHIVE_S3_BUCKET.");
        if (string.IsNullOrWhiteSpace(_options.ArchiveS3CredentialsFile))
            throw new InvalidOperationException("The S3 archive target requires ARCHIVE_S3_CREDENTIALS_FILE.");
        return new S3Target(S3TargetOptions.FromSecretFile(
            endpoint,
            _options.ArchiveS3Bucket,
            _options.ArchiveS3Prefix,
            Path.GetFullPath(_options.ArchiveS3CredentialsFile),
            _options.ArchiveS3PathStyle,
            _options.ArchiveS3ServerSideChecksum,
            _options.ArchiveS3Region));
    }

    private bool S3ArchiveConfigured
        => Uri.TryCreate(_options.ArchiveS3Endpoint, UriKind.Absolute, out var endpoint)
           && endpoint.Scheme is "http" or "https"
           && string.IsNullOrEmpty(endpoint.UserInfo)
           && !string.IsNullOrWhiteSpace(_options.ArchiveS3Bucket)
           && !string.IsNullOrWhiteSpace(_options.ArchiveS3CredentialsFile)
           && File.Exists(Path.GetFullPath(_options.ArchiveS3CredentialsFile));

    private IArchiveTarget TargetFor(ArchiveObjectReference reference)
        => reference.Target.ToLowerInvariant() switch
        {
            "local" => LocalArchiveTarget(),
            "s3" => S3ArchiveTarget(),
            _ => throw new InvalidDataException($"Unknown archive target '{reference.Target}'."),
        };

    private static string ArchiveObjectKey(string project, string taskKey, DateTimeOffset archivedAt, string fileName)
        => $"{SanitizeArchiveSegment(project)}/{SanitizeArchiveSegment(taskKey)}/" +
           $"{archivedAt.UtcDateTime:yyyyMMddTHHmmssfffZ}/{fileName}";
}
