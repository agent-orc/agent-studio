using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AgentStudio.Shared;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Design;

/// <summary>
/// Read + accept surface for the project UX/UI panel (slice 6 of the
/// quality-system mockup, docs/mockups/quality-system/). Each watched
/// project has an optional <c>design/</c> folder under its registered
/// repository directory:
/// <list type="bullet">
///   <item><c>design/brief.md</c> - Markdown brief, frontmatter contains status + last-updated.</item>
///   <item><c>design/references/&lt;slug&gt;.md</c> - kind=accepted|rejected|external + optional sibling image.</item>
///   <item><c>design/council/YYYY-MM-DD-&lt;slug&gt;.md</c> - council critique notes.</item>
///   <item><c>design/loop.md</c> - current iteration status, last action, last council date.</item>
/// </list>
///
/// Action-driven principle (README "Action-Driven Principle"): this service
/// only enumerates and parses what is on disk. The four action buttons in
/// the panel are separate POST endpoints that queue normal CLI jobs.
/// </summary>
public sealed class DesignEvidenceService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<DesignEvidenceService> _logger;

    private static readonly Regex DateInFileNameRegex = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled);

    public DesignEvidenceService(TaskScannerService scanner, ILogger<DesignEvidenceService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>
    /// Returns the design folder root for the project, or null when the
    /// project name is unknown. The folder is not required to exist.
    /// </summary>
    public string? ResolveDesignDir(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry is null) return null;
        var root = string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RootPath : entry.RepositoryPath;
        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(Path.GetFullPath(root), "design");
    }

    /// <summary>
    /// Builds the four-card top-row payload: status, references count,
    /// screenshot accepted/rejected counts, council notes count. Reads
    /// brief.md / loop.md for the status string and falls back to a
    /// neutral "no iteration" when neither file is present.
    /// </summary>
    public DesignOverview GetOverview(string projectName)
    {
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null)
        {
            return new DesignOverview(
                projectName, string.Empty, false, "unknown",
                null, null, 0, 0, 0, 0, 0, 0, false, null);
        }
        if (!IsEvidenceDirectory(designDir, designDir))
        {
            return new DesignOverview(
                projectName, designDir, false, "no iteration",
                "No design/ folder yet", null, 0, 0, 0, 0, 0, 0, false, null);
        }

        var (briefExists, briefSummary, briefStatus) = ReadBrief(designDir);
        var (loopStatus, loopDetail, lastReview) = ReadLoop(designDir);
        var status = !string.IsNullOrWhiteSpace(loopStatus) ? loopStatus
            : (!string.IsNullOrWhiteSpace(briefStatus) ? briefStatus : "no iteration");

        var refs = ListReferencesInternal(designDir);
        var council = ListCouncilNotesInternal(designDir);

        var accepted = 0;
        var rejected = 0;
        var external = 0;
        foreach (var r in refs)
        {
            switch (r.Kind)
            {
                case "accepted": accepted++; break;
                case "rejected": rejected++; break;
                case "external": external++; break;
            }
        }

        var open = 0;
        var acceptedNotes = 0;
        foreach (var n in council)
        {
            if (string.IsNullOrWhiteSpace(n.AcceptedAt)) open++;
            else acceptedNotes++;
        }

        return new DesignOverview(
            ProjectName: projectName,
            DesignDir: designDir,
            Exists: true,
            Status: status,
            StatusDetail: loopDetail ?? briefSummary,
            LastReviewDate: lastReview,
            ReferencesCount: refs.Count,
            ScreenshotsAcceptedCount: accepted,
            ScreenshotsRejectedCount: rejected,
            ExternalCount: external,
            CouncilOpenCount: open,
            CouncilAcceptedCount: acceptedNotes,
            BriefExists: briefExists,
            BriefSummary: briefSummary);
    }

    public DesignReferencesResponse ListReferences(string projectName)
    {
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null)
            return new DesignReferencesResponse(projectName, string.Empty, false, Array.Empty<DesignReferenceItem>());
        var refsDir = Path.Combine(designDir, "references");
        if (!IsEvidenceDirectory(designDir, refsDir))
            return new DesignReferencesResponse(projectName, refsDir, false, Array.Empty<DesignReferenceItem>());
        var list = ListReferencesInternal(designDir);
        return new DesignReferencesResponse(projectName, refsDir, true, list);
    }

    public DesignCouncilResponse ListCouncilNotes(string projectName)
    {
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null)
            return new DesignCouncilResponse(projectName, string.Empty, false, Array.Empty<DesignCouncilNote>());
        var councilDir = Path.Combine(designDir, "council");
        if (!IsEvidenceDirectory(designDir, councilDir))
            return new DesignCouncilResponse(projectName, councilDir, false, Array.Empty<DesignCouncilNote>());
        var list = ListCouncilNotesInternal(designDir);
        return new DesignCouncilResponse(projectName, councilDir, true, list);
    }

    /// <summary>
    /// Reads one council note in raw form for the "unstructured report"
    /// fallback. Returns null when the project / file is unknown or when
    /// the path escapes the council folder.
    /// </summary>
    public string? ReadCouncilNote(string projectName, string fileName)
    {
        if (!IsBareMarkdownName(fileName)) return null;
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null) return null;
        return ReadEvidence(designDir, Path.Combine(designDir, "council", fileName));
    }

    public string? ReadReference(string projectName, string fileName)
    {
        if (!IsBareMarkdownName(fileName)) return null;
        var designDir = ResolveDesignDir(projectName);
        return designDir is null ? null : ReadEvidence(designDir, Path.Combine(designDir, "references", fileName));
    }

    /// <summary>
    /// Stamps an <c>acceptedAt</c> field into the council note's
    /// frontmatter (creating frontmatter if absent) and returns the new
    /// timestamp. Returns null when the project / file is unknown.
    /// </summary>
    public AcceptCouncilNoteResponse? AcceptCouncilNote(string projectName, string fileName)
    {
        if (!IsBareMarkdownName(fileName)) return null;
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null) return null;
        var full = Path.Combine(designDir, "council", fileName);
        var root = Path.GetDirectoryName(designDir)!;
        if (!FilesystemPathBoundary.IsRegularFileUnder(root, full)) return null;

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        try
        {
            using var directory = EvidenceDirectory.Open(root, Path.GetDirectoryName(full)!);
            using var file = directory.OpenRead(fileName);
            using var reader = new StreamReader(file);
            var updated = StampAcceptedAt(reader.ReadToEnd(), stamp);
            BeforeAcceptReplace?.Invoke();
            directory.Replace(fileName, updated, file.SafeFileHandle);
            var parse = DesignEvidenceParser.Parse(updated);
            return new AcceptCouncilNoteResponse(fileName, stamp, parse.ParseOk);
        }
        catch (Exception ex) when (IsFileBoundaryFailure(ex))
        {
            _logger.LogWarning(ex, "Failed to safely stamp council note {File}", full);
            return null;
        }
    }

    // Deterministic substitution seams for the filesystem regression tests.
    internal Action? BeforeAcceptReplace { get; set; }
    internal Action? BeforeEvidenceOpen { get; set; }

    private List<DesignReferenceItem> ListReferencesInternal(string designDir)
    {
        var refsDir = Path.Combine(designDir, "references");
        var items = new List<DesignReferenceItem>();
        foreach (var path in ListEvidenceFiles(designDir, refsDir))
        {
            var text = ReadEvidence(designDir, path);
            if (text is null) continue;
            try { items.Add(BuildReferenceItem(designDir, refsDir, path, text)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read design reference {File}", path);
                var fi = new FileInfo(path);
                items.Add(new DesignReferenceItem(
                    FileName: fi.Name,
                    RelPath: Path.GetRelativePath(designDir, path).Replace('\\', '/'),
                    Kind: "external",
                    Title: null,
                    Summary: null,
                    ScreenshotRelPath: null,
                    UpdatedAt: fi.Exists ? fi.LastWriteTimeUtc : DateTime.UtcNow,
                    ParseOk: false,
                    ParseError: $"unreadable: {ex.GetType().Name}"));
            }
        }
        items.Sort((a, b) => DateTime.Compare(b.UpdatedAt, a.UpdatedAt));
        return items;
    }

    private List<DesignCouncilNote> ListCouncilNotesInternal(string designDir)
    {
        var councilDir = Path.Combine(designDir, "council");
        var items = new List<DesignCouncilNote>();
        foreach (var path in ListEvidenceFiles(designDir, councilDir))
        {
            var text = ReadEvidence(designDir, path);
            if (text is null) continue;
            try { items.Add(BuildCouncilNote(designDir, path, text)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read council note {File}", path);
                var fi = new FileInfo(path);
                items.Add(new DesignCouncilNote(
                    FileName: fi.Name,
                    RelPath: Path.GetRelativePath(designDir, path).Replace('\\', '/'),
                    Category: null,
                    Title: null,
                    Summary: null,
                    NoteDate: ExtractDateFromFileName(fi.Name),
                    AcceptedAt: null,
                    UpdatedAt: fi.Exists ? fi.LastWriteTimeUtc : DateTime.UtcNow,
                    ParseOk: false,
                    ParseError: $"unreadable: {ex.GetType().Name}"));
            }
        }
        // Newest first by note date when available, else mtime.
        items.Sort((a, b) =>
        {
            var keyA = a.NoteDate ?? string.Empty;
            var keyB = b.NoteDate ?? string.Empty;
            var cmp = string.CompareOrdinal(keyB, keyA);
            if (cmp != 0) return cmp;
            return DateTime.Compare(b.UpdatedAt, a.UpdatedAt);
        });
        return items;
    }

    private static DesignReferenceItem BuildReferenceItem(string designDir, string refsDir, string filePath, string text)
    {
        var fi = new FileInfo(filePath);
        var parse = DesignEvidenceParser.Parse(text);
        var rawKind = DesignEvidenceParser.GetString(parse.Fields, "kind")
            ?? DesignEvidenceParser.GetString(parse.Fields, "category");
        var kind = DesignEvidenceParser.NormaliseKind(rawKind);
        var screenshot = DesignEvidenceParser.GetString(parse.Fields, "screenshot")
            ?? DesignEvidenceParser.GetString(parse.Fields, "image")
            ?? GuessSiblingImage(designDir, refsDir, fi.Name);
        var screenshotRel = ResolveDesignRelPath(designDir, refsDir, screenshot);
        return new DesignReferenceItem(
            FileName: fi.Name,
            RelPath: Path.GetRelativePath(designDir, filePath).Replace('\\', '/'),
            Kind: kind,
            Title: DesignEvidenceParser.GetString(parse.Fields, "title"),
            Summary: DesignEvidenceParser.GetString(parse.Fields, "summary"),
            ScreenshotRelPath: screenshotRel,
            UpdatedAt: fi.LastWriteTimeUtc,
            ParseOk: parse.ParseOk,
            ParseError: parse.ParseError);
    }

    private static DesignCouncilNote BuildCouncilNote(string designDir, string filePath, string text)
    {
        var fi = new FileInfo(filePath);
        var parse = DesignEvidenceParser.Parse(text);
        var dateFromName = ExtractDateFromFileName(fi.Name);
        return new DesignCouncilNote(
            FileName: fi.Name,
            RelPath: Path.GetRelativePath(designDir, filePath).Replace('\\', '/'),
            Category: DesignEvidenceParser.GetString(parse.Fields, "category")
                ?? DesignEvidenceParser.GetString(parse.Fields, "kind")
                ?? DesignEvidenceParser.GetString(parse.Fields, "tag"),
            Title: DesignEvidenceParser.GetString(parse.Fields, "title")
                ?? DesignEvidenceParser.GetString(parse.Fields, "role"),
            Summary: DesignEvidenceParser.GetString(parse.Fields, "summary")
                ?? DesignEvidenceParser.GetString(parse.Fields, "note"),
            NoteDate: DesignEvidenceParser.GetString(parse.Fields, "date")
                ?? DesignEvidenceParser.GetString(parse.Fields, "noteDate")
                ?? dateFromName,
            AcceptedAt: DesignEvidenceParser.GetString(parse.Fields, "acceptedAt")
                ?? DesignEvidenceParser.GetString(parse.Fields, "accepted_at"),
            UpdatedAt: fi.LastWriteTimeUtc,
            ParseOk: parse.ParseOk,
            ParseError: parse.ParseError);
    }

    private (bool exists, string? summary, string? status) ReadBrief(string designDir)
    {
        var briefPath = Path.Combine(designDir, "brief.md");
        try
        {
            var text = ReadEvidence(designDir, briefPath);
            if (text is null) return (false, null, null);
            var parse = DesignEvidenceParser.Parse(text);
            return (
                true,
                DesignEvidenceParser.GetString(parse.Fields, "summary"),
                DesignEvidenceParser.GetString(parse.Fields, "status"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read brief.md {File}", briefPath);
            return (true, null, null);
        }
    }

    private (string? status, string? detail, string? lastReview) ReadLoop(string designDir)
    {
        var loopPath = Path.Combine(designDir, "loop.md");
        try
        {
            var text = ReadEvidence(designDir, loopPath);
            if (text is null) return (null, null, null);
            var parse = DesignEvidenceParser.Parse(text);
            return (
                DesignEvidenceParser.GetString(parse.Fields, "status"),
                DesignEvidenceParser.GetString(parse.Fields, "lastAction")
                    ?? DesignEvidenceParser.GetString(parse.Fields, "last_action"),
                DesignEvidenceParser.GetString(parse.Fields, "lastCouncil")
                    ?? DesignEvidenceParser.GetString(parse.Fields, "last_council"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read loop.md {File}", loopPath);
            return (null, null, null);
        }
    }

    private static string? GuessSiblingImage(string designDir, string refsDir, string mdFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(mdFileName);
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" })
        {
            var candidate = Path.Combine(refsDir, stem + ext);
            if (FilesystemPathBoundary.IsRegularFileUnder(Path.GetDirectoryName(designDir)!, candidate))
                return Path.GetFileName(candidate);
        }
        return null;
    }

    /// <summary>
    /// Resolves a frontmatter-supplied screenshot path to a path relative
    /// to the project's <c>design/</c> folder. Bare file names are
    /// resolved against the references folder.
    /// </summary>
    private static string? ResolveDesignRelPath(string designDir, string refsDir, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Replace('\\', '/').Trim('/');
        // Explicit metadata is a path hint, including before its image exists.
        if (trimmed.StartsWith("references/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("council/", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var sibling = Path.Combine(refsDir, trimmed);
        return File.Exists(sibling)
            ? Path.GetRelativePath(designDir, sibling).Replace('\\', '/') : trimmed;
    }

    private static string ExtractDateFromFileName(string fileName)
    {
        var m = DateInFileNameRegex.Match(fileName);
        return m.Success ? m.Groups["date"].Value : string.Empty;
    }

    private static bool IsBareMarkdownName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
            return false;
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEvidenceDirectory(string designDir, string path)
        => FilesystemPathBoundary.IsPathUnderWithoutLinks(Path.GetDirectoryName(designDir)!, path)
            && Directory.Exists(path);

    private string? ReadEvidence(string designDir, string path)
    {
        var root = Path.GetDirectoryName(designDir)!;
        if (!FilesystemPathBoundary.IsRegularFileUnder(root, path)) return null;
        try
        {
            using var directory = EvidenceDirectory.Open(root, Path.GetDirectoryName(path)!);
            BeforeEvidenceOpen?.Invoke();
            using var file = directory.OpenRead(Path.GetFileName(path));
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (IsFileBoundaryFailure(ex))
        {
            _logger.LogWarning(ex, "Failed to safely read design evidence {File}", path);
            return null;
        }
    }

    private static string[] ListEvidenceFiles(string designDir, string path)
    {
        try
        {
            using var directory = EvidenceDirectory.Open(Path.GetDirectoryName(designDir)!, path);
            return Directory.GetFiles(directory.IoPath, "*.md", SearchOption.TopDirectoryOnly)
                .Select(file => Path.Combine(path, Path.GetFileName(file))).ToArray();
        }
        catch (Exception ex) when (IsFileBoundaryFailure(ex)) { return []; }
    }

    private static bool IsFileBoundaryFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    /// <summary>
    /// Pins every directory below the repository. Unix I/O uses the open parent
    /// descriptor; Windows directory handles deny rename/deletion until disposal.
    /// This complements the shared path check across check/open/replace races.
    /// </summary>
    private sealed class EvidenceDirectory : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        public string IoPath { get; private set; } = "";
        private int Descriptor => _handles[^1].DangerousGetHandle().ToInt32();
        private static int DirectoryFlags => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
        private static int NoFollowFlags => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        private static int CloseOnExecFlags => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

        public static EvidenceDirectory Open(string root, string path)
        {
            if (!FilesystemPathBoundary.IsPathUnderWithoutLinks(root, path))
                throw new IOException("Design evidence directory rejected.");
            var result = new EvidenceDirectory();
            try
            {
                var current = Path.GetFullPath(root);
                var segments = Path.GetRelativePath(current, path)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                result.PinDirectory(current, -100);
                foreach (var segment in segments)
                {
                    if (segment == ".") continue;
                    current = Path.Combine(current, segment);
                    result.PinDirectory(OperatingSystem.IsWindows() ? current : segment,
                        OperatingSystem.IsWindows() ? -1 : result.Descriptor);
                }
                result.IoPath = OperatingSystem.IsWindows() ? current
                    : (OperatingSystem.IsMacOS() ? "/dev/fd/" : "/proc/self/fd/") + result.Descriptor;
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private void PinDirectory(string path, int parent)
        {
            SafeFileHandle handle;
            if (OperatingSystem.IsWindows())
                handle = CreateFile(path, 0x80, FileShare.ReadWrite, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            else
            {
                // macOS AT_FDCWD differs from Linux.
                if (OperatingSystem.IsMacOS() && parent == -100) parent = -2;
                handle = UnixOpen(parent, path, DirectoryFlags | NoFollowFlags | CloseOnExecFlags);
            }
            _handles.Add(handle);
            if (handle.IsInvalid || (File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Design evidence directory rejected.");
        }

        public FileStream OpenRead(string name)
        {
            var handle = OperatingSystem.IsWindows()
                ? CreateFile(Path.Combine(IoPath, name), 0x80000000, FileShare.Read | FileShare.Delete,
                    IntPtr.Zero, 3, 0x00200000, IntPtr.Zero)
                : UnixOpen(Descriptor, name, NoFollowFlags | CloseOnExecFlags | (OperatingSystem.IsMacOS() ? 4 : 0x800));
            try
            {
                if (handle.IsInvalid || (File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new IOException("Design evidence file rejected.");
                if (!OperatingSystem.IsWindows() && !IsUnixRegularFile(handle))
                    throw new IOException("Design evidence must be a regular file.");
                var stream = new FileStream(handle, FileAccess.Read);
                if (stream.CanSeek) return stream;
                stream.Dispose();
                throw new IOException("Design evidence must be a regular file.");
            }
            catch { handle.Dispose(); throw; }
        }

        public void Replace(string name, string text, SafeFileHandle source)
        {
            var temporary = Path.Combine(IoPath, ".accepted-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output.SafeFileHandle, File.GetUnixFileMode(source));
                    using var writer = new StreamWriter(output, leaveOpen: true);
                    writer.Write(text);
                    writer.Flush();
                    output.Flush(flushToDisk: true);
                }
                // Rename replaces the directory entry itself, never a substituted
                // leaf link's target. Both paths use the same pinned parent.
                File.Move(temporary, Path.Combine(IoPath, name), overwrite: true);
            }
            finally { File.Delete(temporary); }
        }

        public void Dispose()
        {
            for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        }

        private static SafeFileHandle UnixOpen(int parent, string name, int flags)
            => new((nint)OpenAt(parent, name, flags), ownsHandle: true);

        private static bool IsUnixRegularFile(SafeFileHandle handle)
        {
            var descriptor = handle.DangerousGetHandle().ToInt32();
            if (OperatingSystem.IsMacOS())
                return MacStat(descriptor, out var status) == 0 && (status.Mode & 0xf000) == 0x8000;
            return LinuxStat(descriptor, "", 0x1000, 1, out var linuxStatus) == 0
                && (linuxStatus.Mode & 0xf000) == 0x8000;
        }

        // statx with AT_EMPTY_PATH checks the open Linux handle, including on
        // arm64. Darwin's fstat has a different layout and mode offset.
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxFileStatus { [FieldOffset(28)] public ushort Mode; }

        [StructLayout(LayoutKind.Explicit, Size = 144)]
        private struct MacFileStatus { [FieldOffset(4)] public ushort Mode; }

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        private static extern int LinuxStat(int descriptor, string path, int flags, uint mask, out LinuxFileStatus status);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int MacStat(int descriptor, out MacFileStatus status);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(int directory, string name, int flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
    }

    /// <summary>
    /// Stamps <c>acceptedAt: &lt;stamp&gt;</c> into the YAML frontmatter
    /// of the note, creating frontmatter if absent. Idempotent: replaces
    /// an existing <c>acceptedAt</c> line in place.
    /// </summary>
    internal static string StampAcceptedAt(string original, string stamp)
    {
        // No frontmatter -> wrap the body.
        if (!original.StartsWith("---", StringComparison.Ordinal))
        {
            return $"---\nacceptedAt: {stamp}\n---\n\n{original}";
        }
        var lines = original.Split('\n').ToList();
        // Locate the closing --- of frontmatter.
        var closeIdx = -1;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "---") { closeIdx = i; break; }
        }
        if (closeIdx < 0)
        {
            // Malformed frontmatter; prepend a fresh block.
            return $"---\nacceptedAt: {stamp}\n---\n\n{original}";
        }
        // Replace existing acceptedAt line if present.
        var replaced = false;
        for (int i = 1; i < closeIdx; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("acceptedAt:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("accepted_at:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"acceptedAt: {stamp}";
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            lines.Insert(closeIdx, $"acceptedAt: {stamp}");
        }
        return string.Join('\n', lines);
    }

    private WatchPathEntry? FindProject(string projectName) =>
        _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
}
