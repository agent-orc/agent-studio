using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestratorEngine;

public interface IEngineArtifactSource
{
    Task<ArtifactContentDto> GetArtifactContentAsync(string runId, string artifactId, CancellationToken ct);
}

/// <summary>
/// Reads only Task Server artefacts by immutable IDs. The engine verifies the
/// whole source digest, then selects a redacted and bounded text window. The
/// model receives the window and a digest reference, never a filesystem path.
/// </summary>
public sealed class EngineArtifactReader(IEngineArtifactSource source)
{
    public const int MaxSourceBytes = 1024 * 1024;
    public const int MaxWindowBytes = 8 * 1024;
    public const int MaxWindowLines = 80;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex Credential = new(
        @"(?i)(?:sk-[a-z0-9_-]{12,}|gh[opusr]_[a-z0-9_]{12,}|(?:api[_-]?key|access[_-]?token|authorization|password|secret)\s*[:=]\s*\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<BoundedArtifactWindow> ReadWindowAsync(string runId, string artifactId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        var artifact = await source.GetArtifactContentAsync(runId, artifactId, ct);
        if (artifact.RunId != runId || artifact.ArtifactId != artifactId || artifact.PointerOnly)
            throw new InvalidDataException("The requested immutable artefact is unavailable.");
        if (artifact.SizeBytes > MaxSourceBytes || artifact.ContentBase64.Length > MaxSourceBytes * 2)
            return new(new("artifact", $"artifact:{runId}/{artifactId}", artifact.Sha256,
                AccessClass: "operator"), "", ["oversize-source"]);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(artifact.ContentBase64); }
        catch (FormatException exception) { throw new InvalidDataException("Artefact content is invalid.", exception); }
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.Length != artifact.SizeBytes || !string.Equals(digest, artifact.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artefact content digest does not match its immutable record.");
        if (!artifact.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && artifact.MediaType is not "application/json" and not "application/x-ndjson")
            return new(new("artifact", $"artifact:{runId}/{artifactId}", "sha256:" + digest,
                AccessClass: "operator"), "", ["non-text-source"]);
        string content;
        try { content = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Artefact is not UTF-8 text.", exception); }
        var lines = content.Split('\n');
        var first = Math.Max(0, lines.Length - MaxWindowLines);
        var selected = lines.Skip(first).Select(line => Credential.Replace(line.TrimEnd('\r'), "[omitted: secret]"));
        var window = string.Join('\n', selected);
        var reasons = new List<string>();
        if (first > 0) reasons.Add("line-cap");
        if (window.Contains("[omitted: secret]", StringComparison.Ordinal)) reasons.Add("secret");
        if (Encoding.UTF8.GetByteCount(window) > MaxWindowBytes)
        {
            window = window[^Math.Min(window.Length, MaxWindowBytes)..];
            while (Encoding.UTF8.GetByteCount(window) > MaxWindowBytes)
                window = window[1..];
            reasons.Add("byte-cap");
        }
        var reference = new GroundsSourceRefV1("artifact-window", $"artifact:{runId}/{artifactId}",
            "sha256:" + digest, $"lines:{first + 1}-{lines.Length}", "operator");
        return new(reference, window, reasons);
    }
}

public sealed record BoundedArtifactWindow(GroundsSourceRefV1 Source, string Content,
    IReadOnlyList<string> OmissionReasons);
