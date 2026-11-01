using System.Globalization;
using System.Text;

namespace AgentStudio.TaskServer.Contracts.GitHubApp;

/// <summary>
/// How short-lived repository tokens reach Git on a host: a protected
/// <c>git credential-store</c> file, and a generated Git config with one
/// <c>[credential]</c> section per covered repository, included into each platform
/// Git process. A repository the App does not reach matches no section, so its
/// helpers, its host-wide credential lines and everything else stay exactly as the
/// host configured them. A covered repository's helper only answers <c>get</c>:
/// Git replays a used credential to every helper in its list, and a storing helper
/// would copy a one-hour token into a long-lived store.
/// </summary>
public static class RepositoryGitCredentialFiles
{
    private const string IncludeKey = "include.path";

    /// <summary>The token store's lines: every spelling a remote may use for a repository.</summary>
    public static string RenderGitStore(IEnumerable<RepositoryCredential> credentials)
    {
        var text = new StringBuilder();
        foreach (var credential in credentials)
        {
            if (GitHubRepositoryName.TryParse(credential.RepositoryUrl) is not { } repository) continue;
            var secret = Uri.EscapeDataString(credential.Username) + ":" + Uri.EscapeDataString(credential.Token);
            foreach (var path in Spellings(repository))
                text.Append("https://").Append(secret).Append('@').Append(GitHubRepositoryName.Host).Append('/').Append(path).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// The Git config that sends exactly these repositories to the store. The empty
    /// helper forgets what the host configured for them, so a token and an older
    /// credential never compete for one repository.
    /// </summary>
    public static string RenderGitConfig(string storePath, IEnumerable<RepositoryCredential> credentials)
    {
        var quoted = storePath.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
        var helper = ConfigValue($"!f() {{ test \"$1\" = get || exit 0; exec git credential-store --file='{quoted}' get; }}; f");
        var text = new StringBuilder();
        foreach (var credential in credentials)
        {
            if (GitHubRepositoryName.TryParse(credential.RepositoryUrl) is not { } repository) continue;
            foreach (var path in Spellings(repository))
            {
                text.Append("[credential \"https://").Append(GitHubRepositoryName.Host).Append('/').Append(path).Append("\"]\n");
                text.Append("\tuseHttpPath = true\n");
                text.Append("\thelper =\n");
                text.Append("\thelper = ").Append(helper).Append('\n');
            }
        }
        return text.ToString();
    }

    /// <summary>One line per repository, the format <c>RUNNER_REVIEW_GIT_CREDENTIALS_FILE</c> uses.</summary>
    public static string RenderReviewStore(IEnumerable<RepositoryCredential> credentials)
    {
        var text = new StringBuilder();
        foreach (var credential in credentials)
        {
            if (GitHubRepositoryName.TryParse(credential.RepositoryUrl) is not { } repository) continue;
            text.Append("https://").Append(Uri.EscapeDataString(credential.Username)).Append(':')
                .Append(Uri.EscapeDataString(credential.Token)).Append('@').Append(GitHubRepositoryName.Host)
                .Append('/').Append(repository.Owner).Append('/').Append(repository.Name).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// Includes the generated config into one Git process, after whatever its
    /// environment holds. A process that isolates its Git configuration chose its
    /// credential on purpose, such as a proof of one specific token, and stays
    /// untouched. Applying twice adds nothing.
    /// </summary>
    public static void Include(IDictionary<string, string?> environment, string configPath)
    {
        if (environment.TryGetValue("GIT_CONFIG_NOSYSTEM", out var isolated) && isolated == "1") return;
        var value = configPath.Replace('\\', '/');
        var count = environment.TryGetValue("GIT_CONFIG_COUNT", out var raw)
                    && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var existing)
            ? existing
            : 0;
        for (var index = 0; index < count; index++)
            if (environment.TryGetValue($"GIT_CONFIG_KEY_{index}", out var key) && key == IncludeKey
                && environment.TryGetValue($"GIT_CONFIG_VALUE_{index}", out var path) && path == value)
                return;
        environment[$"GIT_CONFIG_KEY_{count}"] = IncludeKey;
        environment[$"GIT_CONFIG_VALUE_{count}"] = value;
        environment["GIT_CONFIG_COUNT"] = (count + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Writes a credential file so it is never readable by others, not even for a moment.</summary>
    public static void WriteProtected(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        // Setting this at all throws on Windows, even to null.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IEnumerable<string> Spellings(GitHubRepositoryName repository)
    {
        var exact = $"{repository.Owner}/{repository.Name}";
        var lower = exact.ToLowerInvariant();
        yield return exact + ".git";
        yield return exact;
        if (lower == exact) yield break;
        yield return lower + ".git";
        yield return lower;
    }

    private static string ConfigValue(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
