using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCaseOrderer(
    "AgentStudio.Tests.OrderOverrideTestCaseOrderer", "OrchestratorApi.Tests")]

namespace AgentStudio.Tests;

/// <summary>
/// Runs the tests of each class in reverse or shuffled order on request
/// (AGT-3416), so a class whose tests share one host (<see cref="SharedTestHost"/>)
/// can show that no test depends on what an earlier one left behind.
///
/// <para>Unset, it is xUnit's default order. <c>AGENT_STUDIO_TEST_ORDER=reverse</c>
/// reverses that order; <c>random:&lt;seed&gt;</c> shuffles it with the given
/// seed, so a failing order can be replayed.</para>
/// </summary>
public sealed class OrderOverrideTestCaseOrderer(IMessageSink diagnosticMessageSink) : ITestCaseOrderer
{
    public const string Variable = "AGENT_STUDIO_TEST_ORDER";

    private readonly DefaultTestCaseOrderer _default = new(diagnosticMessageSink);

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        var ordered = _default.OrderTestCases(testCases).ToList();
        var order = Environment.GetEnvironmentVariable(Variable);
        if (string.Equals(order, "reverse", StringComparison.OrdinalIgnoreCase))
        {
            ordered.Reverse();
            return ordered;
        }

        const string random = "random:";
        if (order is not null
            && order.StartsWith(random, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(order.AsSpan(random.Length), out var seed))
        {
            new Random(seed).Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ordered));
        }
        return ordered;
    }
}
