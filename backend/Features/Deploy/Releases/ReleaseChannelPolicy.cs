using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <summary>Pure release channel selection from the committed project definition.</summary>
public static class ReleaseChannelPolicy
{
    public static (ProjectChannelDefinition? Channel, string? Error) Select(
        ProjectDeploymentDefinition? definition, string? versionTag, string? branch,
        string? requested, bool mayOverride)
    {
        var channels = definition?.Channels ?? [];
        if (channels.Count == 0)
            return requested is null or "default"
                ? (null, null) : (null, $"Channel '{requested}' is not declared.");
        var matches = channels.Where(item => !item.Default && Matches(item, versionTag, branch)).ToArray();
        var automatic = matches.Length == 1 ? matches[0] : channels.Single(item => item.Default);
        if (requested is not null)
        {
            var selected = channels.FirstOrDefault(item => item.Name == requested);
            if (selected is null) return (null, $"Channel '{requested}' is not declared.");
            if (!mayOverride && matches.Length > 1)
                return (null, $"More than one channel rule matched: {string.Join(", ", matches.Select(Rule))}. An approver must select the channel.");
            if (mayOverride || matches.Length == 1 && selected.Name == automatic.Name
                || matches.Length == 0 && selected.Default) return (selected, null);
            return selected.Default
                ? (null, $"Channel '{requested}' cannot override matching rule {string.Join(", ", matches.Select(Rule))}.")
                : (null, $"Channel '{requested}' rule did not match: {Rule(selected)}.");
        }
        if (matches.Length > 1)
            return (null, $"More than one channel rule matched: {string.Join(", ", matches.Select(Rule))}.");
        return (automatic, null);
    }

    public static IReadOnlyList<ProjectLifecyclePhase> Lifecycle(ProjectDeploymentDefinition? definition,
        ProjectChannelDefinition? channel)
        => channel is { Lifecycle: not "standard" } && definition?.Lifecycles.TryGetValue(channel.Lifecycle, out var phases) == true
            ? phases : definition?.StandardLifecycle ?? ProjectDeploymentVocabulary.DefaultStandardLifecycle();

    private static bool Matches(ProjectChannelDefinition channel, string? versionTag, string? branch)
        => (channel.PrereleaseTag is null || versionTag is not null &&
                Regex.IsMatch(versionTag, $"-{Regex.Escape(channel.PrereleaseTag)}(?:[.+]|$)", RegexOptions.CultureInvariant))
           && (channel.BranchPattern is null || branch is not null &&
               Regex.IsMatch(branch, "^" + Regex.Escape(channel.BranchPattern).Replace("\\*", ".*") + "$",
                   RegexOptions.CultureInvariant));

    private static string Rule(ProjectChannelDefinition channel)
        => string.Join(" and ", new[]
        {
            channel.PrereleaseTag is null ? null : $"prereleaseTag={channel.PrereleaseTag}",
            channel.BranchPattern is null ? null : $"branchPattern={channel.BranchPattern}",
        }.Where(item => item is not null));
}
