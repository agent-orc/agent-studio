using AgentStudio.Projects;
using AgentStudio.Security;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectExecutionSecurityPolicyTests
{
    [Theory]
    [InlineData(true, StudioRoles.Owner, "runner-a", "local", true)]
    [InlineData(true, StudioRoles.Operator, "runner-a", "local", false)]
    [InlineData(true, StudioRoles.Contributor, "runner-a", "local", false)]
    [InlineData(true, null, "runner-a", "local", false)]
    [InlineData(true, StudioRoles.Operator, "runner-a", "runner-b", true)]
    [InlineData(true, StudioRoles.Operator, "unassigned", "local", false)]
    [InlineData(true, StudioRoles.Owner, "unassigned", "local", true)]
    [InlineData(true, StudioRoles.Operator, "local", "local", true)]
    [InlineData(false, StudioRoles.Operator, "runner-a", "local", true)]
    public void Remote_execution_can_only_be_disabled_by_networked_owner(
        bool networked, string? role, string current, string target, bool expected)
        => Assert.Equal(expected, ProjectExecutionSecurityPolicy.MaySelectLocation(
            networked, role, current, target));
}
