using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Deploy;

/// <summary>Immutable provenance and content for a gate-built release artifact.</summary>
public sealed record RegisteredArtifact(string Project, string Commit, string Artifact, string Digest,
    string GateRunId, string Template, string BuilderIdentity, string Location, string SourceDigest,
    string Signature = "");

public class ReleaseArtifactRegistry
{
    private static readonly Regex DigestPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.Compiled);
    private static readonly Regex CommitPattern = new(@"\A[0-9a-f]{40}\z", RegexOptions.Compiled);
    private static readonly Regex ArtifactPattern = new(@"\A[A-Za-z0-9./:_-]+@sha256:[0-9a-f]{64}\z",
        RegexOptions.Compiled);
    private readonly IConfiguration _configuration;
    private readonly object _gate = new();

    public ReleaseArtifactRegistry(IConfiguration configuration) => _configuration = configuration;

    public bool PushVerified(string project, string commit, string artifact, string builderIdentity)
    {
        var sourceRoot = _configuration["Deploy:ReleaseEvidenceRoot"];
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Path.IsPathFullyQualified(sourceRoot)
            || !CommitPattern.IsMatch(commit) || !ArtifactPattern.IsMatch(artifact)
            || string.IsNullOrWhiteSpace(builderIdentity)) return false;
        try
        {
            var evidence = Path.Combine(sourceRoot, commit);
            using var promotion = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "promotion-record.json")));
            var gate = promotion.RootElement;
            var gateRunId = gate.GetProperty("gateRunId").GetString();
            if (string.IsNullOrWhiteSpace(gateRunId) || gateRunId.Length > 200) return false;
            var sourceDigest = File.ReadAllText(Path.Combine(evidence, "release.sha256")).Trim();
            if (!DigestPattern.IsMatch(sourceDigest)) return false;
            if (gate.GetProperty("status").GetString() != "promoted"
                || gate.GetProperty("gate").GetString() != "passed"
                || gate.GetProperty("candidateSha").GetString() != commit
                || !gate.GetProperty("atomicPush").GetBoolean()
                || gate.GetProperty("releasePackage").GetProperty("status").GetString() != "written"
                || gate.GetProperty("releasePackage").GetProperty("sha256").GetString() != sourceDigest
                || !File.ReadLines(Path.Combine(evidence, "full-gate.log"))
                    .Any(line => line == "PROMOTION_FULL_GATE=passed")) return false;
            using (var source = File.OpenRead(Path.Combine(evidence, "release.tar.gz")))
                if (!Convert.ToHexString(SHA256.HashData(source)).Equals(sourceDigest, StringComparison.OrdinalIgnoreCase))
                    return false;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "deploy-artifact.json")));
            var built = manifest.RootElement;
            if (built.GetProperty("project").GetString() != project
                || built.GetProperty("commit").GetString() != commit
                || built.GetProperty("artifact").GetString() != artifact
                || built.GetProperty("sourceSha256").GetString() != sourceDigest) return false;
            var digest = artifact.Split("@sha256:").Last();
            var isPackage = artifact == "release-package@sha256:" + sourceDigest;
            var unsigned = new RegisteredArtifact(project, commit, artifact, digest, gateRunId,
                isPackage ? "static" : "container", builderIdentity, isPackage ? "built-in" : "external", sourceDigest);
            lock (_gate)
            {
                var record = unsigned with { Signature = Sign(unsigned, createKey: true) };
                var path = RecordPath(project, commit, digest);
                if (File.Exists(path)) return JsonSerializer.Deserialize<RegisteredArtifact>(File.ReadAllText(path)) == record
                    && Verify(record);
                if (isPackage)
                {
                    var blob = BlobPath(digest);
                    Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                    if (!File.Exists(blob))
                    {
                        var temporaryBlob = blob + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.Copy(Path.Combine(evidence, "release.tar.gz"), temporaryBlob);
                            if (!MatchesDigest(temporaryBlob, digest)) return false;
                            try { File.Move(temporaryBlob, blob, false); }
                            catch (IOException) when (File.Exists(blob))
                            {
                                if (!MatchesDigest(blob, digest)) return false;
                            }
                        }
                        finally { if (File.Exists(temporaryBlob)) File.Delete(temporaryBlob); }
                    }
                    if (!MatchesDigest(blob, digest)) return false;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(record));
                    File.Move(temporary, path, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return false;
        }
    }

    public RegisteredArtifact? Verified(string project, string commit, string artifact, string template)
    {
        try { return VerifiedForDeployment(project, commit, artifact, template); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Keep temporary read failures visible to a deploy claim so it can retry.</summary>
    public virtual RegisteredArtifact? VerifiedForDeployment(string project, string commit, string artifact,
        string template)
    {
        if (!CommitPattern.IsMatch(commit) || !ArtifactPattern.IsMatch(artifact)) return null;
        var digest = artifact.Split("@sha256:").Last();
        if (!DigestPattern.IsMatch(digest)) return null;
        try
        {
            var path = RecordPath(project, commit, digest);
            if (!File.Exists(path)) return null;
            var record = JsonSerializer.Deserialize<RegisteredArtifact>(File.ReadAllText(path));
            return record is not null && record.Project == project && record.Commit == commit
                && record.Artifact == artifact && record.Template == template && Verify(record) ? record : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    public RegisteredArtifact? VerifiedForArtifact(string project, string commit, string artifact)
        => Verified(project, commit, artifact,
            artifact.StartsWith("release-package@sha256:", StringComparison.Ordinal) ? "static" : "container");

    public string? VerifiedBlob(RegisteredArtifact record)
        => record.Location == "built-in" && Verify(record) ? BlobPath(record.Digest) : null;

    private bool Verify(RegisteredArtifact record)
        => record.Digest is not null && DigestPattern.IsMatch(record.Digest) && SignatureMatches(record)
            && !string.IsNullOrWhiteSpace(record.GateRunId)
            && !string.IsNullOrWhiteSpace(record.BuilderIdentity)
            && (record.Location == "external" && record.Template == "container"
                || record.Location == "built-in" && record.Template == "static"
                    && record.Digest == record.SourceDigest && MatchesDigest(BlobPath(record.Digest), record.Digest));

    private static bool MatchesDigest(string path, string digest)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(digest, StringComparison.OrdinalIgnoreCase);
    }

    private bool SignatureMatches(RegisteredArtifact record)
    {
        if (record.Signature is null || record.Signature.Length != 64 || !File.Exists(KeyPath())) return false;
        var expected = Sign(record with { Signature = "" }, createKey: false);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(record.Signature));
    }

    private string Sign(RegisteredArtifact unsigned, bool createKey)
    {
        var path = KeyPath();
        if (createKey && !File.Exists(path))
        {
            Directory.CreateDirectory(Root());
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, RandomNumberGenerator.GetBytes(32));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                try { File.Move(temporary, path, false); }
                catch (IOException) when (File.Exists(path))
                {
                    if (File.ReadAllBytes(path).Length != 32) throw;
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new InvalidDataException("Registry signing key is invalid.");
        return Convert.ToHexString(HMACSHA256.HashData(key,
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(unsigned)))).ToLowerInvariant();
    }

    private string RecordPath(string project, string commit, string digest)
        => Path.Combine(Root(), "records", AgentStudio.Projects.ProjectEnvironmentService.SafeProjectName(project), commit,
            digest + ".json");
    private string BlobPath(string digest) => Path.Combine(Root(), "blobs", "sha256", digest);
    private string KeyPath() => Path.Combine(Root(), "provenance.key");
    private string Root() => Path.Combine(_configuration["TaskRepository"]
        ?? throw new InvalidOperationException("TaskRepository is required."), "release-registry");
}
