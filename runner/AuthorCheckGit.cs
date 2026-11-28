using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Creates and imports temporary Git objects without using checkout refs or the coding index.</summary>
internal static class AuthorCheckGit
{
    private const string GitTree = "snapshot.git";
    private static readonly string[] GitModes = ["100644", "100755", "120000"];

    public static async Task<AuthorCheckSnapshot> CaptureAsync(
        string worktree, string directory, string prerequisite, IReadOnlyList<string> additions, CancellationToken ct)
    {
        var git = Path.Combine(directory, GitTree);
        var index = Path.Combine(directory, "index");
        var head = (await RunAsync(worktree, null, null, ["rev-parse", "HEAD"], ct)).Trim();
        var objects = (await RunAsync(worktree, null, null,
            ["rev-parse", "--path-format=absolute", "--git-path", "objects"], ct)).Trim();
        await InitAsync(directory, git, objects, ct);
        await RunAsync(worktree, git, index, ["merge-base", "--is-ancestor", prerequisite, head], ct);
        if ((await RunAsync(worktree, null, null, ["ls-files", "-u", "-z"], ct)).Length != 0)
            throw new InvalidDataException("Resolve the coding index conflicts before capturing an author check.");
        var stagedPaths = Split(await RunAsync(worktree, null, null, ["ls-files", "-z"], ct));
        var headPaths = Split(await RunAsync(worktree, git, null, ["ls-tree", "-r", "--name-only", "-z", head], ct));
        var known = stagedPaths.Concat(headPaths).Distinct(StringComparer.Ordinal).ToArray();
        if (known.Length > AuthorCheckProtocol.ObjectLimit || additions.Count > 64
            || additions.Any(path => !AllowedPath(path) || known.Contains(path, StringComparer.Ordinal)))
            throw new InvalidDataException("The author-check file selection is unsupported.");
        foreach (var path in additions)
        {
            var untracked = await RunAsync(worktree, null, null,
                ["--literal-pathspecs", "ls-files", "--others", "--exclude-standard", "-z", "--", path], ct);
            if (untracked != path + "\0" || !File.Exists(Path.Combine(worktree, path)))
                throw new InvalidDataException("An explicitly named addition is ignored, missing, or a directory.");
        }
        var excludedPaths = known.Where(path => !AllowedPath(path)).ToArray();
        if (excludedPaths.Length != 0)
            throw new InvalidDataException("The repository tracks a credential or machine-local path. This pilot cannot upload that tree.");

        async Task<string> ReadTreeAsync()
        {
            await RunAsync(worktree, git, index, ["read-tree", head], ct);
            await RunAsync(worktree, git, index, ["add", "-u", "--", "."], ct);
            // A file staged as new is tracked for this capture, even if the working bytes differ.
            var newFiles = stagedPaths.Except(headPaths, StringComparer.Ordinal).Concat(additions)
                .Where(path => File.Exists(Path.Combine(worktree, path))).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var batch in newFiles.Chunk(100))
                await RunAsync(worktree, git, index, ["--literal-pathspecs", "add", "--", .. batch], ct);
            return (await RunAsync(worktree, git, index, ["write-tree"], ct)).Trim();
        }

        var tree = await ReadTreeAsync();
        var verifiedStagedPaths = Split(await RunAsync(worktree, null, null, ["ls-files", "-z"], ct));
        if (tree != await ReadTreeAsync()
            || head != (await RunAsync(worktree, null, null, ["rev-parse", "HEAD"], ct)).Trim()
            || !stagedPaths.SequenceEqual(verifiedStagedPaths)
            || (await RunAsync(worktree, null, null, ["ls-files", "-u", "-z"], ct)).Length != 0)
            throw new InvalidDataException("The worktree changed during author-check capture.");
        await ValidateTreeAsync(worktree, git, prerequisite, tree, ct);
        var diff = await RunAsync(worktree, git, null,
            ["diff", "--no-ext-diff", "--no-textconv", prerequisite, tree], ct);
        if (PushProtection.ScanAddedLines(diff) is not null)
            throw new InvalidDataException("The author-check changes contain a credential-shaped value.");
        var commit = (await RunAsync(worktree, git, null,
            ["commit-tree", tree, "-p", prerequisite, "-m", "Author check snapshot"], ct)).Trim();
        await RunAsync(worktree, git, null, ["update-ref", AuthorCheckProtocol.BundleRef, commit], ct);
        var bundle = Path.Combine(directory, "source.bundle");
        await RunAsync(worktree, git, null,
            ["bundle", "create", "--version=2", bundle, AuthorCheckProtocol.BundleRef, "^" + prerequisite], ct);
        var bytes = new FileInfo(bundle).Length;
        if (bytes is < 1 or > AuthorCheckProtocol.BundleLimit)
            throw new InvalidDataException("The compressed author-check bundle exceeds 64 MiB.");
        return new(prerequisite, commit, tree, await HashAsync(bundle, ct), (int)bytes);
    }

    public static async Task VerifyAndMaterializeAsync(
        string baseRepo, string directory, AuthorCheckSnapshot source, CancellationToken ct)
    {
        var bundle = Path.Combine(directory, "source.bundle");
        if (new FileInfo(bundle).Length != source.Bytes || await HashAsync(bundle, ct) != source.Sha256)
            throw new InvalidDataException("Author-check bundle digest or length mismatch.");
        await ReadBundleHeaderAsync(bundle, source, ct);
        var git = Path.Combine(directory, GitTree);
        var objects = (await RunAsync(baseRepo, null, null,
            ["rev-parse", "--path-format=absolute", "--git-path", "objects"], ct)).Trim();
        await InitAsync(directory, git, objects, ct);
        await RunAsync(directory, git, null, ["cat-file", "-e", source.Base + "^{commit}"], ct);
        await RunAsync(directory, git, null, ["bundle", "verify", bundle], ct);
        await RunAsync(directory, git, null, ["-c", "fetch.fsckObjects=true", "bundle", "unbundle", bundle], ct);
        var identity = (await RunAsync(directory, git, null,
            ["rev-list", "--parents", "-n", "1", source.Commit], ct)).Trim();
        var tree = (await RunAsync(directory, git, null, ["rev-parse", source.Commit + "^{tree}"], ct)).Trim();
        if (identity != source.Commit + " " + source.Base || tree != source.Tree)
            throw new InvalidDataException("The imported author-check commit does not match the pinned source.");
        // Quota the actual imported objects, including objects outside the requested tree.
        var packs = Path.Combine(git, "objects", "pack");
        long expanded = 0;
        var objectsSeen = 0;
        foreach (var pack in Directory.EnumerateFiles(packs, "*.idx"))
        {
            var inspection = await RunAsync(directory, git, null, ["verify-pack", "-v", pack], ct);
            foreach (var line in inspection.Split('\n'))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 5 || !AuthorCheckProtocol.IsHex(fields[0], 40)) continue;
                if (!long.TryParse(fields[2], out var size) || size < 0
                    || size > AuthorCheckProtocol.ExpandedLimit - expanded || ++objectsSeen > AuthorCheckProtocol.ObjectLimit)
                    throw new InvalidDataException("The imported Git objects exceed the author-check expansion quota.");
                expanded += size;
            }
        }
        await ValidateTreeAsync(directory, git, source.Base, tree, ct);
        var checkout = Path.Combine(directory, "source");
        Directory.CreateDirectory(checkout);
        var index = Path.Combine(directory, "index");
        await RunAsync(checkout, git, index, ["read-tree", source.Commit], ct);
        await RunAsync(checkout, git, index, ["checkout-index", "--all"], ct);
    }

    private static async Task ReadBundleHeaderAsync(string bundle, AuthorCheckSnapshot source, CancellationToken ct)
    {
        await using var file = File.OpenRead(bundle);
        var prefix = new byte[(int)Math.Min(file.Length, 4096)];
        await file.ReadExactlyAsync(prefix, ct);
        var text = Encoding.ASCII.GetString(prefix);
        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (end < 0 || end + 14 > prefix.Length)
            throw new InvalidDataException("The author-check bundle has an invalid header.");
        var records = text[..end].Split('\n');
        var start = end + 2;
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(start + 8, 4));
        if (records.Length != 3 || records[0] != "# v2 git bundle"
            || !records[1].StartsWith("-" + source.Base + " ", StringComparison.Ordinal)
            || records[2] != source.Commit + " " + AuthorCheckProtocol.BundleRef
            || !prefix.AsSpan(start, 4).SequenceEqual("PACK"u8)
            || count is 0 or > AuthorCheckProtocol.ObjectLimit)
            throw new InvalidDataException("The author-check bundle has an unexpected prerequisite, ref, or object count.");
        file.Position = start + 12;
        CheckPackExpansion(file, count, ct);
    }

    private static void CheckPackExpansion(Stream file, uint count, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var prefix = new byte[20];
        var encoded = new OneByteReads(file);
        long total = 0;
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var header = NextByte(file);
            var type = (header >> 4) & 7;
            long size = header & 15;
            for (var shift = 4; (header & 128) != 0; shift += 7)
            {
                if (shift >= 32) throw new InvalidDataException("An author-check pack size is invalid.");
                header = NextByte(file);
                size |= (long)(header & 127) << shift;
            }
            if (type == 6)
            {
                var offsetBytes = 1;
                while ((NextByte(file) & 128) != 0)
                    if (++offsetBytes >= 10) throw new InvalidDataException("An author-check delta offset is invalid.");
            }
            else if (type == 7)
                file.ReadExactly(prefix);
            else if (type is < 1 or > 4)
                throw new InvalidDataException("An author-check pack object type is invalid.");
            if (size > AuthorCheckProtocol.ExpandedLimit - total)
                throw new InvalidDataException("The author-check pack exceeds the expansion quota.");

            using var compressed = new ZLibStream(encoded, CompressionMode.Decompress, leaveOpen: true);
            long expanded = 0;
            if (type is 6 or 7)
            {
                _ = ReadDeltaSize(compressed, ref expanded);
                var target = ReadDeltaSize(compressed, ref expanded);
                total += Math.Max(size, target);
            }
            else total += size;
            if (total > AuthorCheckProtocol.ExpandedLimit)
                throw new InvalidDataException("The author-check pack exceeds the expansion quota.");
            int read;
            while ((read = compressed.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                expanded += read;
                if (expanded > size) throw new InvalidDataException("An author-check pack object exceeds its declared length.");
            }
            if (expanded != size) throw new InvalidDataException("An author-check pack object is truncated.");
        }
        if (file.Length - file.Position != 20)
            throw new InvalidDataException("The author-check pack has an invalid checksum trailer.");
    }

    private static long ReadDeltaSize(Stream stream, ref long consumed)
    {
        long size = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var value = NextByte(stream);
            consumed++;
            size |= (long)(value & 127) << shift;
            if ((value & 128) == 0) return size;
        }
        throw new InvalidDataException("An author-check delta size is invalid.");
    }

    private static int NextByte(Stream stream)
        => stream.ReadByte() is var value && value >= 0
            ? value : throw new InvalidDataException("The author-check pack ended unexpectedly.");

    // ZLibStream can read ahead at member boundaries. Single-byte reads
    // preserve the next pack header while FileStream buffers the disk reads.
    private sealed class OneByteReads(Stream source) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, Math.Min(count, 1));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task ValidateTreeAsync(
        string cwd, string git, string prerequisite, string tree, CancellationToken ct)
    {
        long total = 0;
        var entries = Split(await RunAsync(cwd, git, null, ["ls-tree", "-rlz", tree], ct));
        if (entries.Length > AuthorCheckProtocol.ObjectLimit)
            throw new InvalidDataException("The author-check tree has too many paths.");
        foreach (var entry in entries)
        {
            var tab = entry.IndexOf('\t');
            var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = entry[(tab + 1)..];
            if (!AllowedPath(path) || !GitModes.Contains(fields[0], StringComparer.Ordinal)
                || !long.TryParse(fields[3], out var bytes) || bytes > AuthorCheckProtocol.ExpandedLimit - total)
                throw new InvalidDataException("The author-check tree exceeds its size limit or contains an unsupported path or submodule.");
            total += bytes;
        }
        var changes = Split(await RunAsync(cwd, git, null, ["diff", "--no-renames", "--raw", "-z", prerequisite, tree], ct));
        if (changes.Length % 2 != 0 || changes.Where((_, index) => index % 2 == 0).Any(change =>
            change.StartsWith(":160000 ", StringComparison.Ordinal) || change.AsSpan(7).StartsWith(" 160000 ", StringComparison.Ordinal)))
            throw new InvalidDataException("Changes to submodules are unsupported.");
        var changedPaths = changes.Where((_, index) => index % 2 != 0).ToHashSet(StringComparer.Ordinal);
        foreach (var path in changedPaths)
        {
            if (path.EndsWith(".gitattributes", StringComparison.Ordinal)
                || path.EndsWith(".lfsconfig", StringComparison.Ordinal))
                throw new InvalidDataException("Changes to Git filters or LFS configuration are unsupported.");
        }
        // Inspect changed files only. Git stores LFS pointer blobs as small text files.
        if (changedPaths.Count == 0) return;
        foreach (var revision in new[] { prerequisite, tree })
        {
            var found = await RunAsync(cwd, git, null,
                ["grep", "-I", "-l", "-z", "-F", "version https://git-lfs.github.com/spec/v1", revision, "--"], ct, allowOne: true);
            if (Split(found).Any(match => changedPaths.Contains(match[(revision.Length + 1)..])))
                throw new InvalidDataException("Changes to Git LFS objects are unsupported.");
        }
    }

    internal static bool AllowedPath(string path)
    {
        var parts = path.Split('/');
        return !Path.IsPathRooted(path) && !path.Any(char.IsControl) && !path.Contains('\\')
            && parts.All(part => part is not ("" or "." or "..")
                && !new[] { ".git", ".ssh", ".aws", ".config", ".netrc", "node_modules", "obj", "bin" }
                    .Contains(part, StringComparer.OrdinalIgnoreCase)
                && (!part.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                    || part.Equals(".env.example", StringComparison.OrdinalIgnoreCase))
                && !new[] { ".pem", ".key", ".pfx", ".p12", ".local" }.Any(suffix => part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] Split(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static async Task InitAsync(string cwd, string git, string objects, CancellationToken ct)
    {
        await RunAsync(cwd, null, null, ["init", "--bare", "--object-format=sha1", "--template=", git], ct);
        await File.WriteAllTextAsync(Path.Combine(git, "objects", "info", "alternates"), objects + "\n", ct);
    }

    internal static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task<string> RunAsync(
        string cwd, string? git, string? index, string[] arguments, CancellationToken ct, bool allowOne = false)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Agent Studio";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "runner@agent-studio.invalid";
        start.Environment["GIT_AUTHOR_DATE"] = start.Environment["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z";
        if (index is not null) start.Environment["GIT_INDEX_FILE"] = index;
        if (git is not null)
        {
            start.ArgumentList.Add("--git-dir=" + git);
            if (index is not null) start.ArgumentList.Add("--work-tree=" + cwd);
        }
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Git.");
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await error;
        if (process.ExitCode != 0 && !(allowOne && process.ExitCode == 1))
            throw new InvalidDataException($"Git could not verify or capture author-check source (exit {process.ExitCode}).");
        return await output;
    }
}
