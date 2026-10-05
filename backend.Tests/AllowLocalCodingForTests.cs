namespace AgentStudio.Tests;

/// <summary>Existing fake-CLI fixtures explicitly opt in to their simulated local execution.</summary>
internal sealed class AllowLocalCodingForTests : ILocalCodingAdmissionPolicy
{
    public static AllowLocalCodingForTests Instance { get; } = new();
    public bool AllowsLocalCoding => true;
}
