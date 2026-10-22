namespace AgentStudio.TaskServer.Contracts;

/// <summary>Resolve a variable for one channel and environment. Different scope kinds combine with AND.</summary>
public static class ProjectVariableScopePolicy
{
    public static ProjectVariableDefinition? Resolve(IEnumerable<ProjectVariableDefinition> variables,
        string name, string environment, string channel)
        => variables.Where(variable => variable.Name == name
                && (variable.Environments.Count == 0 || variable.Environments.Contains(environment, StringComparer.Ordinal))
                && (variable.Channels.Count == 0 || variable.Channels.Contains(channel, StringComparer.Ordinal)))
            .OrderByDescending(variable => (variable.Environments.Count > 0 ? 1 : 0)
                + (variable.Channels.Count > 0 ? 1 : 0))
            .ThenBy(variable => variable.Environments.Count == 0 ? int.MaxValue : variable.Environments.Count)
            .ThenBy(variable => variable.Channels.Count == 0 ? int.MaxValue : variable.Channels.Count)
            .FirstOrDefault();
}
