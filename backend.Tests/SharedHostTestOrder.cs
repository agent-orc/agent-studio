using Xunit.Abstractions;
using Xunit.Sdk;

namespace AgentStudio.Tests;

/// <summary>
/// Runs the tests of a class in xUnit's default order, in reverse, or shuffled with
/// a seed, chosen by <c>AGENT_STUDIO_TEST_ORDER</c>: unset, <c>reverse</c> or
/// <c>random:&lt;seed&gt;</c> (AGT-3415). A class on a <see cref="SharedHostFixture"/>
/// carries it so that its isolation is proven rather than assumed: a test that only
/// passes after another one fails in one of the other orders. An unknown value fails
/// the class instead of quietly running the default order.
/// <code>
/// AGENT_STUDIO_TEST_ORDER=reverse dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter "FullyQualifiedName~MyTests"
/// AGENT_STUDIO_TEST_ORDER=random:7 dotnet test ...
/// </code>
/// xUnit runs all rows of one method together, methods in the order their first
/// row appears. So the order is applied to methods, and to the rows within each:
/// reversing single rows would leave a theory with many rows near the front.
/// </summary>
public sealed class SharedHostTestOrder(IMessageSink diagnostics) : ITestCaseOrderer
{
    public const string TypeName = "AgentStudio.Tests.SharedHostTestOrder";
    public const string AssemblyName = "OrchestratorApi.Tests";
    public const string Variable = "AGENT_STUDIO_TEST_ORDER";

    private readonly DefaultTestCaseOrderer _default = new(diagnostics);

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
        => Arrange(_default.OrderTestCases(testCases).ToList(),
            testCase => testCase.TestMethod.TestClass.Class.Name + "." + testCase.TestMethod.Method.Name,
            Environment.GetEnvironmentVariable(Variable));

    internal static IReadOnlyList<T> Arrange<T>(IReadOnlyList<T> standard, Func<T, string> method, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return standard;
        var methods = standard.GroupBy(method, StringComparer.Ordinal).Select(rows => rows.ToArray()).ToArray();
        if (mode == "reverse")
            return methods.AsEnumerable().Reverse().SelectMany(rows => rows.AsEnumerable().Reverse()).ToList();
        if (mode.StartsWith("random:", StringComparison.Ordinal) && int.TryParse(mode["random:".Length..], out var seed))
        {
            var random = new Random(seed);
            random.Shuffle(methods);
            foreach (var rows in methods)
                random.Shuffle(rows);
            return methods.SelectMany(rows => rows).ToList();
        }
        throw new InvalidOperationException($"{Variable} must be unset, 'reverse' or 'random:<seed>', not '{mode}'.");
    }
}
