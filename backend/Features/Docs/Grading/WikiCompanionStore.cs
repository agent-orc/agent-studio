using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Docs;

/// <summary>Outcome of writing a grading verdict into a companion sidecar.</summary>
public sealed record WikiCompanionWriteResult(bool Changed, string CompanionAbsPath);

/// <summary>
/// Reads, merges, and writes the <c>grading</c> block of a wiki page's
/// <c>&lt;source&gt;.meta.json</c> companion (AGT-2051). When a companion already
/// exists (e.g. from the drift-companion generator), only the <c>grading</c>
/// block is set and unrelated content metadata is preserved; a legacy
/// <c>agentReads</c> block is migrated to runtime state during that already
/// authorized write. When no companion exists, a schema-valid minimal companion
/// is created carrying the grading verdict. All writes are idempotent-friendly:
/// the caller can detect a no-op via <see cref="WikiCompanionWriteResult.Changed"/>.
/// </summary>
public sealed class WikiCompanionStore
{
    private const string SchemaId =
        "https://agent-taskboard.local/schemas/wiki-document-companion.schema.json";
    private const string Generator = "backend/Features/Docs/Grading/WikiGradingService.cs";

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> WriteGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly WikiAgentReadStore _agentReads;

    public WikiCompanionStore(WikiAgentReadStore? agentReads = null)
    {
        _agentReads = agentReads ?? new WikiAgentReadStore();
    }

    /// <summary>
    /// sha256 over the page content (matching how the run computes the idempotency
    /// hash), plus byte size and line count, for the companion fingerprint.
    /// </summary>
    public static (string Hash, int SizeBytes, int LineCount) Fingerprint(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var lineCount = string.IsNullOrEmpty(content) ? 0 : content.Split('\n').Length;
        return (hash, bytes.Length, lineCount);
    }

    /// <summary>sha256 of the page content, lowercase hex - the run's skip key.</summary>
    public static string HashContent(string content) => Fingerprint(content).Hash;

    /// <summary>Companion sidecar absolute path for a docs-relative page path.</summary>
    public static string CompanionPathFor(string wikiDir, string docsRelPath) =>
        Path.Combine(wikiDir, docsRelPath.Replace('/', Path.DirectorySeparatorChar) + ".meta.json");

    /// <summary>Stored grading provenance read back for idempotent skips: the
    /// source fingerprint hash, the grading model, and the last grade.</summary>
    public sealed record StoredGrading(string? Hash, string? Model, string? Grade);

    /// <summary>Reads the stored grading fingerprint hash + model + grade, or null
    /// when the companion has no grading block yet. Used by the run for idempotent
    /// skips.</summary>
    public StoredGrading? ReadGrading(string companionAbsPath)
    {
        if (!File.Exists(companionAbsPath)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(companionAbsPath));
            if (node is not JsonObject obj || obj["grading"] is not JsonObject grading) return null;
            var hash = (grading["sourceFingerprint"] as JsonObject)?["hash"]?.GetValue<string>();
            var model = grading["model"]?.GetValue<string>();
            var grade = grading["grade"]?.GetValue<string>();
            return new StoredGrading(hash, model, grade);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "WikiCompanionStore: unreadable companion during grading fingerprint read.");
            return null;
        }
    }

    /// <summary>
    /// Write the grading verdict into the page's companion, merging into an
    /// existing sidecar or creating a minimal schema-valid one. Returns whether
    /// the on-disk bytes actually changed.
    /// </summary>
    public WikiCompanionWriteResult WriteGrading(
        string wikiDir,
        string docsRelPath,
        string title,
        string content,
        WikiPageGradeVerdict verdict,
        WikiGradingRunRequest run,
        string runId,
        DateTime nowUtc)
    {
        var companionAbs = CompanionPathFor(wikiDir, docsRelPath);
        var (hash, sizeBytes, lineCount) = Fingerprint(content);
        var iso = nowUtc.ToString("o");

        var fingerprint = new JsonObject
        {
            ["algorithm"] = "sha256",
            ["hash"] = hash,
            ["sizeBytes"] = sizeBytes,
            ["lineCount"] = lineCount,
            ["capturedAt"] = iso,
        };

        var grading = new JsonObject
        {
            ["grade"] = verdict.Grade,
            ["assessment"] = verdict.Assessment,
        };
        if (verdict.Outdated.HasValue) grading["outdated"] = verdict.Outdated.Value;
        if (verdict.Contradictory.HasValue) grading["contradictory"] = verdict.Contradictory.Value;
        if (verdict.Gaps.HasValue) grading["gaps"] = verdict.Gaps.Value;
        var notes = new JsonArray();
        foreach (var n in verdict.Notes) notes.Add(n);
        grading["notes"] = notes;
        grading["cli"] = string.IsNullOrWhiteSpace(run.CliType) ? "claude" : run.CliType.Trim();
        grading["model"] = run.Model;
        if (!string.IsNullOrWhiteSpace(run.ThinkingLevel)) grading["thinkingLevel"] = run.ThinkingLevel.Trim();
        grading["method"] = "wiki-grading-run";
        grading["runId"] = runId;
        grading["gradedAt"] = iso;
        grading["ok"] = verdict.Ok;
        grading["sourceFingerprint"] = fingerprint;

        lock (GateFor(companionAbs))
        {
            EnsureSafeCompanion(wikiDir, companionAbs);
            JsonObject root = ReadExistingRoot(companionAbs)
                ?? BuildMinimalCompanion(docsRelPath, title, iso, (JsonObject)fingerprint.DeepClone(), run);
            root["grading"] = grading;
            _agentReads.MoveLegacyFromCompanion(wikiDir, docsRelPath, root);
            return WriteRootAtomically(wikiDir, companionAbs, root);
        }
    }

    /// <summary>
    /// Stamps a minimal <c>classification</c> block onto a page's companion at
    /// creation time (2026-07 metadata convention): <c>status = aktuell</c>,
    /// <c>analyzedAt = creation date</c>, and <c>type = </c> the folder default
    /// when the caller supplies one. Merges into an existing companion without
    /// clobbering any field already set (so a later analysis run wins), or writes
    /// a schema-valid minimal companion when none exists. Idempotent: returns
    /// <see cref="WikiCompanionWriteResult.Changed"/> = false on a no-op.
    /// </summary>
    public WikiCompanionWriteResult WriteCreationClassification(
        string wikiDir, string docsRelPath, string title, string content, string? defaultType, DateTime nowUtc)
    {
        var companionAbs = CompanionPathFor(wikiDir, docsRelPath);
        var iso = nowUtc.ToString("o");

        lock (GateFor(companionAbs))
        {
            EnsureSafeCompanion(wikiDir, companionAbs);
            var root = ReadExistingRoot(companionAbs)
                ?? BuildCreationCompanion(docsRelPath, title, content, iso);

            if (root["classification"] is not JsonObject classification)
            {
                classification = new JsonObject
                {
                    ["owner"] = docsRelPath.Split('/', 2)[0],
                    ["documentMode"] = "documentation",
                    ["temporalState"] = "present",
                    ["implementationState"] = "unknown",
                };
                root["classification"] = classification;
            }

            // Only fill gaps - never overwrite a value a real analysis already wrote.
            if (classification["status"] is null) classification["status"] = "aktuell";
            if (classification["analyzedAt"] is null) classification["analyzedAt"] = iso[..10];
            if (!string.IsNullOrWhiteSpace(defaultType) && classification["type"] is null)
                classification["type"] = defaultType;

            _agentReads.MoveLegacyFromCompanion(wikiDir, docsRelPath, root);
            return WriteRootAtomically(wikiDir, companionAbs, root);
        }
    }

    /// <summary>
    /// Updates only the lifecycle status in a page companion. Existing grading,
    /// drift, review, type, and relationship blocks are preserved. A page with
    /// no companion receives the same minimal schema-valid identity used at
    /// creation time.
    /// </summary>
    public WikiCompanionWriteResult WriteClassificationStatus(
        string wikiDir,
        string docsRelPath,
        string title,
        string content,
        string status,
        DateTime nowUtc)
    {
        var companionAbs = CompanionPathFor(wikiDir, docsRelPath);
        var iso = nowUtc.ToString("o");
        lock (GateFor(companionAbs))
        {
            EnsureSafeCompanion(wikiDir, companionAbs);
            var root = ReadExistingRoot(companionAbs)
                ?? BuildCreationCompanion(docsRelPath, title, content, iso);

            if (root["classification"] is not JsonObject classification)
            {
                classification = new JsonObject
                {
                    ["owner"] = docsRelPath.Split('/', 2)[0],
                    ["documentMode"] = "documentation",
                    ["temporalState"] = "present",
                    ["implementationState"] = "unknown",
                };
                root["classification"] = classification;
            }

            classification["status"] = status;
            classification["analyzedAt"] = iso[..10];
            _agentReads.MoveLegacyFromCompanion(wikiDir, docsRelPath, root);
            return WriteRootAtomically(wikiDir, companionAbs, root);
        }
    }

    private static object GateFor(string path) =>
        WriteGates.GetOrAdd(Path.GetFullPath(path), _ => new object());

    private static void EnsureSafeCompanion(string wikiDir, string companionAbs)
    {
        if (!FilesystemPathBoundary.IsPathUnderWithoutLinks(wikiDir, companionAbs))
            throw new IOException("Companion path is outside the wiki root or is a link.");
    }

    private static WikiCompanionWriteResult WriteRootAtomically(string wikiDir, string companionAbs, JsonObject root)
    {
        EnsureSafeCompanion(wikiDir, companionAbs);
        var serialized = root.ToJsonString(WriteOpts) + "\n";
        var changed = !File.Exists(companionAbs)
            || !string.Equals(File.ReadAllText(companionAbs), serialized, StringComparison.Ordinal);
        if (!changed) return new WikiCompanionWriteResult(false, companionAbs);

        var dir = Path.GetDirectoryName(companionAbs);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = companionAbs + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, serialized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, companionAbs, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { SilentCatch.Note(ex, "WikiCompanionStore: temporary sidecar cleanup failed."); }
        }
        return new WikiCompanionWriteResult(true, companionAbs);
    }

    /// <summary>A schema-valid minimal companion (no report/review/drift blocks yet).</summary>
    private static JsonObject BuildCreationCompanion(string docsRelPath, string title, string content, string iso)
    {
        var (hash, sizeBytes, lineCount) = Fingerprint(content);
        return new JsonObject
        {
            ["$schema"] = SchemaId,
            ["schemaVersion"] = "wiki-document-companion/v1",
            ["title"] = string.IsNullOrWhiteSpace(title) ? docsRelPath : title,
            ["source"] = new JsonObject
            {
                ["path"] = "docs/" + docsRelPath,
                ["type"] = DocumentType(docsRelPath),
                ["fingerprint"] = new JsonObject
                {
                    ["algorithm"] = "sha256",
                    ["hash"] = hash,
                    ["sizeBytes"] = sizeBytes,
                    ["lineCount"] = lineCount,
                    ["capturedAt"] = iso,
                },
            },
        };
    }

    private static JsonObject? ReadExistingRoot(string companionAbs)
    {
        if (!File.Exists(companionAbs)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(companionAbs)) as JsonObject;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "WikiCompanionStore: unreadable companion; rebuilding a minimal one.");
            return null;
        }
    }

    private static JsonObject BuildMinimalCompanion(
        string docsRelPath, string title, string iso, JsonObject fingerprint, WikiGradingRunRequest run)
    {
        var sourcePath = "docs/" + docsRelPath;
        var owner = docsRelPath.Split('/', 2)[0];
        return new JsonObject
        {
            ["$schema"] = SchemaId,
            ["schemaVersion"] = "wiki-document-companion/v1",
            ["title"] = string.IsNullOrWhiteSpace(title) ? docsRelPath : title,
            ["source"] = new JsonObject
            {
                ["path"] = sourcePath,
                ["type"] = DocumentType(docsRelPath),
                ["fingerprint"] = (JsonObject)fingerprint.DeepClone(),
            },
            ["report"] = new JsonObject
            {
                ["path"] = sourcePath + ".report.html",
                ["generatedAt"] = iso,
                ["generator"] = Generator,
                ["template"] = "wiki-document-companion-report/v1",
            },
            ["classification"] = new JsonObject
            {
                ["owner"] = owner,
                ["documentMode"] = "documentation",
                ["temporalState"] = "unknown",
                ["implementationState"] = "unknown",
            },
            ["review"] = new JsonObject
            {
                ["date"] = iso[..10],
                ["method"] = "wiki-grading-run",
                ["model"] = run.Model,
                ["sourceFingerprint"] = (JsonObject)fingerprint.DeepClone(),
                ["sourceChangedSinceReview"] = false,
            },
            ["drift"] = new JsonObject
            {
                ["grade"] = "unknown",
                ["hasDrift"] = null,
                ["score"] = null,
                ["summary"] = "No drift review summary has been generated yet.",
                ["rationale"] = new JsonArray(),
            },
            ["findings"] = new JsonArray(),
        };
    }

    private static string DocumentType(string relPath)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        return ext switch
        {
            ".md" => "markdown",
            ".html" or ".htm" => "html",
            ".json" => "json",
            _ => "document",
        };
    }
}
