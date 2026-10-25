using Xunit;

namespace AgentStudio.Tests;

// This test changes process-wide ThreadPool limits and must run alone.
[CollectionDefinition("IamAuthorizationBurst", DisableParallelization = true)]
public sealed class IamAuthorizationBurstCollection { }
