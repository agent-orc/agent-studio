using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

public sealed record ReleaseChannelFacts(string? PreRelease, string? Branch, string? Override);
public sealed record ReleaseChannelChoice(ProjectChannelDefinition? Channel, string? Error, bool Forbidden = false);

/// <summary>Pure channel choice. Rule kinds are conjunctive; the default is the fallback.</summary>
public static class ReleaseChannelPolicy
{
    public static ReleaseChannelChoice Choose(ProjectDeploymentDefinition? definition, ReleaseChannelFacts facts,
        bool isApprover)
    {
        var channels = definition?.Channels ?? [];
        if (channels.Count == 0)
            return facts.Override == ReleaseSnapshot.DefaultChannel && !isApprover
                ? new(null, "Channel override for 'default' requires an Owner or project approver; default fallback rule.", true)
                : facts.Override is null or ReleaseSnapshot.DefaultChannel
                ? new(null, null)
                : new(null, $"Channel '{facts.Override}' is not declared. The implicit default channel has no version rules.");
        if (facts.Override is { } requested)
        {
            var channel = channels.FirstOrDefault(item => item.Name == requested);
            if (channel is null) return new(null, $"Channel '{requested}' is not declared.");
            if (!isApprover)
                return new(null, $"Channel override for '{requested}' requires an Owner or project approver; {Rule(channel)}.", true);
            return new(channel, null);
        }
        var matches = channels.Where(channel => !channel.Default && Matches(channel, facts)).ToArray();
        if (matches.Length > 1)
            return new(null, $"Multiple channel rules match: {string.Join(", ", matches.Select(channel => $"{channel.Name} ({Rule(channel)})"))}.");
        return new(matches.SingleOrDefault() ?? channels.Single(channel => channel.Default), null);
    }

    public static bool Matches(ProjectChannelDefinition channel, ReleaseChannelFacts facts)
        => (channel.PreRelease is null || string.Equals(channel.PreRelease, facts.PreRelease, StringComparison.Ordinal))
           && (channel.Branch is null || facts.Branch is not null && BranchMatches(channel.Branch, facts.Branch));

    private static bool BranchMatches(string pattern, string branch)
    {
        var rule = 0;
        var input = 0;
        var wildcard = -1;
        var retry = 0;
        while (input < branch.Length)
        {
            if (rule < pattern.Length && pattern[rule] == branch[input]) { rule++; input++; }
            else if (rule < pattern.Length && pattern[rule] == '*') { wildcard = rule++; retry = input; }
            else if (wildcard >= 0) { rule = wildcard + 1; input = ++retry; }
            else return false;
        }
        while (rule < pattern.Length && pattern[rule] == '*') rule++;
        return rule == pattern.Length;
    }

    public static string Rule(ProjectChannelDefinition channel)
        => channel.Default ? "default fallback rule"
            : string.Join(" and ", new[]
            {
                channel.PreRelease is null ? null : $"preRelease '{channel.PreRelease}'",
                channel.Branch is null ? null : $"branch pattern '{channel.Branch}'",
            }.Where(rule => rule is not null));
}
