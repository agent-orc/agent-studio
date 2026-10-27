using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// AGT-3083 - the process-wide origin per repository root. It holds the
/// <see cref="GitEffectiveConfig"/> Git resolved for that root, so a lookup
/// answers from memory while every file Git said contributed to the
/// configuration is byte-identical. A changed or vanished contributing file
/// resolves through Git again. A config source created after a capture (an
/// include target that did not exist yet, a new global file) is recorded as an
/// absent source and resolves through Git again once it appears
/// (<see cref="GitConfigSignature.SourcesUnchanged"/>, AGT-3322). The Git
/// state index publishes every capture it pays for. A failed capture is never
/// stored. There is no partial config
/// parser: Git alone interprets includes, conditional includes and worktree
/// config.
/// </summary>
public sealed class GitOriginStore
{
    private readonly Func<string, GitEffectiveConfig> _capture;
    private readonly ConcurrentDictionary<string, GitEffectiveConfig> _configs =
        new(FileSystemPathComparer.Instance);

    public GitOriginStore()
        : this(null)
    {
    }

    internal GitOriginStore(Func<string, GitEffectiveConfig>? capture)
    {
        _capture = capture ?? GitConfigSignature.Capture;
    }

    /// <summary>Stores a capture the Git state index already paid for.</summary>
    internal void Publish(string root, GitEffectiveConfig config)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        _configs[GitConfigScope.NormalizeRoot(root)] = config;
    }

    /// <summary>
    /// The configured origin URL at <paramref name="root"/>, or null when the
    /// repository has no origin or its configuration cannot be read.
    /// </summary>
    internal string? ReadOrigin(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (GitConfigScope.TryReadOrigin(root, out var scoped)) return scoped;
        var key = GitConfigScope.NormalizeRoot(root);
        if (_configs.TryGetValue(key, out var known) && GitConfigSignature.SourcesUnchanged(known))
            return known.OriginUrl;

        GitEffectiveConfig fresh;
        try { fresh = _capture(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing checkout or a Git error is the conservative "no origin"
            // reading, exactly as before. Drop only the entry this read
            // invalidated so a concurrent fresh publish survives.
            SilentCatch.Note(ex, "GitOriginStore: repository configuration unavailable");
            if (known is not null)
                ((ICollection<KeyValuePair<string, GitEffectiveConfig>>)_configs)
                    .Remove(new KeyValuePair<string, GitEffectiveConfig>(key, known));
            return null;
        }
        _configs[key] = fresh;
        return fresh.OriginUrl;
    }
}
