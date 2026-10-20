namespace AgentStudio.TaskServer.Contracts;

/// <summary>Within one scope kind values are alternatives; across kinds both must match.</summary>
public static class ProjectVariableScope
{
    public static int Specificity(ProjectVariableDefinition variable)
        => (variable.Environments.Count > 0 ? 100 - variable.Environments.Count : 0)
           + (variable.Channels.Count > 0 ? 100 - variable.Channels.Count : 0);

    public static ProjectVariableDefinition? Resolve(IEnumerable<ProjectVariableDefinition> variables,
        string name, string environment, string channel)
        => variables.Where(variable => variable.Name == name
                && (variable.Environments.Count == 0 || variable.Environments.Contains(environment, StringComparer.Ordinal))
                && (variable.Channels.Count == 0 || variable.Channels.Contains(channel, StringComparer.Ordinal)))
            .OrderByDescending(Specificity).FirstOrDefault();
}
