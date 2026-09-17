using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Serial collection for every suite that drives the Update Service against a
/// real <see cref="FakeStableCheckout"/>.
///
/// These suites are not unit tests: each one forks git and bash against a temp
/// working tree, boots two in-process Kestrel hosts, and then polls wall-clock
/// budgets (health wait, frontend wait, trigger timeout) for the result. Under
/// the Windows pre-develop gate the whole backend suite runs with default
/// collection parallelism, so up to <c>maxParallelThreads</c> other collections
/// compete for the same cores while those budgets are counting down, and the
/// process spawns that the assertions depend on get scheduled behind them.
///
/// Holding them in one collection with <c>DisableParallelization</c> keeps the
/// update-service suites off each other's back and off the rest of the
/// assembly's back, without quarantining anything: every case still runs, on
/// every host, with every assertion intact.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateServiceSerialCollection
{
    public const string Name = "UpdateServiceSerial";
}
