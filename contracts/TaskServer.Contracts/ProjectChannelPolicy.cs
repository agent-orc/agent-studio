using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public static class ProjectChannelPolicy
{
    public static (ProjectChannelDefinition? Channel, string? Refusal) Select(
        ProjectDeploymentDefinition? definition, string version, string? branch, string? overrideName, bool approver)
    {
        if (definition is null || definition.Channels.Count == 0)
            return overrideName is null ? (new("default", "standard", null, null, true), null)
                : (null, $"Channel '{overrideName}' is not declared.");
        if (overrideName is not null && !approver)
            return (null, "Channel override requires the Owner or an approver role.");
        if (overrideName is not null)
            return definition.Channels.FirstOrDefault(channel => channel.Name == overrideName) is { } selected
                ? (selected, null) : (null, $"Channel '{overrideName}' is not declared.");
        var matches = definition.Channels.Where(channel => !channel.Default && Matches(channel, version, branch)).ToArray();
        if (matches.Length > 1)
            return (null, $"Channel rules for {string.Join(", ", matches.Select(channel => channel.Name))} overlap; choose an explicit channel.");
        if (matches.Length == 1) return (matches[0], null);
        return (definition.Channels.FirstOrDefault(channel => channel.Default), null);
    }

    public static bool Matches(ProjectChannelDefinition channel, string version, string? branch)
        => (channel.PreReleaseTag is not null || channel.BranchPattern is not null)
            && (channel.PreReleaseTag is null || version.Split('-', 2).Skip(1).FirstOrDefault()?
                .Split('.').Contains(channel.PreReleaseTag, StringComparer.OrdinalIgnoreCase) == true)
            && (channel.BranchPattern is null || branch is not null && Regex.IsMatch(branch,
                "^" + Regex.Escape(channel.BranchPattern).Replace(@"\*", ".*") + "$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));

    public static ProjectVariableDefinition? ResolveVariable(ProjectDeploymentDefinition definition,
        string name, string environment, string channel)
        => definition.Variables.Where(variable => variable.Name == name
            && (variable.Environments.Count == 0 || variable.Environments.Contains(environment))
            && (variable.Channels.Count == 0 || variable.Channels.Contains(channel)))
            .OrderByDescending(ProjectDefinitionValidator.ScopeRank).FirstOrDefault();
}
